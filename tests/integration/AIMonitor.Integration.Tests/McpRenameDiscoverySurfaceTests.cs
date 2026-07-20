using AIMonitor.Core;
using AIMonitor.Workflow;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIMonitor.Integration.Tests;

// Salvaged from `AIMonitor.ToolSmokeTests --mcp-live-accepted-three-file-interface-session`
// (docs/plans/retire-legacy-test-harness.md, Phase 2.2), retargeted from the missing stdio bridge onto
// the real AIMonitor.McpServer surface. The stopwatch/benchmark scaffolding of the original mode is
// deliberately NOT ported — it measured, it did not assert.
//
// What IS ported is the discovery sequence, which is the only test anywhere of the question an agent
// must be able to answer BEFORE a breaking change:
//
//     "who consumes this member, so I know how many files this rename actually touches?"
//
// A three-file contract (one owner property, two external consumers) is seeded through the real planned
// -session accept flow, the index is refreshed, and then find_indexed_symbols -> find_indexed_references
// must surface BOTH consumers. That is the assertion that matters: a one-consumer answer would turn a
// three-file change into a two-file change and leave the solution broken.
//
// The rename is then carried out across all three files in a second planned session, and discovery is
// re-run against the rebuilt index to confirm the index followed the change.
public sealed class McpRenameDiscoverySurfaceTests
{
    static McpRenameDiscoverySurfaceTests()
    {
        Environment.SetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG", "1");
    }

    private const string OriginalPropertyName = "LegacyValue";
    private const string RenamedPropertyName = "CurrentValue";

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Index_discovers_every_external_consumer_before_a_contract_rename()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string ownerPath = Path.Combine(fixture.WatchedDirectory, "ContractOwner.cs");
        string firstConsumerPath = Path.Combine(fixture.WatchedDirectory, "ConsumerOne.cs");
        string secondConsumerPath = Path.Combine(fixture.WatchedDirectory, "ConsumerTwo.cs");

        // ---- Seed an ACCEPTED three-file baseline through the real planned-session flow ----
        client = await AcceptPlannedBatchAsync(
            client,
            fixture,
            "three-file contract baseline",
            isNewFile: true,
            [
                (ownerPath, OwnerContent(OriginalPropertyName)),
                (firstConsumerPath, FirstConsumerContent(OriginalPropertyName)),
                (secondConsumerPath, SecondConsumerContent(OriginalPropertyName))
            ]);

        Assert.Contains(OriginalPropertyName, await File.ReadAllTextAsync(ownerPath), StringComparison.Ordinal);
        Assert.Contains(OriginalPropertyName, await File.ReadAllTextAsync(firstConsumerPath), StringComparison.Ordinal);
        Assert.Contains(OriginalPropertyName, await File.ReadAllTextAsync(secondConsumerPath), StringComparison.Ordinal);

        CallToolResult refresh = await client.CallToolAsync("refresh_solution_index", new Dictionary<string, object?>());
        Assert.False(refresh.IsError == true, McpSurfaceClient.Text(refresh));

        // ---- THE SALVAGED QUESTION: find the symbol, then find everyone who consumes it ----
        string stableKey = await FindPropertyStableKeyAsync(client, OriginalPropertyName);
        string referencesJson = await FindReferencesJsonAsync(client, stableKey);

        // BOTH consumers must be discoverable from the index alone. Missing either one is the exact
        // failure the original scenario was written to catch.
        Assert.Contains("ConsumerOne.cs", referencesJson, StringComparison.Ordinal);
        Assert.Contains("ConsumerTwo.cs", referencesJson, StringComparison.Ordinal);

        // ---- Now perform the rename the discovery just scoped, across all three files ----
        client = await AcceptPlannedBatchAsync(
            client,
            fixture,
            "three-file contract rename",
            isNewFile: false,
            [
                (ownerPath, OwnerContent(RenamedPropertyName)),
                (firstConsumerPath, FirstConsumerContent(RenamedPropertyName)),
                (secondConsumerPath, SecondConsumerContent(RenamedPropertyName))
            ]);

