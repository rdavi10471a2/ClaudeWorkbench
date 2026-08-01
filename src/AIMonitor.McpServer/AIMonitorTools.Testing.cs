using AIMonitor.Workflow;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    [McpServerTool]
    [Description("Start the watched solution's tests in the BACKGROUND and return a runId immediately (you never run a shell yourself). The host runs `dotnet test` from the solution root with a TRX logger, so results are framework-agnostic (xUnit/NUnit/MSTest). Because this does NOT block, you can keep working, then poll `get_test_results(runId)` for the outcome and `cancel_tests(runId)` to kill a hanging run. Tests the last-ACCEPTED on-disk source (accept your change first to cover it). Use `filter` to run tests INDIVIDUALLY or BY GROUP (see the parameter); omit it to run everything. Set `configuration` (default Debug).")]
    public BackgroundTestRunner.RunSnapshot StartTests(
        [Description("Optional --filter to scope the run (maps to `dotnet test --filter`). Run INDIVIDUALLY or BY GROUP: a single test = \"FullyQualifiedName=Namespace.Class.Method\"; a whole class = \"FullyQualifiedName~Namespace.Class\"; a namespace/group = \"FullyQualifiedName~Namespace\"; by trait/category = \"Category=Fast\" or \"Trait=Slow\". Combine with | (OR) and & (AND), e.g. \"FullyQualifiedName~ClassA|FullyQualifiedName~ClassB\". Omit to run ALL tests in the watched solution.")] string? filter = null,
        [Description("Build/test configuration: Debug (default) or Release.")] string configuration = "Debug")
    {
        runtimeState.Touch();
        return testRunner.Start(settings, configuration, filter);
    }

    [McpServerTool]
    [Description("Poll a background test run started with start_tests. Returns its status (running | passed | failed | no-tests | error | timeout | cancelled | no-sdk | missing-solution), pass/fail/skipped/total counts, the failing test names, a bounded output tail, and elapsed ms. Call it again while status is \"running\" until Done is true. Unknown runId returns an error result.")]
    public object GetTestResults(
        [Description("The runId returned by start_tests.")] string runId)
    {
        runtimeState.Touch();
        BackgroundTestRunner.RunSnapshot? snapshot = testRunner.Get(runId);
        return snapshot is not null
            ? snapshot
            : new { error = true, message = $"No test run with id '{runId}'. It may have been evicted; start a new run." };
    }

    [McpServerTool]
    [Description("Cancel a background test run started with start_tests, killing its dotnet test process tree. Use it when a run hangs or is no longer needed. Returns the run's final snapshot (status becomes \"cancelled\"), or an error if the runId is unknown.")]
    public object CancelTests(
        [Description("The runId returned by start_tests.")] string runId)
    {
        runtimeState.Touch();
        BackgroundTestRunner.RunSnapshot? snapshot = testRunner.Cancel(runId);
        return snapshot is not null
            ? snapshot
            : new { error = true, message = $"No test run with id '{runId}' to cancel." };
    }
}
