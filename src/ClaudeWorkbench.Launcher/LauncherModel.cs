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
}

// Persisted launcher state (workspaces + settings), stored in
// %LOCALAPPDATA%\ClaudeWorkbench\Launcher\launcher.json.
public sealed class LauncherState
{
    public List<WorkspaceEntry> Workspaces { get; set; } = new();

    public string HostExePath { get; set; } = string.Empty;

    public string SidecarDirectory { get; set; } = string.Empty;

    public BrowserKind Browser { get; set; } = BrowserKind.Chrome;

    public string CustomBrowserPath { get; set; } = string.Empty;

    [JsonIgnore]
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeWorkbench",
        "Launcher");

    [JsonIgnore]
    public static string StatePath => Path.Combine(StateDirectory, "launcher.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LauncherState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                LauncherState? state = JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(StatePath), JsonOptions);
                if (state is not null)
                {
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
        fresh.ApplyDefaults();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    // Best-effort first-run guesses for this repo layout; the user can override in the UI.
    private void ApplyDefaults()
    {
        if (string.IsNullOrWhiteSpace(HostExePath))
        {
            HostExePath = GuessHostExe() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(SidecarDirectory))
        {
            SidecarDirectory = GuessSidecarDir() ?? string.Empty;
        }
    }

    private static string? GuessHostExe()
    {
        // Walk up from the launcher's own location looking for the built host exe.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "src", "ClaudeWorkbench.Host", "bin", "Debug", "net10.0", "ClaudeWorkbench.Host.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string? GuessSidecarDir()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "sidecar", "dist", "index.js");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir, "sidecar");
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