        Assert.Contains(RenamedPropertyName, await File.ReadAllTextAsync(ownerPath), StringComparison.Ordinal);
        Assert.Contains(RenamedPropertyName, await File.ReadAllTextAsync(firstConsumerPath), StringComparison.Ordinal);
        Assert.Contains(RenamedPropertyName, await File.ReadAllTextAsync(secondConsumerPath), StringComparison.Ordinal);

        // The terminal accept rebuilt the index; discovery must now answer for the renamed member and
        // still see both consumers. If it did not, the next agent would scope its change from stale rows.
        string renamedKey = await FindPropertyStableKeyAsync(client, RenamedPropertyName);
        string renamedReferencesJson = await FindReferencesJsonAsync(client, renamedKey);
        Assert.Contains("ConsumerOne.cs", renamedReferencesJson, StringComparison.Ordinal);
        Assert.Contains("ConsumerTwo.cs", renamedReferencesJson, StringComparison.Ordinal);

        await client.DisposeAsync();
    }

    private static async Task<string> FindPropertyStableKeyAsync(McpClient client, string propertyName)
    {
        CallToolResult symbols = await client.CallToolAsync(
            "find_indexed_symbols",
            new Dictionary<string, object?>
            {
                ["text"] = $"ContractOwner.{propertyName}",
                ["kind"] = "Property"
            });
        Assert.False(symbols.IsError == true, McpSurfaceClient.Text(symbols));
        string symbolsJson = McpSurfaceClient.Text(symbols);
        Assert.Contains(propertyName, symbolsJson, StringComparison.Ordinal);
        return McpSurfaceClient.JsonString(symbolsJson, "stableKey");
    }

    private static async Task<string> FindReferencesJsonAsync(McpClient client, string stableKey)
    {
        CallToolResult references = await client.CallToolAsync(
            "find_indexed_references",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = stableKey,
                ["responseShape"] = "rich"
            });
        Assert.False(references.IsError == true, McpSurfaceClient.Text(references));
        string referencesJson = McpSurfaceClient.Text(references);
        // The tool reports a soft failure in-band (isError only appears on the failure shape).
        Assert.DoesNotContain("\"isError\":true", referencesJson, StringComparison.Ordinal);
        return referencesJson;
    }

    // Drives one planned session over N files all the way to a terminal accept, and returns a client
    // reconnected after the simulated operator review.
    //
    // The review stamp is applied to the WHOLE staged batch at once (PreMergeValidationService.Validate
    // with every record in the overlay), not per record. That is required here and is what the original
    // scenario did: a cross-file rename validated one record at a time would compile the renamed owner
    // against the not-yet-renamed consumers. InAppReviewSimulator's single-record stamp is correct for
    // independent edits and is used by the rest of the suite.
    private static async Task<McpClient> AcceptPlannedBatchAsync(
        McpClient client,
        McpSurfaceFixture fixture,
        string purpose,
        bool isNewFile,
        IReadOnlyList<(string Path, string Content)> files)
    {
        object[] filesPlanned = files
            .Select(file => new Dictionary<string, object?>
            {
                ["sourceFilePath"] = file.Path,
                ["owningProjectPath"] = fixture.WatchedProjectPath,
                ["role"] = isNewFile ? "new-file" : "existing-file",
                ["reason"] = purpose
            })
            .Cast<object>()
            .ToArray();

        CallToolResult session = await client.CallToolAsync(
            "start_monitor_session",
            new Dictionary<string, object?>
            {
                ["purpose"] = purpose,
                ["filesPlanned"] = filesPlanned
            });
        Assert.False(session.IsError == true, McpSurfaceClient.Text(session));
        string sessionId = McpSurfaceClient.JsonString(McpSurfaceClient.Text(session), "sessionId");

        foreach ((string path, string content) in files)
        {
            CallToolResult prepare = await client.CallToolAsync(
                isNewFile ? "new_file" : "refresh_file",
                new Dictionary<string, object?>
                {
                    ["sourceFilePath"] = path,
                    ["sessionId"] = sessionId
                });
            Assert.False(prepare.IsError == true, McpSurfaceClient.Text(prepare));

            CallToolResult submit = await client.CallToolAsync(
                "submit_file",
                new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["content"] = content,
                    ["sessionId"] = sessionId
                });
            Assert.False(submit.IsError == true, McpSurfaceClient.Text(submit));
        }

        List<(string RecordId, string StagedHash)> staged = [];
        foreach ((string path, _) in files)
        {
            CallToolResult stage = await client.CallToolAsync(
                "stage_candidate_for_review",
                new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["ledgerSummary"] = purpose,
                    ["sessionId"] = sessionId
                });
            Assert.False(stage.IsError == true, McpSurfaceClient.Text(stage));
            string stageJson = McpSurfaceClient.Text(stage);
            staged.Add((
                McpSurfaceClient.JsonString(stageJson, "stagedRecordId"),
                McpSurfaceClient.JsonString(stageJson, "stagedHash")));
        }

        // Staged records are owned in memory by whichever process hosts the engine, so the stamp made
        // here is only visible to the server after a restart. See McpSurfaceTestHarness.
        await client.DisposeAsync();
        IReadOnlyList<string> stagedFilePaths = MarkBatchReviewed(fixture, staged.Select(record => record.RecordId).ToArray());
        client = await McpSurfaceClient.ConnectAsync(fixture);

        for (int index = 0; index < staged.Count; index++)
        {
            // Simulated operator merge: the staged bytes land in watched source verbatim.
            File.Copy(stagedFilePaths[index], files[index].Path, overwrite: true);

            CallToolResult decision = await client.CallToolAsync(
                "record_diff_decision",
                new Dictionary<string, object?>
                {
                    ["stagedRecordId"] = staged[index].RecordId,
                    ["decision"] = "accepted",
                    ["expectedStagedHash"] = staged[index].StagedHash
                });
            Assert.False(decision.IsError == true, McpSurfaceClient.Text(decision));
            string decisionJson = McpSurfaceClient.Text(decision);
            Assert.Equal("accepted", McpSurfaceClient.JsonString(decisionJson, "classification"));

            // Index refresh is deferred until the whole planned batch is decided.
            string expectedRefresh = index == staged.Count - 1 ? "\"status\":\"rebuilt\"" : "\"status\":\"deferred\"";
            Assert.Contains(expectedRefresh, decisionJson, StringComparison.Ordinal);
        }

        return client;
    }

    private static IReadOnlyList<string> MarkBatchReviewed(McpSurfaceFixture fixture, IReadOnlyList<string> stagedRecordIds)
    {
        MonitorSettings settings = MonitorSettings.Create(
            fixture.RepositoryRoot,
            fixture.WatchedProjectPath,
            fixture.RuntimeRoot);
        WorkflowEditService editService = new(settings);
        StagedEditRecord[] records = stagedRecordIds.Select(editService.GetStagedRecord).ToArray();

        // Validate the batch as one overlay: the renamed owner and its renamed consumers must be
        // compiled together or the cross-file rename can never pass pre-merge validation.
        PreMergeValidationResult validation = new PreMergeValidationService().Validate(settings, records[^1], records);
        Assert.False(validation.IsError, validation.Message);

        foreach (StagedEditRecord record in records)
        {
            editService.PrepareReviewFileForLaunch(record.StagedRecordId);
            editService.RecordPreMergeValidation(record.StagedRecordId, validation, forceApproved: false);
            editService.RecordDiffLaunch(record.StagedRecordId, launched: true, "in-app merge review");
        }

        return records.Select(record => record.StagedFilePath).ToArray();
    }

    private static string OwnerContent(string propertyName)
    {
        return $$"""
            namespace Example
            {
                public static class ContractOwner
                {
                    public static string {{propertyName}} => "{{propertyName}}";
                }
            }
            """;
    }

    private static string FirstConsumerContent(string propertyName)
    {
        return $$"""
            namespace Example
            {
                public static class ConsumerOne
                {
                    public static string Read() => ContractOwner.{{propertyName}};
                }
            }
            """;
    }

    private static string SecondConsumerContent(string propertyName)
    {
        return $$"""
            namespace Example
            {
                public static class ConsumerTwo
                {
                    public static string Compose() => ContractOwner.{{propertyName}} + ":suffix";
                }
            }
            """;
    }
}
