using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Console.Models;
using ClaudeWorkbench.Host.Services;

namespace AIMonitor.Integration.Tests;

// ADR-0007, increment 4b-4: with the index riding the build, the terminal accept must run the
// build-after-accept BEFORE the reindex, so the index reads that build's output instead of racing
// (or duplicating) its own compile. The regression-critical invariant is the ORDER — index LAST —
// which the compile-index trace makes provable in one log. Drives the real EngineReviewWorkflow
// (the one and only writer of watched source) in process, like the atomicity suite.
public sealed class EngineReviewRidesBuildOrderTests
{
    [Fact]
    public void Terminal_accept_riding_the_build_reindexes_after_the_build()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        MonitorSettings settings = MonitorSettings.Create(
            fixture.RepositoryRoot, fixture.WatchedProjectPath, fixture.RuntimeRoot);
        WorkspaceManager workspace = new(fixture.RepositoryRoot, fixture.RuntimeRoot, settings);

        // A fresh trace per run so the ordering assertion reads only this accept's events.
        string tracePath = CompileIndexTrace.GetTraceFilePath(settings);
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        string sessionId = Guid.NewGuid().ToString("N");
        StagedEditRecord record = Stage(
            workspace,
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => \"rides-build\"; } }",
            sessionId);

        EngineReviewWorkflow review = new(workspace, new NullMonitorLogger());

        // Terminal accept with a build-after-accept requested and the index rebuilt. The index rides the
        // build unconditionally now (no flag), so it reads the build-after-accept's output.
        ReviewActionResult accept = review.Accept(
            record.StagedRecordId,
            forceApproveValidation: false,
            rebuildIndex: true,
            buildAfterAccept: true);

        Assert.StartsWith("Accepted", accept.Message, StringComparison.Ordinal);
        Assert.Contains("rides-build", File.ReadAllText(fixture.ProgramFilePath), StringComparison.Ordinal);
        // The reindex ran and reported success (not deferred, not errored).
        Assert.Contains("Index refreshed", accept.AgentSummary ?? string.Empty, StringComparison.Ordinal);

        // The trace proves the order: the build-after-accept completed BEFORE the index refresh started.
        Assert.True(File.Exists(tracePath), $"no compile-index trace was written at {tracePath}");
        string[] traceLines = File.ReadAllLines(tracePath);
        int buildDoneAt = IndexOfEvent(traceLines, "post-accept-build.done");
        int indexRefreshStartAt = IndexOfEvent(traceLines, "index-refresh.start");
        Assert.True(buildDoneAt >= 0, $"no post-accept-build.done in trace:\n{string.Join("\n", traceLines)}");
        Assert.True(indexRefreshStartAt >= 0, $"no index-refresh.start in trace:\n{string.Join("\n", traceLines)}");
        Assert.True(
            buildDoneAt < indexRefreshStartAt,
            $"index refresh must run AFTER the build-after-accept, but the trace has build.done at {buildDoneAt} and index-refresh.start at {indexRefreshStartAt}:\n{string.Join("\n", traceLines)}");
    }

    private static int IndexOfEvent(string[] traceLines, string phase)
    {
        for (int i = 0; i < traceLines.Length; i++)
        {
            // Trace format: "[00001] <ts> <phase padded> path=... | detail" — match the phase token.
            if (traceLines[i].Contains($" {phase} ", StringComparison.Ordinal)
                || traceLines[i].Contains($" {phase} path=", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static StagedEditRecord Stage(
        WorkspaceManager workspace,
        string watchedFilePath,
        string content,
        string sessionId)
    {
        workspace.EditService.Refresh(watchedFilePath);
        workspace.EditService.WriteWorkingCandidate(watchedFilePath, content, manifestJson: null);
        return workspace.EditService.Stage(watchedFilePath, "rides-build order", sessionId);
    }

    private sealed class NullMonitorLogger : IMonitorLogger
    {
        public void Write(
            MonitorLogLevel level,
            string source,
            string eventName,
            string message,
            IReadOnlyDictionary<string, string>? properties = null)
        {
        }
    }
}
