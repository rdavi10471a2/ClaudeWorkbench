using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.MSBuild;
using AIMonitor.Workflow;

namespace AIMonitor.Integration.Tests;

// Engine-direct re-homing of the behaviours that only the retired `AIMonitor.Cli` tests covered
// (see docs/plans/retire-legacy-test-harness.md, Phase 3). Everything here drives the engine
// services the product uses; nothing shells out.
//
// THE RULE THAT MAKES THIS SAFE
// -----------------------------
// `WorkflowEditService` keeps staged records in a per-instance in-memory cache with atomic
// write-through to disk. Every CLI invocation was a fresh process, hence a cold cache, hence a
// record that genuinely rehydrated from disk before each guard ran. Thirteen of the CLI tests
// asserted that rehydration implicitly.
//
// So: a NEW `WorkflowEditService` is constructed at EVERY seam where the CLI previously started a
// new process — refresh, stage, staged-record read, review stamp, decision. Reusing one instance
// across stage -> review-stamp -> accept would satisfy every guard from the warm cache and
// silently stop testing rehydration, while the tests still passed. That is why `NewEditService`
// exists and why no test below holds a service in a local across steps.
public sealed class EngineEditLifecycleTests
{
    static EngineEditLifecycleTests()
    {
        Environment.SetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG", "1");
    }

    // ---------------------------------------------------------------------------------------
    // 1. `accepted-normalized`: the working copy was saved LF-only, watched source came back as
    //    CRLF. The exact hashes differ, the normalized hashes match, so the decision classifies
    //    as accepted-normalized — and that branch still triggers a post-accept index rebuild.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Record_decision_reports_accepted_normalized_when_only_line_endings_differ()
    {
        EngineFixture fixture = CreateFixture();
        const string crlf = "namespace Example\r\n{\r\n    internal static class Program { }\r\n}\r\n";
        const string lf = "namespace Example\n{\n    internal static class Program { }\n}\n";
        await File.WriteAllTextAsync(fixture.ProgramFilePath, crlf);

        WorkflowEditService shared = NewEditService(fixture); // MUTATION: single reused instance
        EditSessionStatus refresh = shared.Refresh(fixture.ProgramFilePath);
        Assert.True(File.Exists(refresh.WorkingFilePath));
        await File.WriteAllTextAsync(refresh.WorkingFilePath, lf);

        StagedEditRecord staged = shared.Stage(fixture.ProgramFilePath);
        Assert.Equal(0, CountBareLf(crlf));
        Assert.True(CountBareLf(lf) > 0);

        // MUTATION: review stamp through the SAME instance (warm cache), not InAppReviewSimulator.
        StagedEditRecord forReview = shared.GetStagedRecord(staged.StagedRecordId);
        shared.PrepareReviewFileForLaunch(staged.StagedRecordId);
        PreMergeValidationResult overlay = new PreMergeValidationService().ValidateStagedOverlay(forReview, [forReview]);
        shared.RecordPreMergeValidation(staged.StagedRecordId, overlay, forceApproved: false);
        shared.RecordDiffLaunch(staged.StagedRecordId, launched: true, "in-app merge review");

        // The operator's merge review wrote the reviewed result back with CRLF endings.
        await File.WriteAllTextAsync(fixture.ProgramFilePath, crlf);

        MonitorSettings mutationSettings = LoadSettings(fixture);
        ReviewDecisionWithIndexRefreshResult decision = new StagedDecisionWorkflow().Record(
            mutationSettings,
            new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(mutationSettings)),
            shared, // MUTATION: same instance again
            staged.StagedRecordId,
            "accepted",
            staged.StagedHash,
            "AIMonitor.Integration.Tests");

