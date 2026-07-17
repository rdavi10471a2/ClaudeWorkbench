using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

public sealed class WorkflowEditServiceSafetyTests
{
    [Fact]
    public void Refresh_creates_exact_retrieval_backup_before_working_candidate()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        byte[] watchedBytes = "namespace Example { internal static class Program { public static string Value => \"watched\"; } }"u8.ToArray();
        File.WriteAllBytes(fixture.ProgramFilePath, watchedBytes);

        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);

        Assert.False(string.IsNullOrWhiteSpace(refresh.LastRetrievalBackupPath));
        Assert.True(File.Exists(refresh.LastRetrievalBackupPath));
        Assert.StartsWith(fixture.Settings.RuntimeRoot, refresh.LastRetrievalBackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("retrieval-backups", "Program.cs"), refresh.LastRetrievalBackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".cs.bak", refresh.LastRetrievalBackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(watchedBytes, File.ReadAllBytes(refresh.LastRetrievalBackupPath));
        Assert.Equal(refresh.OriginalHash, refresh.LastRetrievalBackupHash);
        Assert.False(string.IsNullOrWhiteSpace(refresh.LastRetrievalBackupAtUtc));
    }

    [Fact]
    public void NewFile_does_not_create_retrieval_backup()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        string newFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Generated.cs");

        EditSessionStatus newFile = service.NewFile(newFilePath);

        Assert.True(string.IsNullOrWhiteSpace(newFile.LastRetrievalBackupPath));
        Assert.True(string.IsNullOrWhiteSpace(newFile.LastRetrievalBackupHash));
        Assert.False(Directory.Exists(Path.Combine(
            MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(fixture.Settings),
            "retrieval-backups")));
    }

    [Fact]
    public void Accepted_decision_requires_recorded_premerge_validation()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash));
        Assert.Contains("pre-merge validation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepted_decision_requires_successful_diff_review_launch()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash));
        Assert.Contains("successful diff review launch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepted_decision_rejects_failed_validation_without_force_approval()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "failed", IsError = true, DiagnosticCount = 1 },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash));
        Assert.Contains("failed pre-merge validation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepted_decision_rejects_dirty_unexpected_watched_source()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.WriteAllText(fixture.ProgramFilePath, "namespace Example { internal static class Program { public static string Value => \"external\"; } }");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash));
        Assert.Contains("Cannot accept", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepted_decision_succeeds_after_force_approved_failed_validation()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "failed", IsError = true, DiagnosticCount = 1 },
            forceApproved: true);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);

        StagedEditRecord accepted = service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);

        Assert.Equal("accepted", accepted.Classification);
        Assert.True(service.GetStatus(fixture.ProgramFilePath).RequiresRefresh);
    }

    [Fact]
    public void RecordDecision_rejects_terminal_record_reuse()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordDecision(record.StagedRecordId, "rejected");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(record.StagedRecordId, "rejected"));
        Assert.Contains("already has a final decision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restaging_same_file_supersedes_prior_active_record()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"first\"; } }");

        StagedEditRecord first = service.Stage(fixture.ProgramFilePath, sessionId: "session-a");
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"second\"; } }");

        StagedEditRecord second = service.Stage(fixture.ProgramFilePath, sessionId: "session-a");
        StagedEditRecord superseded = service.GetStagedRecord(first.StagedRecordId);

        Assert.Equal("superseded", superseded.Status);
        Assert.Equal("superseded", superseded.Classification);
        Assert.Equal(second.StagedRecordId, superseded.SupersededByStagedRecordId);
        Assert.Equal("staged", second.Status);
        Assert.Equal("session-a", second.SessionId);
    }

    [Fact]
    public void Superseded_record_cannot_launch_or_record_decision()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"first\"; } }");
        StagedEditRecord first = service.Stage(fixture.ProgramFilePath, sessionId: "session-a");
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"second\"; } }");
        service.Stage(fixture.ProgramFilePath, sessionId: "session-a");

        InvalidOperationException launch = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDiffLaunch(first.StagedRecordId, launched: true, "old launch"));
        InvalidOperationException decision = Assert.Throws<InvalidOperationException>(() =>
            service.RecordDecision(first.StagedRecordId, "rejected"));

        Assert.Contains("superseded", launch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("superseded", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListStagedRecords_filters_by_session()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"first\"; } }");
        StagedEditRecord first = service.Stage(fixture.ProgramFilePath, sessionId: "session-a");

        string secondFile = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Other.cs");
        File.WriteAllText(secondFile, "namespace Example { internal static class Other { } }");
        EditSessionStatus secondRefresh = service.Refresh(secondFile);
        File.WriteAllText(secondRefresh.WorkingFilePath, "namespace Example { internal static class Other { public static string Value => \"other\"; } }");
        StagedEditRecord second = service.Stage(secondFile, sessionId: "session-b");

        IReadOnlyList<StagedEditRecord> sessionA = service.ListStagedRecords("session-a");
        IReadOnlyList<StagedEditRecord> sessionB = service.ListStagedRecords("session-b");

        Assert.Contains(sessionA, record => record.StagedRecordId == first.StagedRecordId);
        Assert.DoesNotContain(sessionA, record => record.StagedRecordId == second.StagedRecordId);
        Assert.Contains(sessionB, record => record.StagedRecordId == second.StagedRecordId);
    }

    [Fact]
    public void Text_edit_can_defer_overlay_validation_until_planned_working_set_is_complete()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);

        ReplaceTextResult result = service.ReplaceText(
            fixture.ProgramFilePath,
            "internal static class Program { }",
            "internal static class Program { public static string Value => \"planned\"; }",
            expectedMatches: 1,
            validateOverlay: false);

        Assert.Equal("planned-overlay-pending", result.OverlayValidation?.Status);
        Assert.False(result.OverlayValidation?.HasErrors);
        Assert.Contains("\"planned\"", File.ReadAllText(refresh.WorkingFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Planned_overlay_retry_reports_and_recovers_three_file_cross_reference_errors()
    {
        ThreeFileWorkflowFixture fixture = CreateThreeFileFixture();
        WorkflowEditService service = new(fixture.Settings);
        service.Refresh(fixture.ProviderPath);
        service.Refresh(fixture.ConsumerPath);
        service.Refresh(fixture.PresenterPath);

        EditSessionStatus providerStatus = service.SubmitFile(
            fixture.ProviderPath,
            """
            namespace Example;

            internal static class Provider
            {
                public static string RenamedValue()
                {
                    return "candidate";
                }
            }
            """,
            validateOverlay: false);

        Assert.Equal("planned-overlay-pending", providerStatus.OverlayValidation?.Status);

        // The parent symbol was renamed and one child has been updated, but the second
        // child still points at the old parent member. The overlay should make that
        // missed blast-radius file visible before review.
        EditSessionStatus consumerStatus = service.SubmitFile(
            fixture.ConsumerPath,
            """
            namespace Example;

            internal static class Consumer
            {
                public static string Read()
                {
                    return Provider.RenamedValue();
                }
            }
            """);

        Assert.NotNull(consumerStatus.OverlayValidation);
        Assert.True(consumerStatus.OverlayValidation.HasErrors);
        Assert.Equal(3, consumerStatus.OverlayValidation.OverlayFileCount);
        Assert.Contains(consumerStatus.OverlayValidation.Diagnostics, diagnostic =>
            diagnostic.Path.Equals(fixture.PresenterPath, StringComparison.OrdinalIgnoreCase)
            && diagnostic.Id == "CS0117");

        // The agent can now repair the missed child Working candidate and retry the
        // overlay without touching watched source or force-launching review.
        EditSessionStatus presenterStatus = service.SubmitFile(
            fixture.PresenterPath,
            """
            namespace Example;

            internal static class Presenter
            {
                public static string Render()
                {
                    return Consumer.Read() + Provider.RenamedValue();
                }
            }
            """);

        Assert.NotNull(presenterStatus.OverlayValidation);
        Assert.False(presenterStatus.OverlayValidation.HasErrors);
        Assert.Equal("compiled", presenterStatus.OverlayValidation.Status);
        Assert.Equal(3, presenterStatus.OverlayValidation.OverlayFileCount);
    }

    [Fact]
    public void Stage_blocks_after_accept_until_refresh()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);
        service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.Stage(fixture.ProgramFilePath));
        Assert.Contains("refresh_file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refresh_preserves_index_stale_after_accept_until_rebuild_marks_fresh()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);
        service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);

        Assert.True(service.GetStatus(fixture.ProgramFilePath).IndexStale);

        EditSessionStatus refreshed = service.Refresh(fixture.ProgramFilePath);

        Assert.True(refreshed.IndexStale);
        Assert.Equal("index-stale", refreshed.Classification);

        service.MarkIndexFresh(fixture.ProgramFilePath);

        Assert.False(service.GetStatus(fixture.ProgramFilePath).IndexStale);
    }

    [Fact]
    public void Roslyn_typed_edit_blocks_after_accept_until_refresh()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);
        service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);

        RoslynEditService roslyn = new(fixture.Settings);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            roslyn.AddMethod(
                fixture.ProgramFilePath,
                "Program",
                "public static string Extra() => \"blocked\";"));
        Assert.Contains("refresh_file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Roslyn_typed_edit_succeeds_after_refresh_and_index_rebuild()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        StagedEditRecord record = StageChangedCandidate(service, fixture);

        service.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        service.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, fixture.ProgramFilePath, overwrite: true);
        service.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);
        service.MarkIndexFresh(fixture.ProgramFilePath);
        service.Refresh(fixture.ProgramFilePath);

        RoslynEditService roslyn = new(fixture.Settings);
        RoslynEditResult result = roslyn.AddMethod(
            fixture.ProgramFilePath,
            "Program",
            "public static string Extra() => \"allowed\";");

        Assert.Equal("updated", result.Status);
        Assert.Contains("Extra", File.ReadAllText(result.WorkingFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceText_honors_occurrence_index_without_adapter_file_writes()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        string textFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Notes.txt");
        File.WriteAllText(textFilePath, "one fish one fish");
        EditSessionStatus refresh = service.Refresh(textFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "one fish one fish");

        ReplaceTextResult result = service.ReplaceText(
            textFilePath,
            "one",
            "two",
            expectedMatches: 2,
            occurrenceIndex: 1);

        Assert.True(result.Changed);
        Assert.Equal("one fish two fish", File.ReadAllText(refresh.WorkingFilePath));
    }

    [Fact]
    public void ReplaceSpan_uses_crlf_aware_line_columns()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        string textFilePath = Path.Combine(Path.GetDirectoryName(fixture.ProgramFilePath)!, "Notes.txt");
        File.WriteAllText(textFilePath, "first\r\nsecond\r\nthird\r\n");
        EditSessionStatus refresh = service.Refresh(textFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "first\r\nsecond\r\nthird\r\n");

        TextSpanResult span = service.FindTextSpan(textFilePath, "second");
        EditSessionStatus status = service.ReplaceSpan(
            textFilePath,
            span.StartLine,
            span.StartColumn,
            span.EndLine,
            span.EndColumn,
            "changed",
            expectedOldTextHash: span.TextHash,
            expectedOldText: "second");

        Assert.Equal("pending", status.Classification);
        Assert.Equal("first\r\nchanged\r\nthird\r\n", File.ReadAllText(refresh.WorkingFilePath));
    }

    [Fact]
    public void SubmitFile_reports_operation_manifest_syntax_and_overlay_feedback()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);

        EditSessionStatus status = service.SubmitFile(
            fixture.ProgramFilePath,
            "namespace Example { internal static class Program { public static string Value => Missing.Value; } }",
            """{"intent":"phase6"}""");

        Assert.Equal(1, status.OperationCount);
        Assert.Equal("""{"intent":"phase6"}""", status.ManifestJson);
        Assert.NotNull(status.SyntaxValidation);
        Assert.False(status.SyntaxValidation.HasErrors);
        Assert.NotNull(status.OverlayValidation);
        Assert.True(status.OverlayValidation.HasErrors);
        Assert.Equal("compiled-with-errors", status.OverlayValidation.Status);
        Assert.Contains(status.OverlayValidation.Diagnostics, diagnostic => diagnostic.Id == "CS0103");
    }

    [Fact]
    public void SubmitFile_rejects_invalid_csharp_syntax_before_writing_candidate()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        string originalWorkingText = File.ReadAllText(refresh.WorkingFilePath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            service.SubmitFile(fixture.ProgramFilePath, "namespace Example { internal static class Program { public static string Broken => ; } }"));

        Assert.Contains("C# syntax validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalWorkingText, File.ReadAllText(refresh.WorkingFilePath));
        Assert.Equal(0, service.GetStatus(fixture.ProgramFilePath).OperationCount);
    }

    [Fact]
    public void ReplaceText_and_find_span_report_counts_and_validation_feedback()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string First => \"same\"; public static string Second => \"same\"; } }");

        TextSpanResult span = service.FindTextSpan(fixture.ProgramFilePath, "\"same\"", occurrenceIndex: 1);
        ReplaceTextResult result = service.ReplaceText(
            fixture.ProgramFilePath,
            "\"same\"",
            "\"changed\"",
            occurrenceIndex: 1,
            manifestJson: """{"tool":"replace"}""");

        Assert.Equal(2, span.OccurrenceCount);
        Assert.Equal(2, result.TotalMatchCount);
        Assert.Equal(1, result.ReplacementCount);
        Assert.Equal(1, result.OperationCount);
        Assert.Equal("""{"tool":"replace"}""", result.ManifestJson);
        Assert.NotNull(result.SyntaxValidation);
        Assert.False(result.SyntaxValidation.HasErrors);
        Assert.NotNull(result.OverlayValidation);
    }

    [Fact]
    public void Roslyn_typed_edit_reports_overlay_feedback_and_operation_count()
    {
        WorkflowFixture fixture = CreateFixture();
        WorkflowEditService service = new(fixture.Settings);
        service.Refresh(fixture.ProgramFilePath);
        RoslynEditService roslyn = new(fixture.Settings);

        RoslynEditResult result = roslyn.AddMethod(
            fixture.ProgramFilePath,
            "Program",
            "public static string BrokenSemantic() => Missing.Value;",
            manifestJson: """{"tool":"add_method"}""");

        Assert.Equal("updated", result.Status);
        Assert.Equal(1, result.OperationCount);
        Assert.Equal("""{"tool":"add_method"}""", result.ManifestJson);
        Assert.NotNull(result.SyntaxValidation);
        Assert.False(result.SyntaxValidation.HasErrors);
        Assert.NotNull(result.OverlayValidation);
        Assert.True(result.OverlayValidation.HasErrors);
        Assert.Contains(result.OverlayValidation.Diagnostics, diagnostic => diagnostic.Id == "CS0103");
    }

    private static StagedEditRecord StageChangedCandidate(WorkflowEditService service, WorkflowFixture fixture)
    {
        EditSessionStatus refresh = service.Refresh(fixture.ProgramFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");
        return service.Stage(fixture.ProgramFilePath);
    }

    private static WorkflowFixture CreateFixture()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorWorkflowSafetyTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string programFilePath = Path.Combine(watchedRoot, "Program.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(programFilePath, "namespace Example { internal static class Program { } }");

        return new WorkflowFixture(
            MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot),
            programFilePath);
    }

    private static ThreeFileWorkflowFixture CreateThreeFileFixture()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorWorkflowSafetyTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string providerPath = Path.Combine(watchedRoot, "Provider.cs");
        string consumerPath = Path.Combine(watchedRoot, "Consumer.cs");
        string presenterPath = Path.Combine(watchedRoot, "Presenter.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            providerPath,
            """
            namespace Example;

            internal static class Provider
            {
                public static string Value()
                {
                    return "original";
                }
            }
            """);
        File.WriteAllText(
            consumerPath,
            """
            namespace Example;

            internal static class Consumer
            {
                public static string Read()
                {
                    return Provider.Value();
                }
            }
            """);
        File.WriteAllText(
            presenterPath,
            """
            namespace Example;

            internal static class Presenter
            {
                public static string Render()
                {
                    return Consumer.Read() + Provider.Value();
                }
            }
            """);

        return new ThreeFileWorkflowFixture(
            MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot),
            providerPath,
            consumerPath,
            presenterPath);
    }

    private sealed record WorkflowFixture(MonitorSettings Settings, string ProgramFilePath);

    private sealed record ThreeFileWorkflowFixture(
        MonitorSettings Settings,
        string ProviderPath,
        string ConsumerPath,
        string PresenterPath);
}
