using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// A launchable, cancellable test service for ANY watched solution. The agent can't cancel a blocking
// tool call (it's parked awaiting the result), so testing is asynchronous, modelled on how Claude
// Code's own Bash tool backgrounds work: start_tests spawns `dotnet test` and returns a runId
// immediately; get_test_results polls it; cancel_tests kills it by handle. Because launch does not
// block, the agent CAN cancel or poll with a separate call.
//
// Framework-agnostic by design: results come from a TRX logger file (--logger trx), the structured
// format VSTest/Microsoft.Testing.Platform emit for xUnit / NUnit / MSTest alike — not from scraping
// stdout, which differs per framework and version. Stdout is kept only as an output tail and as a
// fallback when no TRX is produced (e.g. a build failure before any test ran).
//
// Registered as a singleton so the three tools share run state.
public sealed class BackgroundTestRunner
{
    public sealed record RunSnapshot(
        string RunId,
        string Status,
        bool Done,
        bool IsError,
        string Configuration,
        string? Filter,
        string SolutionPath,
        int Passed,
        int Failed,
        int Skipped,
        int Total,
        IReadOnlyList<string> Failures,
        string OutputTail,
        long DurationMs,
        string Message);

    private const int MaxOutputTailChars = 12000;
    private const int MaxTrackedRuns = 20;
    private static readonly XNamespace TrxNs = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private readonly ConcurrentDictionary<string, TestRun> runs = new();

    // Launch a run and return its snapshot IMMEDIATELY (status "running"), or a terminal snapshot for a
    // pre-flight failure (missing solution). Never blocks on the test process.
    public RunSnapshot Start(MonitorSettings settings, string configuration = "Debug", string? filter = null, TimeSpan? timeout = null)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        string safeConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        string runId = Guid.NewGuid().ToString("N")[..12];

        TestRun run = new(runId, safeConfiguration, filter, solutionPath);
        runs[runId] = run;
        EvictOldRuns();

        if (!File.Exists(solutionPath))
        {
            run.CompleteImmediately("missing-solution", "Test run skipped because the watched solution file is missing.");
            return run.Snapshot();
        }

