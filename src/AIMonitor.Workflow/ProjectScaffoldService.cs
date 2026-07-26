using System.ComponentModel;
using System.Diagnostics;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Host-run scaffolding of a NEW C# project into the watched solution. This is the operator-driven
// counterpart to greenfield creation: the Launcher writes a blank .slnx, then this fills it (and any
// existing solution) with projects. It is NOT part of the agent surface — the agent never shells and
// never runs the SDK. The host invokes it from the Source tab's "Add project" dialog.
//
// Mechanism is the .NET SDK itself (`dotnet new` / `dotnet sln add`), driven out-of-process exactly
// like SolutionRestoreService drives `dotnet restore`. That gives us EVERY installed project template
// for free (enumerated at runtime, not hard-coded) and stays correct across SDK versions — the same
// reasoning that made the pre-merge validation build a real subprocess build rather than a hand-built
// in-memory model. C# only, by design.
public sealed class ProjectScaffoldService
{
    // A project template the installed SDK can create (e.g. "console" -> "Console App").
    public sealed record ProjectKind(string ShortName, string DisplayName);

    public sealed record ScaffoldRequest(
        string Kind,
        string ProjectName,
        string? TargetFramework,
        string? SubfolderRelative);

    public sealed record ScaffoldResult(
        bool IsError,
        string Message,
        string ProjectPath,
        string CsprojPath,
        IReadOnlyList<string> Diagnostics);

    // dotnet new list / --list-sdks are ~1-2s each; cache per process. The set only changes when the
    // operator installs an SDK or template pack, which doesn't happen mid-session.
    private IReadOnlyList<ProjectKind>? cachedKinds;
    private IReadOnlyList<string>? cachedFrameworks;
    private readonly object gate = new();

    // Sensible fallback if `dotnet new list` can't be parsed (format drift on some SDKs). These are all
    // in-box C# project templates, so `dotnet new <shortName>` still works even when enumeration didn't.
    private static readonly IReadOnlyList<ProjectKind> FallbackKinds =
    [
        new("classlib", "Class Library"),
        new("console", "Console App"),
        new("worker", "Worker Service"),
        new("web", "ASP.NET Core Empty"),
        new("webapi", "ASP.NET Core Web API"),
        new("mvc", "ASP.NET Core Web App (MVC)"),
        new("blazor", "Blazor Web App"),
        new("razorclasslib", "Razor Class Library"),
        new("winforms", "Windows Forms App"),
        new("wpf", "WPF Application"),
        new("xunit", "xUnit Test Project"),
        new("nunit", "NUnit Test Project"),
        new("mstest", "MSTest Test Project"),
    ];

    public IReadOnlyList<ProjectKind> ListProjectKinds()
    {
        lock (gate)
        {
            if (cachedKinds is not null)
            {
                return cachedKinds;
            }
        }

        IReadOnlyList<ProjectKind> kinds = QueryProjectKinds();
        lock (gate)
        {
            cachedKinds ??= kinds;
            return cachedKinds;
        }
    }

    public IReadOnlyList<string> ListTargetFrameworks()
    {
        lock (gate)
        {
            if (cachedFrameworks is not null)
            {
                return cachedFrameworks;
            }
        }

        IReadOnlyList<string> frameworks = QueryTargetFrameworks();
        lock (gate)
        {
            cachedFrameworks ??= frameworks;
            return cachedFrameworks;
        }
    }

    private IReadOnlyList<ProjectKind> QueryProjectKinds()
    {
        ProcessResult result = RunProcess(
            "dotnet",
            ["new", "list", "--type", "project", "--language", "C#"],
            Environment.CurrentDirectory,
            TimeSpan.FromMinutes(1));
        if (result.LaunchFailed || result.TimedOut || result.ExitCode != 0)
        {
            // No dotnet / enumeration failed: degrade to the curated in-box list so the dialog still
            // opens. Create() surfaces the real "SDK not found" error if they try to use it.
            return FallbackKinds;
        }

        List<ProjectKind> parsed = ParseTemplateTable(result.StandardOutput);
        return parsed.Count > 0 ? parsed : FallbackKinds;
    }

