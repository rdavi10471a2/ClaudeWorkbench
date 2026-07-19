using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWorkbench.Launcher;

public enum BrowserKind
{
    Chrome,
    Edge,
    Custom,       // CustomBrowserPath, launched with Chromium --app flags
    DefaultShell, // OS default browser; cannot be job-controlled (no distinct process)
}

// One configured workspace the launcher can start. Ports are assigned at start time
// (not persisted) so they stay free between runs.
public sealed class WorkspaceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    // The watched .sln/.slnx.
    public string SolutionPath { get; set; } = string.Empty;

    // Folder under InstancesRoot holding this workspace's config, runtime and host.log.
    // Claimed from the name on first start and then sticky: renaming a workspace (or adding
    // a second one with the same name) must never point an instance at someone else's state.
    public string InstanceFolder { get; set; } = string.Empty;
}

// Persisted launcher state (workspaces + settings), stored in
// %LOCALAPPDATA%\ClaudeWorkbench\Launcher\launcher.json.
public sealed class LauncherState
{
    public List<WorkspaceEntry> Workspaces { get; set; } = new();

    public string HostExePath { get; set; } = string.Empty;

    public string SidecarDirectory { get; set; } = string.Empty;

    // Explicit override for where instances live. Normally empty: the default is derived at
    // run time (<workbench root>\runtime\<workspace>), so it follows the workbench even when
    // the launcher is a desktop shortcut sitting somewhere else entirely.
    public string InstancesRoot { get; set; } = string.Empty;

    // Last known absolute location of the workbench. Stored (and deliberately NOT made
    // relative — it is what relative paths are measured against) so a launcher that gets
    // copied out of the workbench can still resolve them.
    public string WorkbenchRootHint { get; set; } = string.Empty;

    public BrowserKind Browser { get; set; } = BrowserKind.Chrome;

    public string CustomBrowserPath { get; set; } = string.Empty;

    private static string? stateDirectory;

    // State lives IN THE INSTALL (<workbench root>\launcher.json), not in %LOCALAPPDATA% -
    // the whole point of a published folder is that copying it takes the workspace list with
    // it. Only a read-only install (Program Files, a network share) falls back to per-user state.
    [JsonIgnore]
    public static string StateDirectory => stateDirectory ??= ResolveStateDirectory();

    [JsonIgnore]
    public static string StatePath => Path.Combine(StateDirectory, StateFileName);

    private const string StateFileName = "launcher.json";

    private static string ResolveStateDirectory()
    {
        string candidate = WalkUpToRoot(AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        return IsWritable(candidate) ? candidate : PerUserStateDirectory;
    }

    // The pre-portable location. Still read once, so an existing install keeps its workspaces;
    // the next Save writes to the install folder instead.
    [JsonIgnore]
    private static string PerUserStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeWorkbench",
        "Launcher");

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Where instances lived before they were named by workspace: %LOCALAPPDATA%\...\instances\<guid>.
    [JsonIgnore]
    private static string LegacyInstancesRoot => Path.Combine(PerUserStateDirectory, "instances");

    // Provisioning goes to <workbench root>\runtime — the same place a directly-run host puts
    // it — unless the user overrode it. Only if no workbench can be located at all (the
    // launcher hasn't been pointed at a host exe yet) does it fall back to %LOCALAPPDATA%.
    [JsonIgnore]
    public string DefaultInstancesRoot =>
        WorkbenchRoot is not null ? Path.Combine(WorkbenchRoot, "runtime") : LegacyInstancesRoot;

    [JsonIgnore]
    public string EffectiveInstancesRoot =>
        string.IsNullOrWhiteSpace(InstancesRoot) ? DefaultInstancesRoot : InstancesRoot;

