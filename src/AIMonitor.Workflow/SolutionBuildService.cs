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
        string Message)
    {
        // ADR-0007 build-output handoff (populated only when the build rode the flag). The reindex reads these
        // instead of running its OWN compile: GeneratedRoot is the project's obj/<cfg>/<tfm>/generated dir (the
        // build's accurate razor .g.cs), HarvestedReferences is the resolved reference set the compile used, and
        // RidesBuildProject is the single project those outputs belong to. Null/empty on the ordinary build path.
        public string? RidesBuildProject { get; init; }

        public string? GeneratedRoot { get; init; }

        public IReadOnlyList<string> HarvestedReferences { get; init; } = [];
    }

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

        // ADR-0007: when the index rides the build AND the watched solution is the single project the read
        // path supports, this build ALSO emits the generated .g.cs and dumps the resolved reference set — so
        // the reindex can read this build's output instead of running its own compile. On the ordinary path
        // (flag off, or multi-project) none of this happens and the build args are exactly as before.
        string? rideBuildProject = IndexRidesBuild.Enabled
            ? WatchedSolutionInfo.ResolveSingleProject(solutionPath)
            : null;
        string? scratchDirectory = null;
        string? refsDumpPath = null;
        List<string> buildArguments =
            // -nodeReuse:false: don't leave MSBuild worker nodes pinning files after the build (matches
            // the restore/validation services; see the file-locking diagnosis).
            ["build", solutionPath, "-c", safeConfiguration, "--nologo", "-nodeReuse:false"];
        if (rideBuildProject is not null)
        {
            scratchDirectory = Path.Combine(Path.GetTempPath(), "AIMonitorPostAcceptBuild", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratchDirectory);
            string targetsPath = Path.Combine(scratchDirectory, "dumprefs.targets");
            refsDumpPath = Path.Combine(scratchDirectory, "refs.txt");
            File.WriteAllText(targetsPath, """
                <Project>
                  <Target Name="DumpRefsForIndex" BeforeTargets="CoreCompile">
                    <WriteLinesToFile File="$(RefsDumpPath)" Lines="@(ReferencePathWithRefAssemblies)" Overwrite="true" />
                  </Target>
                </Project>
                """);
            buildArguments.Add("-p:EmitCompilerGeneratedFiles=true");
            buildArguments.Add($"-p:CustomAfterMicrosoftCommonTargets={targetsPath}");
            buildArguments.Add($"-p:RefsDumpPath={refsDumpPath}");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessResult result = RunProcess(
            "dotnet",
            buildArguments,
            solutionRoot,
            timeout ?? TimeSpan.FromMinutes(10));
        stopwatch.Stop();

        // Harvest the build-output handoff for the reindex before the scratch dumprefs file is cleaned up.
        // The generated root lives in the real project's obj/ (it persists — that is the point); only the
        // refs list is read out of scratch here.
        string? generatedRoot = null;
        IReadOnlyList<string> harvestedReferences = [];
        if (rideBuildProject is not null)
        {
            string projectDirectory = Path.GetDirectoryName(rideBuildProject) ?? solutionRoot;
            generatedRoot = FindGeneratedRoot(projectDirectory, safeConfiguration);
            harvestedReferences = refsDumpPath is not null && File.Exists(refsDumpPath)
                ? File.ReadAllLines(refsDumpPath)
                : [];
            if (scratchDirectory is not null)
            {
                try { Directory.Delete(scratchDirectory, recursive: true); } catch { /* scratch cleanup is best-effort */ }
            }
        }

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
                    : $"Built {safeConfiguration} output.")
        {
            RidesBuildProject = rideBuildProject,
            GeneratedRoot = generatedRoot,
            HarvestedReferences = harvestedReferences,
        };
    }

    // obj/<config>/<tfm>/generated — the SDK razor source generator's output. First match (single-project /
    // single-TFM is all the read path supports today; multi-TFM is a later refinement).
    private static string FindGeneratedRoot(string projectDirectory, string configuration)
    {
        string objConfig = Path.Combine(projectDirectory, "obj", configuration);
        if (!Directory.Exists(objConfig))
        {
            return string.Empty;
        }

        return Directory.GetDirectories(objConfig, "generated", SearchOption.AllDirectories)
            .FirstOrDefault() ?? string.Empty;
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
