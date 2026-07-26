using System.Diagnostics;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Launches the built executable of the watched solution — the "run" half of build & run. Capability,
// not a workflow phase: an operator "Run" button and the optional run-after-accept both call it. Finds
// the one executable project (OutputType Exe/WinExe), locates its built exe under bin/<config>, and
// starts it detached. Requires a prior build (it runs the artifact, it does not compile). Operator-only;
// the agent never launches processes.
public sealed class SolutionRunService
{
    // The most recently launched app, so a re-run stops the previous instance instead of piling up
    // windows across accepts. Process lives for the Host's lifetime; static is the right scope.
    private static Process? lastLaunched;
    private static readonly object gate = new();

    public sealed record RunResult(bool IsError, string Message, string? ExecutablePath);

    public RunResult Run(MonitorSettings settings, string configuration)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return new RunResult(true, "Run skipped — the watched solution file is missing.", null);
        }

        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;
        string config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();

        List<string> runnable = FindRunnableProjects(solutionRoot);
        if (runnable.Count == 0)
        {
            return new RunResult(true, "Nothing to run — no project with OutputType Exe/WinExe.", null);
        }

        if (runnable.Count > 1)
        {
            return new RunResult(
                true,
                $"Multiple executable projects ({runnable.Count}) — pick one from the Source tab's Run action.",
                null);
        }

        string project = runnable[0];
        string projectDirectory = Path.GetDirectoryName(project) ?? solutionRoot;
        string projectName = Path.GetFileNameWithoutExtension(project);
        string binConfig = Path.Combine(projectDirectory, "bin", config);
        if (!Directory.Exists(binConfig))
        {
            return new RunResult(true, $"No {config} build output for {projectName} — build first.", null);
        }

        // The exe lives under bin/<config>/<tfm>/; the tfm folder varies (net10.0, net8.0-windows, …),
        // so search and take the freshest match.
        string? executable = Directory
            .EnumerateFiles(binConfig, projectName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (executable is null)
        {
            return new RunResult(true, $"No built {projectName}.exe found under bin/{config} — build first.", null);
        }

        try
        {
            lock (gate)
            {
                if (lastLaunched is { HasExited: false })
                {
                    try
                    {
                        lastLaunched.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // Best-effort: the previous instance may already be gone or unkillable.
                    }
                }

                lastLaunched = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? projectDirectory,
                });
            }

            return new RunResult(false, $"Launched {projectName} ({config}).", executable);
        }
        catch (Exception ex)
        {
            return new RunResult(true, $"Could not launch {projectName}: {ex.Message}", executable);
        }
    }

    // A project is runnable if its .csproj declares an executable OutputType. Cheap text check — good
    // enough to distinguish apps from libraries without a full MSBuild evaluation.
    private static List<string> FindRunnableProjects(string solutionRoot)
    {
        List<string> runnable = new();
        foreach (string csproj in Directory.EnumerateFiles(solutionRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(solutionRoot, csproj);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(csproj);
            }
            catch (Exception)
            {
                continue;
            }

            if (text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase))
            {
                runnable.Add(csproj);
            }
        }

        return runnable;
    }
}