        string resultsDirectory = Path.Combine(MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings), "test-runs", runId);
        try
        {
            Directory.CreateDirectory(resultsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.CompleteImmediately("error", $"Could not create the test-results directory: {ex.Message}");
            return run.Snapshot();
        }

        const string trxName = "results.trx";
        string trxPath = Path.Combine(resultsDirectory, trxName);
        List<string> arguments =
        [
            "test", solutionPath, "-c", safeConfiguration, "--nologo", "-nodeReuse:false",
            "--logger", $"trx;LogFileName={trxName}",
            "--results-directory", resultsDirectory,
        ];
        if (!string.IsNullOrWhiteSpace(filter))
        {
            arguments.Add("--filter");
            arguments.Add(filter.Trim());
        }

        string workingDirectory = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;
        run.Launch(arguments, workingDirectory, trxPath, timeout ?? TimeSpan.FromMinutes(10));
        return run.Snapshot();
    }

    public RunSnapshot? Get(string runId) =>
        runs.TryGetValue(runId, out TestRun? run) ? run.Snapshot() : null;

    // Cancel one run by handle (kills the dotnet test process tree). Null if the id is unknown.
    public RunSnapshot? Cancel(string runId) =>
        runs.TryGetValue(runId, out TestRun? run) ? run.Cancel().Snapshot() : null;

    // Cancel every in-flight run (e.g. the operator's Interrupt). Returns how many were running.
    public int CancelAll()
    {
        int cancelled = 0;
        foreach (TestRun run in runs.Values)
        {
            if (run.CancelIfRunning())
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    private void EvictOldRuns()
    {
        if (runs.Count <= MaxTrackedRuns)
        {
            return;
        }

        foreach (TestRun stale in runs.Values
            .Where(run => run.Snapshot().Done)
            .OrderBy(run => run.StartedUtc)
            .Take(runs.Count - MaxTrackedRuns))
        {
            runs.TryRemove(stale.Id, out _);
        }
    }

    // Parse a TRX result file for counts and failing test names. Framework-agnostic (xUnit/NUnit/MSTest
    // all log TRX through VSTest). Public+static for unit testing without running the SDK.
    public static (int Passed, int Failed, int Skipped, int Total, IReadOnlyList<string> Failures) ParseTrx(string trxPath)
    {
        XDocument document = XDocument.Load(trxPath);
        XElement? counters = document.Descendants(TrxNs + "Counters").FirstOrDefault();
        int total = (int?)counters?.Attribute("total") ?? 0;
        int passed = (int?)counters?.Attribute("passed") ?? 0;
        int failed = (int?)counters?.Attribute("failed") ?? 0;
        int skipped = Math.Max(0, total - passed - failed);

        List<string> failures = document.Descendants(TrxNs + "UnitTestResult")
            .Where(result => string.Equals((string?)result.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(result => (string?)result.Attribute("testName") ?? string.Empty)
            .Where(name => name.Length > 0)
            .Take(50)
            .ToList();

        return (passed, failed, skipped, total, failures);
    }

    // Fallback when no TRX exists: pull `: error` lines out of stdout so a build failure is still
    // reported with something actionable.
    public static IReadOnlyList<string> ExtractBuildErrors(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

    private static string Tail(string output) =>
        output.Length <= MaxOutputTailChars ? output : "…(truncated)…" + Environment.NewLine + output[^MaxOutputTailChars..];

    // One test execution: owns the process, the captured output, and the mutable result state, all
    // guarded by `gate`. Snapshot() takes the lock so a poll never tears a half-written update.
    private sealed class TestRun
    {
        private readonly object gate = new();
        private readonly StringBuilder output = new();
        private Process? process;
        private CancellationTokenSource? cts;
        private Stopwatch stopwatch = new();
        private string trxPath = string.Empty;

        private string status = "running";
        private bool done;
        private bool explicitCancel;
        private int passed, failed, skipped, total;
        private IReadOnlyList<string> failures = [];
        private string message = "Test run is in progress.";
        private long durationMs;

        public TestRun(string id, string configuration, string? filter, string solutionPath)
        {
            Id = id;
            Configuration = configuration;
            Filter = filter;
            SolutionPath = solutionPath;
            StartedUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public string Configuration { get; }
        public string? Filter { get; }
        public string SolutionPath { get; }
        public DateTime StartedUtc { get; }

        public void CompleteImmediately(string finalStatus, string finalMessage)
        {
            lock (gate)
            {
                status = finalStatus;
                message = finalMessage;
                done = true;
            }
        }

        public void Launch(IReadOnlyList<string> arguments, string workingDirectory, string trx, TimeSpan timeout)
        {
            trxPath = trx;
            ProcessStartInfo startInfo = new("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                CompleteImmediately("no-sdk", "Test run skipped — the .NET SDK ('dotnet') was not found on PATH.");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            stopwatch = Stopwatch.StartNew();
            cts = new CancellationTokenSource(timeout);
            _ = Task.Run(() => WaitAndFinalizeAsync(timeout));
        }

        private async Task WaitAndFinalizeAsync(TimeSpan timeout)
        {
            bool timedOut = false;
            bool cancelled = false;
            try
            {
                await process!.WaitForExitAsync(cts!.Token);
            }
            catch (OperationCanceledException)
            {
                lock (gate)
                {
                    cancelled = explicitCancel;
                    timedOut = !explicitCancel;
                }

                KillTree();
                try
                {
                    await process!.WaitForExitAsync();
                }
                catch (Exception)
                {
                }
            }

            // Ensure the async output handlers have flushed before we read the buffer / exit code.
            try
            {
                process!.WaitForExit();
            }
            catch (Exception)
            {
            }

            stopwatch.Stop();
            int exitCode = TryGetExitCode();
            Finalize(timedOut, cancelled, exitCode);
        }

        private void Finalize(bool timedOut, bool cancelled, int exitCode)
        {
            string capturedOutput;
            lock (gate)
            {
                capturedOutput = output.ToString();
            }

            int p = 0, f = 0, s = 0, t = 0;
            IReadOnlyList<string> fails = [];
            bool trxParsed = false;
            if (File.Exists(trxPath))
            {
                try
                {
                    (p, f, s, t, fails) = ParseTrx(trxPath);
                    trxParsed = true;
                }
                catch (Exception)
                {
                    trxParsed = false;
                }
            }

            string finalStatus;
            string finalMessage;
            if (cancelled)
            {
                finalStatus = "cancelled";
                finalMessage = "Test run was cancelled; the dotnet test process tree was killed.";
            }
            else if (timedOut)
            {
                finalStatus = "timeout";
                finalMessage = "Test run timed out; the dotnet test process tree was killed.";
            }
            else if (trxParsed)
            {
                finalStatus = f > 0 ? "failed" : t == 0 ? "no-tests" : "passed";
                finalMessage = finalStatus switch
                {
                    "failed" => $"{f} test{(f == 1 ? string.Empty : "s")} failed ({p} passed, {s} skipped).",
                    "no-tests" => "No tests were discovered in the watched solution.",
                    _ => $"All {p} test{(p == 1 ? string.Empty : "s")} passed ({s} skipped).",
                };
            }
            else
            {
                // No TRX — almost always a build failure before any test ran.
                IReadOnlyList<string> buildErrors = ExtractBuildErrors(capturedOutput);
                finalStatus = exitCode == 0 ? "no-tests" : "error";
                fails = buildErrors;
                finalMessage = exitCode == 0
                    ? "No tests ran and no result file was produced."
                    : buildErrors.Count > 0
                        ? $"dotnet test failed to build/run ({buildErrors.Count} error line(s))."
                        : $"dotnet test exited with code {exitCode} and produced no result file.";
            }

            lock (gate)
            {
                passed = p;
                failed = f;
                skipped = s;
                total = t;
                failures = fails;
                status = finalStatus;
                message = finalMessage;
                durationMs = stopwatch.ElapsedMilliseconds;
                done = true;
            }
        }

        public TestRun Cancel()
        {
            lock (gate)
            {
                if (done)
                {
                    return this;
                }

                explicitCancel = true;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            return this;
        }

        public bool CancelIfRunning()
        {
            lock (gate)
            {
                if (done)
                {
                    return false;
                }
            }

            Cancel();
            return true;
        }

        public RunSnapshot Snapshot()
        {
            lock (gate)
            {
                long elapsed = done ? durationMs : stopwatch.ElapsedMilliseconds;
                bool isError = status is "failed" or "error" or "timeout" or "cancelled" or "no-sdk" or "missing-solution";
                return new RunSnapshot(
                    Id, status, done, isError, Configuration, Filter, SolutionPath,
                    passed, failed, skipped, total, failures, Tail(output.ToString()), elapsed, message);
            }
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                output.Append(line).Append('\n');
            }
        }

        private void KillTree()
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        private int TryGetExitCode()
        {
            try
            {
                return process?.ExitCode ?? -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }
}