        Assert.Equal("accepted-normalized", decision.Classification);
        Assert.NotNull(decision.IndexRefresh);
        Assert.Equal("rebuilt", decision.IndexRefresh!.Status);
    }

    // ---------------------------------------------------------------------------------------
    // 2. Razor: a non-C#, non-indexed file type completes refresh -> stage -> review -> accept
    //    with the new markup landing on disk.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Razor_file_round_trips_through_working_stage_review_and_accept()
    {
        EngineFixture fixture = CreateFixture();
        string razorFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Pages", "Index.razor");
        Directory.CreateDirectory(Path.GetDirectoryName(razorFilePath)!);
        await File.WriteAllTextAsync(razorFilePath, "@page \"/\"\r\n<h1>Old</h1>\r\n");

        EditSessionStatus refresh = NewEditService(fixture).Refresh(razorFilePath);
        Assert.True(File.Exists(refresh.WorkingFilePath));
        await File.WriteAllTextAsync(refresh.WorkingFilePath, "@page \"/\"\r\n<h1>New</h1>\r\n");

        StagedEditRecord staged = NewEditService(fixture).Stage(razorFilePath);
        StagedEditRecord rehydrated = NewEditService(fixture).GetStagedRecord(staged.StagedRecordId);
        Assert.EndsWith(".razor", rehydrated.StagedFilePath, StringComparison.OrdinalIgnoreCase);

        MarkReviewedInApp(fixture, staged.StagedRecordId);
        File.Copy(rehydrated.StagedFilePath, razorFilePath, overwrite: true);

        ReviewDecisionWithIndexRefreshResult decision = RecordDecision(
            fixture,
            staged.StagedRecordId,
            "accepted",
            staged.StagedHash);

        Assert.Equal("accepted", decision.Classification);
        Assert.Contains("<h1>New</h1>", await File.ReadAllTextAsync(razorFilePath), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 3. CSS: same round trip for wwwroot/site.css, plus the staged copy keeps the original
    //    extension (the staged path is what the review surface opens).
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Css_file_round_trips_through_working_stage_review_and_accept()
    {
        EngineFixture fixture = CreateFixture();
        string cssFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "wwwroot", "site.css");
        Directory.CreateDirectory(Path.GetDirectoryName(cssFilePath)!);
        await File.WriteAllTextAsync(cssFilePath, "body {\r\n  color: black;\r\n}\r\n");

        EditSessionStatus refresh = NewEditService(fixture).Refresh(cssFilePath);
        Assert.True(File.Exists(refresh.WorkingFilePath));
        await File.WriteAllTextAsync(refresh.WorkingFilePath, "body {\r\n  color: #1d4ed8;\r\n}\r\n");

        StagedEditRecord staged = NewEditService(fixture).Stage(cssFilePath);
        StagedEditRecord rehydrated = NewEditService(fixture).GetStagedRecord(staged.StagedRecordId);
        Assert.EndsWith("site.css", rehydrated.StagedFilePath, StringComparison.OrdinalIgnoreCase);

        MarkReviewedInApp(fixture, staged.StagedRecordId);
        File.Copy(rehydrated.StagedFilePath, cssFilePath, overwrite: true);

        ReviewDecisionWithIndexRefreshResult decision = RecordDecision(
            fixture,
            staged.StagedRecordId,
            "accepted",
            staged.StagedHash);

        Assert.Equal("accepted", decision.Classification);
        Assert.Contains("#1d4ed8", await File.ReadAllTextAsync(cssFilePath), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 4. New file: preparing the review target creates a ZERO-BYTE watched file (so the diff
    //    surface has a left-hand side), and rejecting deletes it again.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task New_file_review_prep_creates_blank_watched_file_and_reject_cleans_it_up()
    {
        EngineFixture fixture = CreateFixture();
        string newFilePath = Path.Combine(
            Path.GetDirectoryName(fixture.ProgramFilePath)!,
            "Generated",
            "LaunchRejectedThing.cs");

        EditSessionStatus created = NewEditService(fixture).NewFile(newFilePath);
        Assert.True(created.IsNewFile);
        await File.WriteAllTextAsync(
            created.WorkingFilePath,
            "namespace Example.Generated { internal sealed class LaunchRejectedThing { } }");

        StagedEditRecord staged = NewEditService(fixture).Stage(newFilePath);
        StagedEditRecord rehydrated = NewEditService(fixture).GetStagedRecord(staged.StagedRecordId);
        Assert.True(rehydrated.IsNewFile);
        Assert.False(File.Exists(newFilePath));

        // PrepareReviewFileForLaunch runs inside the in-app review stamp.
        MarkReviewedInApp(fixture, staged.StagedRecordId);
        Assert.True(File.Exists(newFilePath));
        Assert.Equal(0, new FileInfo(newFilePath).Length);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(newFilePath));
        Assert.Equal(
            newFilePath,
            NewEditService(fixture).GetStagedRecord(staged.StagedRecordId).ReviewBaselineFilePath);

        ReviewDecisionWithIndexRefreshResult decision = RecordDecision(
            fixture,
            staged.StagedRecordId,
            "rejected",
            expectedStagedHash: null);

        Assert.Equal("rejected", decision.Classification);
        Assert.False(File.Exists(newFilePath));
    }

    // ---------------------------------------------------------------------------------------
    // 4b. The complement: reject is safe when review never ran, so there is no blank watched
    //     file to clean up. The delete is conditional on new-file AND zero length.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task New_file_rejects_when_watched_file_remains_missing()
    {
        EngineFixture fixture = CreateFixture();
        string newFilePath = Path.Combine(
            Path.GetDirectoryName(fixture.ProgramFilePath)!,
            "Generated",
            "RejectedThing.cs");

        EditSessionStatus created = NewEditService(fixture).NewFile(newFilePath);
        await File.WriteAllTextAsync(
            created.WorkingFilePath,
            "namespace Example.Generated { internal sealed class RejectedThing { } }");

        StagedEditRecord staged = NewEditService(fixture).Stage(newFilePath);
        Assert.False(File.Exists(newFilePath));

        ReviewDecisionWithIndexRefreshResult decision = RecordDecision(
            fixture,
            staged.StagedRecordId,
            "rejected",
            expectedStagedHash: null);

        Assert.Equal("rejected", decision.Classification);
        Assert.False(File.Exists(newFilePath));
    }

    // ---------------------------------------------------------------------------------------
    // 5. File-scoped index queries resolve a bare relative path against the WATCHED SOLUTION
    //    FOLDER, not the process working directory, and documents/symbols/references-in-file
    //    all agree on the same resolved file. Asserted against SolutionIndexQueryService itself,
    //    because the retired CLI resolved with Path.GetFullPath against the process cwd — the
    //    exact confusion this test exists to rule out.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void File_scoped_index_queries_resolve_against_the_watched_folder_not_the_process_directory()
    {
        EngineFixture fixture = CreateFixture();
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(LoadSettings(fixture));

        IReadOnlyList<IndexedDocumentRow> documents = service.ListDocuments(filePath: "Program.cs");
        IReadOnlyList<IndexedSymbolRow> symbols = service.ListSymbols("Program.cs", "Program");
        IReadOnlyList<IndexedReferenceRow> references = service.ListReferencesInFile("Program.cs");

        IndexedDocumentRow document = Assert.Single(documents);
        IndexedSymbolRow symbol = Assert.Single(symbols);
        IndexedReferenceRow reference = Assert.Single(references);

        // All three agree, and they agree on the file under the watched solution folder.
        Assert.Equal(fixture.ProgramFilePath, document.FilePath);
        Assert.Equal(fixture.ProgramFilePath, symbol.FilePath);
        Assert.Equal(fixture.ProgramFilePath, reference.FilePath);
        Assert.Equal(fixture.ProgramSymbolStableKey, reference.TargetStableKey);
        Assert.Equal("IdentifierName", reference.ReferenceKind);

        // The discrimination: "Program.cs" resolved against the process working directory is a
        // different path entirely, it does not exist, and asking for it returns nothing. If the
        // service resolved like the CLI did, the three queries above would have been empty.
        string processRelative = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Program.cs"));
        Assert.NotEqual(fixture.ProgramFilePath, processRelative);
        Assert.False(File.Exists(processRelative));
        Assert.Empty(service.ListDocuments(filePath: processRelative));
        Assert.Empty(service.ListSymbols(processRelative, "Program"));
        Assert.Empty(service.ListReferencesInFile(processRelative));
    }

    // ---------------------------------------------------------------------------------------
    // 6. Pre-merge (GATE 1) validation. These five were already engine-direct in the CLI suite —
    //    the CLI was only used to refresh and stage — so they port near verbatim.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Premerge_validation_reports_errors_only()
    {
        EngineFixture fixture = CreateFixture();

        EditSessionStatus refresh = NewEditService(fixture).Refresh(fixture.ProgramFilePath);
        await File.WriteAllTextAsync(
            refresh.WorkingFilePath,
            "#warning intentional warning should not be shown\r\nnamespace Example { internal static class Program { public static string Value => \"broken\" } }\r\n");

        StagedEditRecord staged = NewEditService(fixture).Stage(fixture.ProgramFilePath);

        PreMergeValidationResult validation = ValidateStaged(fixture, staged.StagedRecordId);
        Assert.Equal("failed", validation.Status);
        Assert.True(validation.IsError);
        Assert.True(validation.DiagnosticCount > 0);
        string diagnostics = string.Join(Environment.NewLine, validation.Diagnostics);
        Assert.Contains(": error ", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(": warning ", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intentional warning", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Premerge_validation_does_not_block_on_warnings_only()
    {
        EngineFixture fixture = CreateFixture();

        EditSessionStatus refresh = NewEditService(fixture).Refresh(fixture.ProgramFilePath);
        await File.WriteAllTextAsync(
            refresh.WorkingFilePath,
            "#warning intentional warning should not block\r\nnamespace Example { internal static class Program { public static string Value => \"warning-only\"; } }\r\n");

        StagedEditRecord staged = NewEditService(fixture).Stage(fixture.ProgramFilePath);

        PreMergeValidationResult validation = ValidateStaged(fixture, staged.StagedRecordId);
        Assert.Equal("passed", validation.Status);
        Assert.False(validation.IsError);
        Assert.Equal(0, validation.DiagnosticCount);
        Assert.Empty(validation.Diagnostics);
    }

    [Fact]
    public async Task Premerge_validation_excludes_runtime_when_runtime_is_under_watched_root()
    {
        EngineFixture fixture = CreateFixture(runtimeUnderWatchedRoot: true);
        string runtimeMarkerPath = Path.Combine(
            Path.GetDirectoryName(fixture.WatchedSolutionPath)!,
            "runtime",
            "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeMarkerPath)!);
        await File.WriteAllTextAsync(runtimeMarkerPath, "runtime state must not be copied into validation");

        EditSessionStatus refresh = NewEditService(fixture).Refresh(fixture.ProgramFilePath);
        await File.WriteAllTextAsync(
            refresh.WorkingFilePath,
            "namespace Example { internal static class Program { public static string Value => \"validation\"; } }");

        StagedEditRecord staged = NewEditService(fixture).Stage(fixture.ProgramFilePath);

        PreMergeValidationResult validation = ValidateStaged(fixture, staged.StagedRecordId);
        Assert.False(File.Exists(Path.Combine(validation.ValidationWorkspacePath, "runtime", "marker.txt")));
    }

    [Fact]
    public async Task Premerge_validation_blocks_when_staged_candidate_changes_after_stage()
    {
        EngineFixture fixture = CreateFixture();

        EditSessionStatus refresh = NewEditService(fixture).Refresh(fixture.ProgramFilePath);
        await File.WriteAllTextAsync(
            refresh.WorkingFilePath,
            "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");

        StagedEditRecord staged = NewEditService(fixture).Stage(fixture.ProgramFilePath);
        StagedEditRecord rehydrated = NewEditService(fixture).GetStagedRecord(staged.StagedRecordId);
        await File.WriteAllTextAsync(
            rehydrated.StagedFilePath,
            "namespace Example { internal static class Program { public static string Value => \"tampered\"; } }");

        PreMergeValidationResult validation = ValidateStaged(fixture, staged.StagedRecordId);
        Assert.Equal("staged-hash-mismatch", validation.Status);
        Assert.True(validation.IsError);
    }

    [Fact]
    public async Task Multi_file_staged_candidate_with_compile_error_blocks_without_mutating_watched_source()
    {
        EngineFixture fixture = CreateFixture();
        string helperFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Helper.cs");
        await File.WriteAllTextAsync(
            helperFilePath,
            "namespace Example { internal static class Helper { public static string Value() => \"old\"; } }");
        string originalProgramText = await File.ReadAllTextAsync(fixture.ProgramFilePath);
        string originalHelperText = await File.ReadAllTextAsync(helperFilePath);

        EditSessionStatus helperRefresh = NewEditService(fixture).Refresh(helperFilePath);
        await File.WriteAllTextAsync(
            helperRefresh.WorkingFilePath,
            "namespace Example { internal static class Helper { public static string Value() => \"candidate\"; } }");
        NewEditService(fixture).Stage(helperFilePath);

        EditSessionStatus programRefresh = NewEditService(fixture).Refresh(fixture.ProgramFilePath);
        await File.WriteAllTextAsync(
            programRefresh.WorkingFilePath,
            "namespace Example { internal static class Program { public static string Value => Helper.Value() } }");
        StagedEditRecord programStaged = NewEditService(fixture).Stage(fixture.ProgramFilePath);

        // Staging and validating a multi-file session must leave the working tree untouched.
        // ADR-0005 extends the same guarantee across the operator's per-file accepts: no file in
        // a session reaches watched source until the terminal accept writes them all together
        // (asserted against the writer itself in EngineReviewSessionAtomicityTests).
        PreMergeValidationResult validation = ValidateStaged(fixture, programStaged.StagedRecordId);
        Assert.Equal("failed", validation.Status);
        Assert.Equal(originalProgramText, await File.ReadAllTextAsync(fixture.ProgramFilePath));
        Assert.Equal(originalHelperText, await File.ReadAllTextAsync(helperFilePath));
    }

    // =======================================================================================
    // Helpers
    // =======================================================================================

    // A NEW service at every seam the CLI crossed as a new process. See the class comment: this
    // is the whole reason these tests still cover disk rehydration. Do not hoist it into a field.
    private static WorkflowEditService NewEditService(EngineFixture fixture)
    {
        return new WorkflowEditService(LoadSettings(fixture));
    }

    private static MonitorSettings LoadSettings(EngineFixture fixture)
    {
        return MonitorSettingsLoader.Load(fixture.RepositoryRoot, fixture.SettingsPath);
    }

    // The engine path behind the retired `edit record-decision` / `edit accept` verbs: the shared
    // StagedDecisionWorkflow the MCP server and the host both call. Fresh settings, fresh logger
    // and a fresh WorkflowEditService, exactly as a new CLI process would have had.
    private static ReviewDecisionWithIndexRefreshResult RecordDecision(
        EngineFixture fixture,
        string stagedRecordId,
        string decision,
        string? expectedStagedHash)
    {
        MonitorSettings settings = LoadSettings(fixture);
        IMonitorLogger logger = new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(settings));
        return new StagedDecisionWorkflow().Record(
            settings,
            logger,
            new WorkflowEditService(settings),
            stagedRecordId,
            decision,
            expectedStagedHash,
            "AIMonitor.Integration.Tests");
    }

    // Pre-merge (GATE 1) validation used to be reachable through the retired `edit launch-diff`
    // command. The engine service behind it is unchanged, so assert against it directly.
    private static PreMergeValidationResult ValidateStaged(EngineFixture fixture, string stagedRecordId)
    {
        MonitorSettings settings = LoadSettings(fixture);
        StagedEditRecord record = new WorkflowEditService(settings).GetStagedRecord(stagedRecordId);
        return new PreMergeValidationService().Validate(settings, record);
    }

    // Accepting still requires a recorded review; the product records it in-app. See InAppReviewSimulator.
    private static void MarkReviewedInApp(EngineFixture fixture, string stagedRecordId)
    {
        MonitorSettings settings = LoadSettings(fixture);
        InAppReviewSimulator.MarkReviewed(
            settings.RepositoryRoot,
            settings.WatchedSolutionPath,
            settings.RuntimeRoot,
            stagedRecordId);
    }

    private static EngineFixture CreateFixture(bool runtimeUnderWatchedRoot = false)
    {
        string repositoryRoot = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorEngineEditTests", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(tempRoot, "config", "appsettings.json");
        string watchedSolutionPath = Path.Combine(tempRoot, "Watched", "Example.csproj");
        string programFilePath = Path.Combine(tempRoot, "Watched", "Program.cs");
        string programSymbolStableKey = "symbol:program";

        Directory.CreateDirectory(Path.GetDirectoryName(watchedSolutionPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            watchedSolutionPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(programFilePath, "namespace Example { internal static class Program { } }");

        MonitorSettingsLoader.SaveLocal(
            repositoryRoot,
            watchedSolutionPath,
            runtimeUnderWatchedRoot
                ? Path.Combine(tempRoot, "Watched", "runtime")
                : Path.Combine(tempRoot, "runtime"),
            settingsPath);
        MonitorSettings settings = MonitorSettingsLoader.Load(repositoryRoot, settingsPath);
        SolutionIndexStore store = new(new SolutionIndexDatabase(MonitorDataPaths.GetDefaultIndexDatabasePath(settings)));
        store.SaveSnapshot(new MSBuildSolutionSnapshot(
            watchedSolutionPath,
            [
                new MSBuildProjectSnapshot(
                    "project:example",
                    "Example",
                    Path.Combine(tempRoot, "Watched", "Example.csproj"),
                    "C#",
                    "net10.0",
                    "",
                    "Exe",
                    "Microsoft.NET.Sdk",
                    "Example",
                    "Example",
                    "enable",
                    "enable",
                    "latest",
                    [
                        new MSBuildDocumentSnapshot("document:program", "Program.cs", programFilePath, [])
                    ],
                    [
                        new MSBuildSymbolSnapshot(
                            programSymbolStableKey,
                            "Program",
                            "NamedType",
                            "Example",
                            "",
                            programFilePath,
                            1,
                            1,
                            "Example.Program")
                    ],
                    [
                        new MSBuildReferenceSnapshot(
                            programSymbolStableKey,
                            programFilePath,
                            1,
                            45,
                            "IdentifierName",
                            "Program")
                    ],
                    [],
                    [],
                    [new MSBuildPackageReferenceSnapshot("Microsoft.Data.Sqlite", "10.0.0")],
                    [],
                    [],
                    ["DEBUG"])
            ],
            []));

        return new EngineFixture(
            repositoryRoot,
            settingsPath,
            watchedSolutionPath,
            programFilePath,
            programSymbolStableKey);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClaudeWorkbench.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find ClaudeWorkbench.slnx.");
    }

    private static int CountBareLf(string text)
    {
        string withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        return withoutCrLf.Count(character => character == '\n');
    }

    private sealed record EngineFixture(
        string RepositoryRoot,
        string SettingsPath,
        string WatchedSolutionPath,
        string ProgramFilePath,
        string ProgramSymbolStableKey);
}