    // `dotnet new list` prints a fixed-width column table. Anchor on the header row's column positions
    // ("Template Name" / "Short Name" / "Language") so we survive width changes across SDK versions.
    private static List<ProjectKind> ParseTemplateTable(string output)
    {
        List<ProjectKind> kinds = [];
        string[] lines = output.Replace("\r", string.Empty).Split('\n');

        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Template Name", StringComparison.Ordinal)
                && lines[i].Contains("Short Name", StringComparison.Ordinal))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0 || headerIndex + 2 >= lines.Length)
        {
            return kinds;
        }

        string header = lines[headerIndex];
        int nameStart = header.IndexOf("Template Name", StringComparison.Ordinal);
        int shortStart = header.IndexOf("Short Name", StringComparison.Ordinal);
        int langStart = header.IndexOf("Language", StringComparison.Ordinal);
        if (nameStart < 0 || shortStart <= nameStart || langStart <= shortStart)
        {
            return kinds;
        }

        // Row after the header is the dashed separator; data begins after it.
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = headerIndex + 2; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            if (line.Length <= shortStart)
            {
                continue;
            }

            string displayName = Slice(line, nameStart, shortStart).Trim();
            string shortCell = Slice(line, shortStart, langStart).Trim();
            if (string.IsNullOrWhiteSpace(shortCell) || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            // A row can list multiple short names (comma-separated); the first is canonical.
            string shortName = shortCell.Split(',')[0].Trim();
            if (shortName.Length == 0 || !seen.Add(shortName))
            {
                continue;
            }

            kinds.Add(new ProjectKind(shortName, displayName));
        }

        return kinds
            .OrderBy(kind => kind.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return string.Empty;
        }

        int safeEnd = Math.Min(end, line.Length);
        return line[start..safeEnd];
    }

    // Offer one entry per installed SDK major (net8.0, net9.0, net10.0, ...), newest first. Templates
    // accept whatever `-f` value we pass here; offering only installed majors keeps the list honest.
    private IReadOnlyList<string> QueryTargetFrameworks()
    {
        ProcessResult result = RunProcess(
            "dotnet",
            ["--list-sdks"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30));
        if (result.LaunchFailed || result.TimedOut || result.ExitCode != 0)
        {
            return ["net10.0", "net9.0", "net8.0"];
        }

        SortedSet<int> majors = [];
        foreach (string line in result.StandardOutput.Replace("\r", string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Line form: "10.0.100 [C:\Program Files\dotnet\sdk]"
            string version = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            string majorText = version.Split('.')[0];
            if (int.TryParse(majorText, out int major) && major >= 5)
            {
                majors.Add(major);
            }
        }

        if (majors.Count == 0)
        {
            return ["net10.0", "net9.0", "net8.0"];
        }

        return majors
            .OrderByDescending(major => major)
            .Select(major => $"net{major}.0")
            .ToList();
    }

    public ScaffoldResult Create(MonitorSettings settings, ScaffoldRequest request, TimeSpan? timeout = null)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return Error("The watched solution file is missing.", []);
        }

        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;

        string? nameError = ValidateName(request.ProjectName);
        if (nameError is not null)
        {
            return Error(nameError, []);
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            return Error("Pick a project type.", []);
        }

        // Containment: the new project must live inside the solution root (the greenfield contract —
        // "any added projects need to live within it"). Reject traversal or absolute escapes.
        string relativeDirectory = string.IsNullOrWhiteSpace(request.SubfolderRelative)
            ? request.ProjectName
            : Path.Combine(request.SubfolderRelative.Trim(), request.ProjectName);
        string targetDirectory = Path.GetFullPath(Path.Combine(solutionRoot, relativeDirectory));
        string rootWithSeparator = solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!targetDirectory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return Error("The project must live inside the solution folder.", []);
        }

        if (Directory.Exists(targetDirectory)
            && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            return Error($"A non-empty folder already exists at {targetDirectory}.", []);
        }

        TimeSpan runTimeout = timeout ?? TimeSpan.FromMinutes(5);

        // 1) Scaffold the project. `dotnet new` restores the new project as part of creation.
        List<string> newArgs = ["new", request.Kind, "-n", request.ProjectName, "-o", targetDirectory];
        if (!string.IsNullOrWhiteSpace(request.TargetFramework))
        {
            newArgs.Add("-f");
            newArgs.Add(request.TargetFramework.Trim());
        }

        ProcessResult created = RunProcess("dotnet", newArgs, solutionRoot, runTimeout);
        if (created.LaunchFailed)
        {
            return Error(
                "The .NET SDK ('dotnet') wasn't found on PATH. Install the .NET 10 SDK and make sure "
                    + "'dotnet' runs from a terminal, then retry.",
                []);
        }

        if (created.TimedOut || created.ExitCode != 0)
        {
            return Error(
                created.TimedOut ? "dotnet new timed out." : "dotnet new failed to create the project.",
                ExtractDiagnostics(created));
        }

        string csprojPath = Path.Combine(targetDirectory, request.ProjectName + ".csproj");
        if (!File.Exists(csprojPath))
        {
            // Some templates name the project differently; find the produced .csproj.
            csprojPath = Directory.Exists(targetDirectory)
                ? Directory.EnumerateFiles(targetDirectory, "*.csproj", SearchOption.AllDirectories).FirstOrDefault() ?? csprojPath
                : csprojPath;
        }

        if (!File.Exists(csprojPath))
        {
            return Error("Project was created but no .csproj was found to add to the solution.", ExtractDiagnostics(created));
        }

        // 2) Register it in the .slnx (SDK writes the <Project Path="..."/> entry, forward slashes, no GUID).
        ProcessResult added = RunProcess(
            "dotnet",
            ["sln", solutionPath, "add", csprojPath],
            solutionRoot,
            runTimeout);
        if (added.TimedOut || added.ExitCode != 0)
        {
            return new ScaffoldResult(
                true,
                added.TimedOut
                    ? "Project created, but adding it to the solution timed out."
                    : "Project created, but adding it to the solution failed.",
                targetDirectory,
                csprojPath,
                ExtractDiagnostics(added));
        }

        // 3) Restore the whole solution so the index's design-time load sees restored assets. Cheap when
        //    already current; `dotnet new` restored the new project, this stitches it into the solution.
        ProcessResult restored = RunProcess(
            "dotnet",
            ["restore", solutionPath, "--nologo", "-nodeReuse:false"],
            solutionRoot,
            runTimeout);
        if (restored.TimedOut || restored.ExitCode != 0)
        {
            // Non-fatal: the project exists and is in the solution; the reindex/restore can be retried.
            return new ScaffoldResult(
                false,
                $"Created {request.ProjectName} and added it to the solution (restore reported issues — a rebuild may be needed).",
                targetDirectory,
                csprojPath,
                ExtractDiagnostics(restored));
        }

        return new ScaffoldResult(
            false,
            $"Created {request.ProjectName} and added it to the solution.",
            targetDirectory,
            csprojPath,
            []);
    }

    private static ScaffoldResult Error(string message, IReadOnlyList<string> diagnostics)
    {
        return new ScaffoldResult(true, message, string.Empty, string.Empty, diagnostics);
    }

    // Keep project names to a portable, MSBuild-safe shape: start with a letter or underscore, then
    // letters/digits/._- . This is stricter than the SDK allows but avoids surprising path/identifier
    // problems in a governed workspace.
    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a project name.";
        }

        string trimmed = name.Trim();
        if (!char.IsLetter(trimmed[0]) && trimmed[0] != '_')
        {
            return "The name must start with a letter or underscore.";
        }

        foreach (char character in trimmed)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character != '-')
            {
                return "Use only letters, digits, '.', '_' or '-' in the name.";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractDiagnostics(ProcessResult result)
    {
        string output = string.Join(Environment.NewLine, [result.StandardOutput, result.StandardError]);
        string[] diagnostics = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No templates", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("NU1", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        if (diagnostics.Length == 0)
        {
            // Surface the tail of stderr/stdout so a bare non-zero exit isn't a silent failure.
            string fallback = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            diagnostics = fallback
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(6)
                .ToArray();
        }

        return diagnostics;
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // `dotnet` isn't on PATH (or otherwise couldn't launch): Start() throws BEFORE any exit code
            // exists. Signal a launch failure instead of letting it bubble into the Blazor circuit — the
            // caller turns this into a clear "install the SDK" message.
            return new ProcessResult(-1, false, true, string.Empty,
                $"Could not start '{fileName}'. Is the .NET SDK installed and on PATH? ({ex.Message})");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeout);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return new ProcessResult(
            exited ? process.ExitCode : -1,
            !exited,
            false,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private sealed record ProcessResult(int ExitCode, bool TimedOut, bool LaunchFailed, string StandardOutput, string StandardError);
}
