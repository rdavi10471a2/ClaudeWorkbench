using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIMonitor.Integration.Tests;

// AREA B (planned-session workflow E2E) and AREA C (safety / negative paths) of the MCP-surface suite.
//
// With the planned-session gate in place, every watched-source mutation requires a session whose edit plan
// names the file. These tests drive the full flow end to end against a real out-of-process AIMonitor.McpServer:
// start_monitor_session(filesPlanned) -> refresh/new + edit (GATE 1 overlay validation present in the tool
// response) -> stage -> launch (full overlay build once the batch is staged) -> simulated WinMerge merge ->
// record_diff_decision -> terminal build + post-accept index refresh. Single-file and multi-file are both
// covered, plus the negative gates: an unplanned file is rejected, accept-without-launch is rejected, an
// invalid-C# candidate is rejected, and a dirty-unexpected accept (watched != staged) is rejected.
public sealed class McpPlannedSessionSurfaceTests
{
    static McpPlannedSessionSurfaceTests()
    {
        Environment.SetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG", "1");
    }

    // ---- AREA B: planned-session E2E ----

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Single_file_planned_session_runs_full_flow_to_terminal_accept()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "single-file e2e", fixture.ProgramFilePath);

        CallToolResult refresh = await client.CallToolAsync(
            "refresh_file",
            new Dictionary<string, object?>
            {
                ["sourceFilePath"] = fixture.ProgramFilePath,
                ["sessionId"] = sessionId
            });
        Assert.False(refresh.IsError == true, McpSurfaceClient.Text(refresh));

        // Edit the working candidate via a Roslyn-aware submit; GATE 1 overlay validation runs once the
        // single planned candidate exists and is reported in the tool response.
        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => \"e2e\"; } }",
                ["sessionId"] = sessionId
            });
        Assert.False(submit.IsError == true, McpSurfaceClient.Text(submit));
        string submitJson = McpSurfaceClient.Text(submit);
        Assert.Contains("\"overlayValidation\"", submitJson, StringComparison.Ordinal);
        Assert.DoesNotContain("e2e", await File.ReadAllTextAsync(fixture.ProgramFilePath), StringComparison.Ordinal);

        CallToolResult stage = await client.CallToolAsync(
            "stage_candidate_for_review",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["ledgerSummary"] = "single-file e2e",
                ["sessionId"] = sessionId
            });
        Assert.False(stage.IsError == true, McpSurfaceClient.Text(stage));
        string stageJson = McpSurfaceClient.Text(stage);
        string stagedRecordId = McpSurfaceClient.JsonString(stageJson, "stagedRecordId");
        string stagedHash = McpSurfaceClient.JsonString(stageJson, "stagedHash");
        string stagedRecordJson = await McpSurfaceClient.StagedRecordJsonAsync(client, stagedRecordId);
        string stagedFilePath = McpSurfaceClient.JsonString(stagedRecordJson, "stagedFilePath");

        CallToolResult launch = await client.CallToolAsync(
            "launch_staged_diff",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["diffToolPath"] = McpSurfaceClient.FakeDiffToolPath()
            });
        Assert.False(launch.IsError == true, McpSurfaceClient.Text(launch));
        Assert.Contains("\"launched\":true", McpSurfaceClient.Text(launch), StringComparison.Ordinal);

        // Simulated WinMerge merge: copy the staged bytes verbatim into watched source.
        File.Copy(stagedFilePath, fixture.ProgramFilePath, overwrite: true);

        CallToolResult decision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["decision"] = "accepted",
                ["expectedStagedHash"] = stagedHash
            });
        Assert.False(decision.IsError == true, McpSurfaceClient.Text(decision));
        string decisionJson = McpSurfaceClient.Text(decision);
        Assert.Equal("accepted", McpSurfaceClient.JsonString(decisionJson, "classification"));
        // The single planned file is the last decided, so the terminal build + index rebuild fired.
        Assert.Contains("\"indexRefresh\"", decisionJson, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"rebuilt\"", decisionJson, StringComparison.Ordinal);
        Assert.Contains("e2e", await File.ReadAllTextAsync(fixture.ProgramFilePath), StringComparison.Ordinal);

        // Post-accept: a write to the same watched file requires a fresh refresh first (refresh-required guard).
        CallToolResult staleSubmit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => \"stale\"; } }",
                ["sessionId"] = sessionId
            });
        Assert.True(staleSubmit.IsError == true, McpSurfaceClient.Text(staleSubmit));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Multi_file_planned_session_defers_index_refresh_until_last_decision()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string helperFilePath = Path.Combine(fixture.WatchedDirectory, "Helper.cs");
        await File.WriteAllTextAsync(
            helperFilePath,
            "namespace Example { internal static class Helper { public static string Value() => \"old\"; } }");

        CallToolResult session = await client.CallToolAsync(
            "start_monitor_session",
            new Dictionary<string, object?>
            {
                ["purpose"] = "multi-file e2e",
                ["filesPlanned"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["sourceFilePath"] = helperFilePath,
                        ["owningProjectPath"] = fixture.WatchedProjectPath
                    },
                    new Dictionary<string, object?>
                    {
                        ["sourceFilePath"] = fixture.ProgramFilePath,
                        ["owningProjectPath"] = fixture.WatchedProjectPath
                    }
                }
            });
        Assert.False(session.IsError == true, McpSurfaceClient.Text(session));
        string sessionId = McpSurfaceClient.JsonString(McpSurfaceClient.Text(session), "sessionId");

        CallToolResult helperSubmit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = helperFilePath,
                ["content"] = "namespace Example { internal static class Helper { public static string Value() => \"accepted-helper\"; } }",
                ["sessionId"] = sessionId
            });
        Assert.False(helperSubmit.IsError == true, McpSurfaceClient.Text(helperSubmit));

        CallToolResult programSubmit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => Helper.Value(); } }",
                ["sessionId"] = sessionId
            });
        Assert.False(programSubmit.IsError == true, McpSurfaceClient.Text(programSubmit));

        (string helperRecordId, string helperHash, string helperStagedPath) = await StageAsync(client, helperFilePath, sessionId, "multi helper");
        (string programRecordId, string programHash, string programStagedPath) = await StageAsync(client, fixture.ProgramFilePath, sessionId, "multi program");

        // Both planned files must be staged before launch opens WinMerge.
        await LaunchAsync(client, helperRecordId);
        await LaunchAsync(client, programRecordId);

        // Decide the first file; index refresh is DEFERRED until the whole batch is decided.
        File.Copy(helperStagedPath, helperFilePath, overwrite: true);
        CallToolResult helperDecision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = helperRecordId,
                ["decision"] = "accepted",
                ["expectedStagedHash"] = helperHash
            });
        Assert.False(helperDecision.IsError == true, McpSurfaceClient.Text(helperDecision));
        string helperDecisionJson = McpSurfaceClient.Text(helperDecision);
        Assert.Equal("accepted", McpSurfaceClient.JsonString(helperDecisionJson, "classification"));
        Assert.Contains("\"status\":\"deferred\"", helperDecisionJson, StringComparison.Ordinal);

        // Decide the last file; now the terminal build + rebuild fires.
        File.Copy(programStagedPath, fixture.ProgramFilePath, overwrite: true);
        CallToolResult programDecision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = programRecordId,
                ["decision"] = "accepted",
                ["expectedStagedHash"] = programHash
            });
        Assert.False(programDecision.IsError == true, McpSurfaceClient.Text(programDecision));
        string programDecisionJson = McpSurfaceClient.Text(programDecision);
        Assert.Equal("accepted", McpSurfaceClient.JsonString(programDecisionJson, "classification"));
        Assert.Contains("\"status\":\"rebuilt\"", programDecisionJson, StringComparison.Ordinal);

        Assert.Contains("accepted-helper", await File.ReadAllTextAsync(helperFilePath), StringComparison.Ordinal);
        Assert.Contains("Helper.Value()", await File.ReadAllTextAsync(fixture.ProgramFilePath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Planned_new_file_flow_stages_and_rejects_without_creating_watched_source()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string newFilePath = Path.Combine(fixture.WatchedDirectory, "Generated", "NewThing.cs");
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "planned new file", newFilePath);

        CallToolResult create = await client.CallToolAsync(
            "new_file",
            new Dictionary<string, object?>
            {
                ["sourceFilePath"] = newFilePath,
                ["sessionId"] = sessionId
            });
        Assert.False(create.IsError == true, McpSurfaceClient.Text(create));
        string workingFilePath = McpSurfaceClient.JsonString(McpSurfaceClient.Text(create), "workingFilePath");
        Assert.True(File.Exists(workingFilePath));
        Assert.False(File.Exists(newFilePath));

        await File.WriteAllTextAsync(workingFilePath, "namespace Example.Generated { internal sealed class NewThing { } }");

        CallToolResult stage = await client.CallToolAsync(
            "stage_candidate_for_review",
            new Dictionary<string, object?>
            {
                ["path"] = newFilePath,
                ["sessionId"] = sessionId
            });
        Assert.False(stage.IsError == true, McpSurfaceClient.Text(stage));
        string stagedRecordId = McpSurfaceClient.JsonString(McpSurfaceClient.Text(stage), "stagedRecordId");

        CallToolResult decision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["decision"] = "rejected"
            });
        Assert.False(decision.IsError == true, McpSurfaceClient.Text(decision));
        Assert.Equal("rejected", McpSurfaceClient.JsonString(McpSurfaceClient.Text(decision), "classification"));
        Assert.False(File.Exists(newFilePath));
    }

    // ---- AREA C: safety / negative paths ----

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Planned_session_rejects_mutation_of_an_unplanned_file()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        // Plan ONLY Program.cs.
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "unplanned rejection", fixture.ProgramFilePath);

        string unplannedFilePath = Path.Combine(fixture.WatchedDirectory, "Unplanned.cs");
        await File.WriteAllTextAsync(
            unplannedFilePath,
            "namespace Example { internal static class Unplanned { } }");

        // refresh_file is a read/retrieval surface and is not itself gated, but the MUTATION (submit_file)
        // of a file the session never planned must be rejected by the planned gate.
        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = unplannedFilePath,
                ["content"] = "namespace Example { internal static class Unplanned { public static int X => 1; } }",
                ["sessionId"] = sessionId
            });
        Assert.True(submit.IsError == true, McpSurfaceClient.Text(submit));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Mutation_without_a_planned_session_is_rejected()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        // No start_monitor_session / no sessionId: the planned gate must refuse the mutation.
        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static int X => 1; } }"
            });
        Assert.True(submit.IsError == true, McpSurfaceClient.Text(submit));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Invalid_csharp_candidate_is_rejected_before_staging()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "invalid csharp", fixture.ProgramFilePath);

        // Syntactically broken C# submitted to the working candidate must be rejected as agent feedback
        // (not forced to WinMerge). The candidate write fails, so it never reaches staging.
        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => ",
                ["sessionId"] = sessionId
            });
        Assert.True(submit.IsError == true, McpSurfaceClient.Text(submit));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Accept_without_launch_is_rejected()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "accept without launch", fixture.ProgramFilePath);

        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => \"no-launch\"; } }",
                ["sessionId"] = sessionId
            });
        Assert.False(submit.IsError == true, McpSurfaceClient.Text(submit));

        (string stagedRecordId, string stagedHash, string stagedFilePath) = await StageAsync(client, fixture.ProgramFilePath, sessionId, "no launch");

        // Simulate the merge but DO NOT launch first. Accept must be refused: there is no recorded launch.
        File.Copy(stagedFilePath, fixture.ProgramFilePath, overwrite: true);
        CallToolResult decision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["decision"] = "accepted",
                ["expectedStagedHash"] = stagedHash
            });
        Assert.True(decision.IsError == true, McpSurfaceClient.Text(decision));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Accept_with_dirty_unexpected_watched_source_is_rejected()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string sessionId = await McpSurfaceClient.StartPlannedSessionAsync(client, fixture, "dirty unexpected", fixture.ProgramFilePath);

        CallToolResult submit = await client.CallToolAsync(
            "submit_file",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.ProgramFilePath,
                ["content"] = "namespace Example { internal static class Program { public static string Value => \"dirty\"; } }",
                ["sessionId"] = sessionId
            });
        Assert.False(submit.IsError == true, McpSurfaceClient.Text(submit));

        (string stagedRecordId, string stagedHash, _) = await StageAsync(client, fixture.ProgramFilePath, sessionId, "dirty");
        await LaunchAsync(client, stagedRecordId);

        // Operator did NOT merge the staged bytes verbatim: watched source carries something else.
        await File.WriteAllTextAsync(
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => \"hand-edited\"; } }");

        CallToolResult decision = await client.CallToolAsync(
            "record_diff_decision",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["decision"] = "accepted",
                ["expectedStagedHash"] = stagedHash
            });
        // Accept must be refused because watched source does not match the staged candidate.
        Assert.True(decision.IsError == true || McpSurfaceClient.JsonString(McpSurfaceClient.Text(decision), "classification") != "accepted",
            McpSurfaceClient.Text(decision));
    }

    private static async Task<(string RecordId, string StagedHash, string StagedFilePath)> StageAsync(
        McpClient client,
        string path,
        string sessionId,
        string ledgerSummary)
    {
        CallToolResult stage = await client.CallToolAsync(
            "stage_candidate_for_review",
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["ledgerSummary"] = ledgerSummary,
                ["sessionId"] = sessionId
            });
        Assert.False(stage.IsError == true, McpSurfaceClient.Text(stage));
        string stageJson = McpSurfaceClient.Text(stage);
        string recordId = McpSurfaceClient.JsonString(stageJson, "stagedRecordId");
        string stagedHash = McpSurfaceClient.JsonString(stageJson, "stagedHash");
        string recordJson = await McpSurfaceClient.StagedRecordJsonAsync(client, recordId);
        string stagedFilePath = McpSurfaceClient.JsonString(recordJson, "stagedFilePath");
        return (recordId, stagedHash, stagedFilePath);
    }

    private static async Task LaunchAsync(McpClient client, string stagedRecordId)
    {
        CallToolResult launch = await client.CallToolAsync(
            "launch_staged_diff",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId,
                ["diffToolPath"] = McpSurfaceClient.FakeDiffToolPath()
            });
        Assert.False(launch.IsError == true, McpSurfaceClient.Text(launch));
        Assert.Contains("\"launched\":true", McpSurfaceClient.Text(launch), StringComparison.Ordinal);
    }
}
