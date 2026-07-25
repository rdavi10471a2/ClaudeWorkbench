using System.Diagnostics;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Host-run `dotnet restore` of the watched solution. NuGet restore is not part of the design-time
// MSBuildWorkspace load the indexer uses, and the pre-merge build restores only its private
// validation mirror — so a package a caller added to a real .csproj (or a freshly-opened
// package-bearing project) has no restored assets on the REAL source until this runs. The agent
// never shells: this is invoked by the host (workspace open / post-accept reindex) and exposed as a
// governed MCP tool. Restore is a no-op when assets are already current, so it is cheap to call.
public sealed class SolutionRestoreService
{
    public sealed record RestoreResult(
        string Status,
        bool IsError,
        string SolutionPath,
        int DiagnosticCount,
        IReadOnlyList<string> Diagnostics,
        string Message);

    public RestoreResult Restore(MonitorSettings settings, TimeSpan? timeout = null)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return new RestoreResult(
                "missing-solution",
                true,
                solutionPath,
                0,
                [],
                "Restore skipped because the watched solution file is missing.");
        }

        // Run from the solution root so any Directory.Build.props / NuGet.config / global.json beside
        // or above the solution are honoured exactly as a normal `dotnet restore` would.
        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;
        ProcessResult result = RunProcess(
            "dotnet",
            // -nodeReuse:false: don't leave MSBuild worker nodes pinning files after restore (matches
            // the pre-merge build; see the file-locking diagnosis).
            ["restore", solutionPath, "--nologo", "-nodeReuse:false"],
            solutionRoot,
            timeout ?? TimeSpan.FromMinutes(5));

        string output = string.Join(Environment.NewLine, [result.StandardOutput, result.StandardError]);
        string[] diagnostics = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("NU1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        bool failed = result.TimedOut || result.ExitCode != 0;
        if (failed && diagnostics.Length == 0)
        {
            diagnostics = [$"dotnet restore exited with code {result.ExitCode} but emitted no parseable diagnostics."];
        }

        return new RestoreResult(
            result.TimedOut ? "timeout" : failed ? "failed" : "restored",
            failed,
            solutionPath,
            diagnostics.Length,
            diagnostics,
            result.TimedOut
                ? "dotnet restore timed out."
                : failed
                    ? "dotnet restore failed."
                    : "dotnet restore completed (packages are up to date).");
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
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
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
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private sealed record ProcessResult(int ExitCode, bool TimedOut, string StandardOutput, string StandardError);
}
