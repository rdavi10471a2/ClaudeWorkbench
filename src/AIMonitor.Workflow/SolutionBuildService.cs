using System.Diagnostics;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Host-run `dotnet build` of the watched solution that produces REAL output in the project's own
// bin/<config>. Distinct from PreMergeValidationService, which builds a throwaway mirror to GATE an
// edit — this compiles the accepted, in-place source so the operator has a runnable artifact.
// Operator-driven (post-accept build, or a manual Build/Run action); the agent never shells the SDK.
// Runs out-of-process like SolutionRestoreService, and is safe to sequence after the reindex (they run
// serially, so no concurrent MSBuild handles contend on the watched tree).
public sealed class SolutionBuildService
{
    public sealed record BuildResult(
        string Status,
        bool IsError,
        string Configuration,
        string SolutionPath,
        int DiagnosticCount,
        IReadOnlyList<string> Diagnostics,
        long DurationMs,
        string Message);

    public BuildResult Build(MonitorSettings settings, string configuration = "Debug", TimeSpan? timeout = null)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        string safeConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        if (!File.Exists(solutionPath))
        {
            return new BuildResult(
                "missing-solution",
                true,
                safeConfiguration,
                solutionPath,
                0,
                [],
                0,
                "Build skipped because the watched solution file is missing.");
        }

        // Run from the solution root so Directory.Build.props / global.json above the solution apply,
        // exactly as a normal `dotnet build` would.
        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;

        // ADR-0007: the index rides the build — so EVERY build emits every project's generated .g.cs and dumps
        // each project's resolved refs into its own obj (the shared Core per-project target,
        // AfterTargets=ResolveReferences), so a reindex can READ this build's whole-solution output instead of
        // running its own compile. No flag: the ordering is the behaviour. Cheap on an up-to-date build (the
        // dump runs at ResolveReferences even when CoreCompile is skipped).
        string rideTargetsFile = IndexRidesBuild.WritePerProjectRefsTargetsFile();
        List<string> buildArguments =
            // -nodeReuse:false: don't leave MSBuild worker nodes pinning files after the build (matches
            // the restore/validation services; see the file-locking diagnosis).
            [
                "build", solutionPath, "-c", safeConfiguration, "--nologo", "-nodeReuse:false",
                "-p:EmitCompilerGeneratedFiles=true",
                $"-p:CustomAfterMicrosoftCommonTargets={rideTargetsFile}"
            ];

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessResult result = RunProcess(
            "dotnet",
            buildArguments,
            solutionRoot,
            timeout ?? TimeSpan.FromMinutes(10));
        stopwatch.Stop();

        try { Directory.Delete(Path.GetDirectoryName(rideTargetsFile)!, recursive: true); } catch { /* best-effort */ }

        if (result.LaunchFailed)
        {
            return new BuildResult(
                "no-sdk",
                true,
                safeConfiguration,
                solutionPath,
                1,
                ["The .NET SDK ('dotnet') wasn't found on PATH. Install the .NET SDK to build."],
                stopwatch.ElapsedMilliseconds,
                "Build skipped — the .NET SDK ('dotnet') was not found on PATH.");
        }

        string output = string.Join(Environment.NewLine, [result.StandardOutput, result.StandardError]);
        string[] diagnostics = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        bool failed = result.TimedOut || result.ExitCode != 0;
        if (failed && diagnostics.Length == 0)
        {
            diagnostics = [$"dotnet build exited with code {result.ExitCode} but emitted no parseable error diagnostics."];
        }

        return new BuildResult(
            result.TimedOut ? "timeout" : failed ? "failed" : "built",
            failed,
            safeConfiguration,
            solutionPath,
            diagnostics.Length,
            diagnostics,
            stopwatch.ElapsedMilliseconds,
            result.TimedOut
                ? $"dotnet build ({safeConfiguration}) timed out."
                : failed
                    ? $"dotnet build ({safeConfiguration}) failed."
                    : $"Built {safeConfiguration} output.");
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ProcessResult(-1, false, true, string.Empty, ex.Message);
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
