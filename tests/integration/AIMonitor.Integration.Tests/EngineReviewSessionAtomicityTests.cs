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