    // The directory holding this workspace's instance state, creating (and on first use
    // claiming) it. The claim is persisted so the folder survives a later rename.
    public string InstanceDirectoryFor(WorkspaceEntry workspace)
    {
        string root = EffectiveInstancesRoot;
        if (string.IsNullOrWhiteSpace(workspace.InstanceFolder))
        {
            workspace.InstanceFolder = ClaimFolderName(workspace);
            MigrateLegacyFolder(workspace, root);
            if (Workspaces.Contains(workspace))
            {
                Save(); // a transient workspace (self-test) claims a name but isn't persisted
            }
        }

        string dir = Path.Combine(root, workspace.InstanceFolder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A legible folder name derived from the workspace name, unique among the workspaces
    // that have already claimed one.
    private string ClaimFolderName(WorkspaceEntry workspace)
    {
        string baseName = SanitizeFolderName(workspace.Name);
        if (baseName.Length == 0)
        {
            baseName = SanitizeFolderName(Path.GetFileNameWithoutExtension(workspace.SolutionPath));
        }

        if (baseName.Length == 0)
        {
            baseName = "workspace";
        }

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceEntry other in Workspaces)
        {
            if (!ReferenceEquals(other, workspace) && !string.IsNullOrWhiteSpace(other.InstanceFolder))
            {
                taken.Add(other.InstanceFolder);
            }
        }

        string candidate = baseName;
        for (int suffix = 2; taken.Contains(candidate); suffix++)
        {
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    // Carry state written by an older build (instances\<guid>) over to the named folder, so
    // an upgrade keeps its index and config instead of silently starting from scratch.
    private static void MigrateLegacyFolder(WorkspaceEntry workspace, string root)
    {
        try
        {
            string legacy = Path.Combine(LegacyInstancesRoot, workspace.Id);
            string target = Path.Combine(root, workspace.InstanceFolder);
            if (Directory.Exists(legacy) && !Directory.Exists(target))
            {
                Directory.CreateDirectory(root);
                Directory.Move(legacy, target);
            }
        }
        catch
        {
            // Best effort: a locked or cross-volume legacy folder just means a fresh start.
        }
    }

    // Windows is fussier than "no invalid chars": trailing dots/spaces are silently stripped
    // by the filesystem and device names (CON, LPT1, …) can't be directory names at all.
    private static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length > 64)
        {
            name = name[..64].TrimEnd('.', ' ');
        }

        string stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            name = "_" + name;
        }

        return name;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LauncherState Load()
    {
        // Prefer the install's own file; otherwise adopt any per-user state left by an older
        // build, which the next Save then migrates into the install.
        string path = StatePath;
        if (!File.Exists(path))
        {
            string perUser = Path.Combine(PerUserStateDirectory, StateFileName);
            if (File.Exists(perUser))
            {
                path = perUser;
            }
        }

        try
        {
            if (File.Exists(path))
            {
                LauncherState? state = JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(path), JsonOptions);
                if (state is not null)
                {
                    state.Reanchor();
                    state.ResolvePaths();
                    state.ApplyDefaults();
                    return state;
                }
            }
        }
        catch
        {
            // Fall through to defaults on a corrupt file.
        }

        LauncherState fresh = new();
        fresh.Reanchor();
        fresh.ApplyDefaults();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(ToPortable(), JsonOptions));
    }

    // The ClaudeWorkbench folder this launcher drives — the one holding src\, sidecar\ and
    // runtime\ (or, for a published build, the folder the host exe sits in). Everything the
    // launcher points at lives under it, so it is the anchor for every stored path.
    //
    // The launcher's OWN location is only a hint: it may be a desktop shortcut, a Release
    // build, or a publish folder outside the workbench entirely. So the host exe wins as the
    // locator, and the launcher's location is the fallback for the in-checkout dev case.
    [JsonIgnore]
    public string? WorkbenchRoot { get; private set; }

    // Recompute the anchor. Called on load and whenever the host exe changes in Settings, so
    // pointing the launcher at a different workbench moves everything with it.
    public void Reanchor() => WorkbenchRoot = FindWorkbenchRoot(HostExePath, WorkbenchRootHint);

    private static string? FindWorkbenchRoot(string hostExeHint, string storedRootHint)
    {
        // A host exe we've been pointed at locates the workbench regardless of where the
        // launcher lives. Its path is stored absolute whenever it sits outside our anchor,
        // so this works before any relative path has been resolved.
        if (!string.IsNullOrWhiteSpace(hostExeHint) && Path.IsPathRooted(hostExeHint) && File.Exists(hostExeHint))
        {
            string? hostDirectory = Path.GetDirectoryName(hostExeHint);
            string? fromHost = WalkUpToRoot(hostDirectory);
            if (fromHost is not null)
            {
                return fromHost;
            }

            // A published host: no checkout above it, so it is its own workbench root.
            if (Directory.Exists(hostDirectory))
            {
                return hostDirectory;
            }
        }

        // The launcher is running from inside a checkout.
        string? fromLauncher = WalkUpToRoot(AppContext.BaseDirectory);
        if (fromLauncher is not null)
        {
            return fromLauncher;
        }

        // Last resort: where the workbench was the last time we saw it. This is what keeps a
        // launcher copied out to the desktop able to resolve its relative paths.
        return Directory.Exists(storedRootHint) ? storedRootHint : null;
    }

    // Walk up looking for a workbench marker. A checkout is recognised by the solution file
    // or the Host project folder (so this works from a bin directory as well as the root);
    // a published workbench by its host\ folder. Either way the answer is the folder that
    // owns runtime\.
    private static string? WalkUpToRoot(string? startDirectory)
    {
        string? dir = startDirectory;
        for (int depth = 0; depth < 10 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir, "ClaudeWorkbench.slnx"))
                || Directory.Exists(Path.Combine(dir, "src", "ClaudeWorkbench.Host"))
                || File.Exists(Path.Combine(dir, "host", HostExeName)))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private const string HostExeName = "ClaudeWorkbench.Host.exe";

    // Stored form -> absolute. A relative entry is anchored to the workbench root, so moving
    // or renaming the workbench doesn't strand the state (launcher.json lives outside it, in
    // %LOCALAPPDATA%, and used to keep absolute paths into whatever checkout wrote it).
    private string Resolve(string stored) =>
        string.IsNullOrWhiteSpace(stored) || WorkbenchRoot is null || Path.IsPathRooted(stored)
            ? stored
            : Path.GetFullPath(Path.Combine(WorkbenchRoot, stored));

    // Absolute -> stored form. Paths inside the workbench are stored relative to it; anything
    // outside (a watched solution elsewhere on disk) stays absolute.
    private string Portable(string absolute)
    {
        if (string.IsNullOrWhiteSpace(absolute) || WorkbenchRoot is null || !Path.IsPathRooted(absolute))
        {
            return absolute;
        }

        string relative = Path.GetRelativePath(WorkbenchRoot, absolute);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            ? absolute
            : relative;
    }

    private void ResolvePaths()
    {
        HostExePath = Resolve(HostExePath);
        SidecarDirectory = Resolve(SidecarDirectory);
        InstancesRoot = Resolve(InstancesRoot);
        foreach (WorkspaceEntry workspace in Workspaces)
        {
            workspace.SolutionPath = Resolve(workspace.SolutionPath);
        }
    }

    private LauncherState ToPortable() => new()
    {
        HostExePath = Portable(HostExePath),
        SidecarDirectory = Portable(SidecarDirectory),
        InstancesRoot = Portable(InstancesRoot),
        WorkbenchRootHint = WorkbenchRoot ?? WorkbenchRootHint,
        Browser = Browser,
        CustomBrowserPath = Portable(CustomBrowserPath),
        Workspaces = Workspaces.Select(w => new WorkspaceEntry
        {
            Id = w.Id,
            Name = w.Name,
            SolutionPath = Portable(w.SolutionPath),
            InstanceFolder = w.InstanceFolder,
        }).ToList(),
    };

    // Best-effort guesses; the user can override in Settings. A stored path that no longer
    // exists is re-guessed rather than kept, so state written by an older build (or pointing
    // at a workbench that has since moved) heals itself on load. InstancesRoot is deliberately
    // NOT defaulted here — leaving it empty keeps it tracking the workbench root.
    private void ApplyDefaults()
    {
        if (string.IsNullOrWhiteSpace(HostExePath) || !File.Exists(HostExePath))
        {
            HostExePath = GuessHostExe() ?? HostExePath;
            Reanchor(); // a freshly-found host exe may be what locates the workbench
        }

        if (string.IsNullOrWhiteSpace(SidecarDirectory) || !Directory.Exists(SidecarDirectory))
        {
            SidecarDirectory = GuessSidecarDir() ?? SidecarDirectory;
        }

        // An earlier build stored <workbench>\runtime-launcher as an explicit root. Drop it so
        // it reverts to the default and tracks the workbench again.
        if (WorkbenchRoot is not null
            && string.Equals(InstancesRoot, Path.Combine(WorkbenchRoot, "runtime-launcher"), StringComparison.OrdinalIgnoreCase))
        {
            InstancesRoot = string.Empty;
        }
    }

    // Prefer a host shipped alongside the launcher (a publish, or a desktop install), then
    // any build inside the workbench — newest wins, Release ahead of Debug, so this doesn't
    // go stale against a specific configuration or target framework.
    private string? GuessHostExe()
    {
        // Published flat: the host sits next to the launcher.
        string sibling = Path.Combine(AppContext.BaseDirectory, HostExeName);
        if (File.Exists(sibling))
        {
            return Path.GetFullPath(sibling);
        }

        if (WorkbenchRoot is null)
        {
            return null;
        }

        // Published workbench: <root>\host\ alongside <root>\sidecar\ and <root>\runtime\.
        string published = Path.Combine(WorkbenchRoot, "host", HostExeName);
        if (File.Exists(published))
        {
            return published;
        }

        string binRoot = Path.Combine(WorkbenchRoot, "src", "ClaudeWorkbench.Host", "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(binRoot, HostExeName, SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null; // an unreadable bin tree just means "no guess"
        }
    }

    // <workbench>\sidecar, or one shipped next to the launcher itself.
    private string? GuessSidecarDir()
    {
        foreach (string? root in new[] { WorkbenchRoot, AppContext.BaseDirectory })
        {
            if (root is null)
            {
                continue;
            }

            string candidate = Path.Combine(root, "sidecar");
            if (File.Exists(Path.Combine(candidate, "dist", "index.js")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
