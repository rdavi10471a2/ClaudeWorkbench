using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Console.Models;
using ClaudeWorkbench.Host.Services;

namespace AIMonitor.Integration.Tests;

// ADR-0005 (docs/decisions/0005-edit-session-is-atomic.md): the edit SESSION is the atomic unit
// of decision. These tests drive EngineReviewWorkflow — the one and only writer of watched
// source — in process, because the contract under test is about which bytes reach a developer's
// working tree and when. Nothing else can prove that.
//
// The three properties asserted here are:
//   1. a NON-terminal accept is an approval and writes nothing;
//   2. a reject after an approval voids the session, so NEITHER file is written;
//   3. the terminal accept writes the whole approved set, and a single-file session (whose
//      first accept is already terminal) behaves exactly as it always has.
//
// Tests 2 and 3 reach the terminal accept, which runs the authoritative GATE-2 `dotnet build`
// over the combined overlay — they are minutes-scale by design, which is why they live in the
// integration suite.
public sealed class EngineReviewSessionAtomicityTests
{
    // THE case the ADR exists for: the operator accepts one file of a two-file refactor and
    // rejects the other. Under the old per-file write the first file was already in watched
    // source and stayed there. Now the session is void and the working tree is untouched.
    [Fact]
    public void Reject_after_a_non_terminal_accept_leaves_both_files_unwritten()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        WorkspaceManager workspace = CreateWorkspace(fixture);
        string helperFilePath = Path.Combine(fixture.WatchedDirectory, "Helper.cs");
        File.WriteAllText(helperFilePath, "namespace Example { internal static class Helper { public static string Value() => \"old\"; } }");

        string originalHelperText = File.ReadAllText(helperFilePath);
        string originalProgramText = File.ReadAllText(fixture.ProgramFilePath);

        string sessionId = Guid.NewGuid().ToString("N");
        StagedEditRecord helperRecord = Stage(
            workspace,
            helperFilePath,
            "namespace Example { internal static class Helper { public static string Value() => \"approved\"; } }",
            sessionId);
        StagedEditRecord programRecord = Stage(
            workspace,
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => \"approved\"; } }",
            sessionId);

        EngineReviewWorkflow review = new(workspace, new NullMonitorLogger());

        // Accept #1 of 2 — non-terminal, so it must approve and write NOTHING.
        ReviewActionResult accept = review.Accept(helperRecord.StagedRecordId, forceApproveValidation: false);
        Assert.StartsWith("Accepted", accept.Message, StringComparison.Ordinal);
        Assert.Contains("NOTHING has been written", accept.Message, StringComparison.Ordinal);
        Assert.Contains("1 file(s) still to review", accept.Message, StringComparison.Ordinal);
        Assert.Null(accept.AgentSummary);
        Assert.Equal(originalHelperText, File.ReadAllText(helperFilePath));
        Assert.Equal(originalProgramText, File.ReadAllText(fixture.ProgramFilePath));

        StagedEditRecord approvedHelper = workspace.EditService.GetStagedRecord(helperRecord.StagedRecordId);
        Assert.Equal("approved", approvedHelper.Decision);
        Assert.Equal(string.Empty, approvedHelper.WrittenAtUtc);

        // Reject #2 of 2 — a single reject voids the whole session, including the approval.
        ReviewActionResult reject = review.Reject(programRecord.StagedRecordId);
        Assert.Contains("Edit session stopped", reject.Message, StringComparison.Ordinal);
        Assert.Contains("none of them were written to watched source", reject.AgentSummary ?? string.Empty, StringComparison.Ordinal);

        // The whole point: neither file ever reached the developer's working tree.
        Assert.Equal(originalHelperText, File.ReadAllText(helperFilePath));
        Assert.Equal(originalProgramText, File.ReadAllText(fixture.ProgramFilePath));

