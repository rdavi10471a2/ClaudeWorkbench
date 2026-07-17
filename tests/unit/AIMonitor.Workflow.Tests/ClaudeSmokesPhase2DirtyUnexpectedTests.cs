using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — Phase 2 CMB-parity CI gate (dirty-unexpected blocking), authored by Claude
// (review+test role; no production edits). LOCAL only.
//
// The Phase 2-5 parity review CLAIMED the dirty-unexpected reject "silently proceeds — the next stage succeeds and
// supersedes it." Running this proved that claim WRONG (a read-only over-read): the file IS protected — the next
// stage throws "Watched file changed since refresh" (the watched-changed guard catches it). So this smoke pins the
// real, safe behavior: after a dirty-unexpected decision the next stage is BLOCKED (recovery/refresh required), not
// silently re-staged. The only true residual is low/ergonomic: the persisted Status="dirty-unexpected" is an orphaned
// field nothing consumes, and the block message is generic rather than dirty-unexpected-specific (a Codex polish item,
// not a safety hole).
public sealed class ClaudeSmokesPhase2DirtyUnexpectedTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_dirty_unexpected_blocks_next_stage_until_recovery()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);

        // Stage a candidate (staged != original).
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");
        StagedEditRecord record = service.Stage(fixture.ProgramFilePath);

        // Operator left watched source as neither the original nor the staged candidate -> dirty-unexpected.
        File.WriteAllText(fixture.ProgramFilePath, "namespace Example { internal static class Program { public static string Value => \"external-edit\"; } }");
        StagedEditRecord decided = service.RecordDecision(record.StagedRecordId, "rejected");

        // Confirm we reproduced the dirty-unexpected state.
        Assert.Equal("dirty-unexpected", decided.Classification);

        // SAFE BEHAVIOR (verified, contra the review): the dirty-unexpected file is protected — the next stage is
        // blocked and requires recovery/refresh; it is NOT silently re-staged/superseded.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.Stage(fixture.ProgramFilePath));
        Assert.True(
            ex.Message.Contains("refresh", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("dirty", StringComparison.OrdinalIgnoreCase),
            $"Expected the dirty-unexpected file to be blocked pending recovery/refresh; got: {ex.Message}");
    }

    private static WorkflowFixture CreateFixture()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesP2", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string programFilePath = Path.Combine(watchedRoot, "Program.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(programFilePath, "namespace Example { internal static class Program { } }");

        return new WorkflowFixture(MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot), programFilePath);
    }

    private sealed record WorkflowFixture(MonitorSettings Settings, string ProgramFilePath);
}