        StagedEditRecord finalHelper = workspace.EditService.GetStagedRecord(helperRecord.StagedRecordId);
        StagedEditRecord finalProgram = workspace.EditService.GetStagedRecord(programRecord.StagedRecordId);
        Assert.Equal("rejected", finalHelper.Decision);
        Assert.Equal("rejected", finalProgram.Decision);
        Assert.Equal(string.Empty, finalHelper.WrittenAtUtc);
        Assert.Equal(string.Empty, finalProgram.WrittenAtUtc);
    }

    // The terminal accept writes the whole approved set at once, after the combined-overlay
    // build passes — so what was validated is what was written.
    [Fact]
    public void Terminal_accept_writes_every_approved_file_in_the_session()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        WorkspaceManager workspace = CreateWorkspace(fixture);
        string helperFilePath = Path.Combine(fixture.WatchedDirectory, "Helper.cs");
        File.WriteAllText(helperFilePath, "namespace Example { internal static class Helper { public static string Value() => \"old\"; } }");
        string originalHelperText = File.ReadAllText(helperFilePath);

        string sessionId = Guid.NewGuid().ToString("N");
        StagedEditRecord helperRecord = Stage(
            workspace,
            helperFilePath,
            "namespace Example { internal static class Helper { public static string Value() => \"session-written\"; } }",
            sessionId);
        StagedEditRecord programRecord = Stage(
            workspace,
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => \"session-written\"; } }",
            sessionId);

        EngineReviewWorkflow review = new(workspace, new NullMonitorLogger());

        ReviewActionResult firstAccept = review.Accept(helperRecord.StagedRecordId, forceApproveValidation: false);
        Assert.Contains("NOTHING has been written", firstAccept.Message, StringComparison.Ordinal);
        Assert.Equal(originalHelperText, File.ReadAllText(helperFilePath));

        ReviewActionResult terminalAccept = review.Accept(programRecord.StagedRecordId, forceApproveValidation: false);
        Assert.StartsWith("Accepted", terminalAccept.Message, StringComparison.Ordinal);
        Assert.Contains("2 file(s) written", terminalAccept.Message, StringComparison.Ordinal);

        // Both files land together on the terminal accept, not one per accept.
        Assert.Contains("session-written", File.ReadAllText(helperFilePath), StringComparison.Ordinal);
        Assert.Contains("session-written", File.ReadAllText(fixture.ProgramFilePath), StringComparison.Ordinal);

        StagedEditRecord finalHelper = workspace.EditService.GetStagedRecord(helperRecord.StagedRecordId);
        StagedEditRecord finalProgram = workspace.EditService.GetStagedRecord(programRecord.StagedRecordId);
        Assert.Equal("accepted", finalHelper.Decision);
        Assert.Equal("accepted", finalProgram.Decision);
        Assert.NotEqual(string.Empty, finalHelper.WrittenAtUtc);
        Assert.NotEqual(string.Empty, finalProgram.WrittenAtUtc);

        // The agent is told about the whole set, not just the last file.
        Assert.Contains("2 file(s) written to source together", terminalAccept.AgentSummary ?? string.Empty, StringComparison.Ordinal);
    }

    // The safety property ADR-0005 leans on: a single-file session's first accept is ALREADY
    // terminal, so its behaviour is identical before and after this change — build, write,
    // record, index, agent summary, all on the one accept.
    [Fact]
    public void Single_file_session_accept_is_terminal_and_writes_immediately()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        WorkspaceManager workspace = CreateWorkspace(fixture);

        string sessionId = Guid.NewGuid().ToString("N");
        StagedEditRecord record = Stage(
            workspace,
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => \"single-file\"; } }",
            sessionId);

        EngineReviewWorkflow review = new(workspace, new NullMonitorLogger());
        ReviewActionResult accept = review.Accept(record.StagedRecordId, forceApproveValidation: false);

        Assert.StartsWith("Accepted", accept.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING has been written", accept.Message, StringComparison.Ordinal);
        Assert.Contains("written", accept.Message, StringComparison.Ordinal);
        Assert.Contains("single-file", File.ReadAllText(fixture.ProgramFilePath), StringComparison.Ordinal);

        StagedEditRecord decided = workspace.EditService.GetStagedRecord(record.StagedRecordId);
        Assert.Equal("accepted", decided.Decision);
        Assert.NotEqual(string.Empty, decided.WrittenAtUtc);
        Assert.NotNull(accept.AgentSummary);
    }

    // The ONLY automated coverage of the Accept-With-Validation-Override path. A mutually-broken
    // two-file session (each references a member the OTHER never defines) carries a pre-merge
    // overlay-compile error on BOTH records, so normal accept is refused and the operator must
    // force. Asserts the override contract end to end:
    //   - normal accept is refused and offers the override;
    //   - a NON-terminal override is an approval: force-approved, writes NOTHING, and runs no build
    //     (the redundant per-file build that flashed the accept spinner — see
    //     WorkflowEditService.ForceApprovePreMergeValidation);
    //   - the terminal override writes the WHOLE broken set at once even though GATE-2 fails,
    //     because forcing is a deliberate, operator-only choice.
    // Reaches the terminal GATE-2 dotnet build, so (like the other terminal tests) it is
    // minutes-scale by design.
    [Fact]
    public void Force_override_writes_a_build_failing_session_atomically()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        WorkspaceManager workspace = CreateWorkspace(fixture);
        string helperFilePath = Path.Combine(fixture.WatchedDirectory, "Helper.cs");
        File.WriteAllText(helperFilePath, "namespace Example { internal static class Helper { public static string Value() => \"old\"; } }");
        string originalHelperText = File.ReadAllText(helperFilePath);

        // Each file references a member the OTHER does not define: both candidates PARSE (so they
        // clear the submit-time syntax guard) but the combined set cannot compile.
        string sessionId = Guid.NewGuid().ToString("N");
        StagedEditRecord helperRecord = Stage(
            workspace,
            helperFilePath,
            "namespace Example { internal static class Helper { public static string Value() => Program.Missing(); } }",
            sessionId);
        StagedEditRecord programRecord = Stage(
            workspace,
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => Helper.Missing(); } }",
            sessionId);

        // Stamp the overlay-compile failure a real staged record carries (complete_edit_plan records
        // it; GATE 1 is only a readiness check, so the atomicity Stage helper leaves no verdict).
        StampOverlayError(workspace, helperRecord.StagedRecordId);
        StampOverlayError(workspace, programRecord.StagedRecordId);

        EngineReviewWorkflow review = new(workspace, new NullMonitorLogger());

        // Normal accept is refused; the override is offered.
        ReviewActionResult blocked = review.Accept(helperRecord.StagedRecordId, forceApproveValidation: false);
        Assert.True(blocked.OverrideAvailable, blocked.Message);
        Assert.Equal(originalHelperText, File.ReadAllText(helperFilePath));

        // Non-terminal override: an approval — force-approved, writes NOTHING (and runs no build).
        ReviewActionResult overrideFirst = review.Accept(helperRecord.StagedRecordId, forceApproveValidation: true);
        Assert.StartsWith("Accepted", overrideFirst.Message, StringComparison.Ordinal);
        Assert.Contains("NOTHING has been written", overrideFirst.Message, StringComparison.Ordinal);
        Assert.Equal(originalHelperText, File.ReadAllText(helperFilePath));
        StagedEditRecord approvedHelper = workspace.EditService.GetStagedRecord(helperRecord.StagedRecordId);
        Assert.Equal("approved", approvedHelper.Decision);
        Assert.True(approvedHelper.PreMergeValidationForceApproved);
        Assert.Equal(string.Empty, approvedHelper.WrittenAtUtc);

        // Terminal override: GATE-2 build FAILS, but forcing writes the whole set together anyway.
        ReviewActionResult overrideTerminal = review.Accept(programRecord.StagedRecordId, forceApproveValidation: true);
        Assert.StartsWith("Accepted", overrideTerminal.Message, StringComparison.Ordinal);
        Assert.Contains("written", overrideTerminal.Message, StringComparison.Ordinal);

        // Both broken files land on disk together — the deliberate, forced merge.
        Assert.Contains("Program.Missing()", File.ReadAllText(helperFilePath), StringComparison.Ordinal);
        Assert.Contains("Helper.Missing()", File.ReadAllText(fixture.ProgramFilePath), StringComparison.Ordinal);
        StagedEditRecord finalHelper = workspace.EditService.GetStagedRecord(helperRecord.StagedRecordId);
        StagedEditRecord finalProgram = workspace.EditService.GetStagedRecord(programRecord.StagedRecordId);
        Assert.Equal("accepted", finalHelper.Decision);
        Assert.Equal("accepted", finalProgram.Decision);
        Assert.NotEqual(string.Empty, finalHelper.WrittenAtUtc);
        Assert.NotEqual(string.Empty, finalProgram.WrittenAtUtc);
    }

    // Stamp the pre-merge overlay-compile failure a real staged record carries so the override path
    // is exercised. EnsureValidatedAndLaunched preserves an existing verdict (it does not re-run
    // GATE 1 over the top), so this is exactly the state a complete_edit_plan overlay failure leaves.
    private static void StampOverlayError(WorkspaceManager workspace, string stagedRecordId)
    {
        workspace.EditService.RecordPreMergeValidation(
            stagedRecordId,
            new PreMergeValidationResult
            {
                Status = "failed",
                IsError = true,
                DiagnosticCount = 1,
                Diagnostics = ["overlay compile error (test fixture)"]
            },
            forceApproved: false);
    }

    private static WorkspaceManager CreateWorkspace(McpSurfaceFixture fixture)
    {
        MonitorSettings settings = MonitorSettings.Create(
            fixture.RepositoryRoot,
            fixture.WatchedProjectPath,
            fixture.RuntimeRoot);
        return new WorkspaceManager(fixture.RepositoryRoot, fixture.RuntimeRoot, settings);
    }

    // Refresh -> write the working candidate -> stage, all through the SAME WorkflowEditService
    // instance the review workflow reads (staged records are owned in memory per instance).
    // Overlay validation is skipped so the staged record carries no pre-merge verdict of its
    // own; the review workflow stamps GATE 1 itself when the operator opens the file.
    private static StagedEditRecord Stage(
        WorkspaceManager workspace,
        string watchedFilePath,
        string content,
        string sessionId)
    {
        workspace.EditService.Refresh(watchedFilePath);
        workspace.EditService.WriteWorkingCandidate(watchedFilePath, content, manifestJson: null, validateOverlay: false);
        return workspace.EditService.Stage(watchedFilePath, "session atomicity", sessionId);
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
