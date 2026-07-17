using AIMonitor.Core;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Workflow;

namespace AIMonitor.Indexing.Tests;

public sealed class StagedDecisionWorkflowTests
{
    [Fact]
    public async Task RebuildAsync_clears_index_stale_flags_after_successful_manual_rebuild()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorIndexingTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string sourcePath = Path.Combine(watchedRoot, "Program.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(
            projectPath,
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
        File.WriteAllText(sourcePath, "namespace Example { internal static class Program { } }");

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);
        WorkflowEditService workflowService = new(settings);
        EditSessionStatus refresh = workflowService.Refresh(sourcePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");
        StagedEditRecord record = workflowService.Stage(sourcePath);
        workflowService.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        workflowService.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, sourcePath, overwrite: true);

        workflowService.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);

        Assert.True(workflowService.GetStatus(sourcePath).IndexStale);

        await new SolutionIndexRebuildService().RebuildAsync(settings);

        Assert.False(workflowService.GetStatus(sourcePath).IndexStale);
    }

    [Fact]
    public void Record_reports_stale_index_when_post_accept_rebuild_fails()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorIndexingTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string missingProjectPath = Path.Combine(watchedRoot, "Missing.csproj");
        string sourcePath = Path.Combine(watchedRoot, "Program.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(sourcePath, "namespace Example { internal static class Program { } }");

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, missingProjectPath, runtimeRoot);
        WorkflowEditService workflowService = new(settings);
        EditSessionStatus refresh = workflowService.Refresh(sourcePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");
        StagedEditRecord record = workflowService.Stage(sourcePath);
        workflowService.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        workflowService.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, sourcePath, overwrite: true);

        ReviewDecisionWithIndexRefreshResult result = new StagedDecisionWorkflow().Record(
            settings,
            NullMonitorLogger.Instance,
            workflowService,
            record.StagedRecordId,
            "accepted",
            record.StagedHash,
            "AIMonitor.Indexing.Tests");

        Assert.Equal("accepted", result.Classification);
        Assert.NotNull(result.IndexRefresh);
        Assert.True(result.IndexRefresh.IsError);
        Assert.Contains("index rows are stale", result.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.True(workflowService.GetStatus(sourcePath).IndexStale);
    }

    [Fact]
    public void Record_blocks_terminal_planned_accept_when_accepted_overlay_does_not_build()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorIndexingTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string providerPath = Path.Combine(watchedRoot, "Provider.cs");
        string consumerPath = Path.Combine(watchedRoot, "Consumer.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(
            projectPath,
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
        File.WriteAllText(
            providerPath,
            """
            namespace Example;

            public static class Provider
            {
                public static string Value => "original";
            }
            """);
        File.WriteAllText(
            consumerPath,
            """
            namespace Example;

            public static class Consumer
            {
                public static string Read()
                {
                    return Provider.Value;
                }
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);
        WorkflowEditService workflowService = new(settings);
        EditSessionStatus providerRefresh = workflowService.Refresh(providerPath);
        EditSessionStatus consumerRefresh = workflowService.Refresh(consumerPath);
        File.WriteAllText(
            providerRefresh.WorkingFilePath,
            """
            namespace Example;

            public static class Provider
            {
                public static string RenamedValue => "candidate";
            }
            """);
        File.WriteAllText(
            consumerRefresh.WorkingFilePath,
            """
            namespace Example;

            public static class Consumer
            {
                public static string Read()
                {
                    return Provider.Value + ":candidate";
                }
            }
            """);

        StagedEditRecord providerRecord = workflowService.Stage(providerPath, sessionId: "planned-session");
        StagedEditRecord consumerRecord = workflowService.Stage(consumerPath, sessionId: "planned-session");
        PreMergeValidationResult stagedOverlayReady = new()
        {
            Status = "planned-staged-overlay-ready",
            IsError = false
        };
        workflowService.RecordPreMergeValidation(providerRecord.StagedRecordId, stagedOverlayReady, forceApproved: false);
        workflowService.RecordPreMergeValidation(consumerRecord.StagedRecordId, stagedOverlayReady, forceApproved: false);
        workflowService.RecordDiffLaunch(providerRecord.StagedRecordId, launched: true, "test launch");
        workflowService.RecordDiffLaunch(consumerRecord.StagedRecordId, launched: true, "test launch");
        File.Copy(providerRecord.StagedFilePath, providerPath, overwrite: true);

        ReviewDecisionWithIndexRefreshResult firstAccept = new StagedDecisionWorkflow().Record(
            settings,
            NullMonitorLogger.Instance,
            workflowService,
            providerRecord.StagedRecordId,
            "accepted",
            providerRecord.StagedHash,
            "AIMonitor.Indexing.Tests",
            deferIndexRefresh: true);

        Assert.Equal("deferred", firstAccept.IndexRefresh?.Status);

        File.Copy(consumerRecord.StagedFilePath, consumerPath, overwrite: true);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new StagedDecisionWorkflow().Record(
            settings,
            NullMonitorLogger.Instance,
            workflowService,
            consumerRecord.StagedRecordId,
            "accepted",
            consumerRecord.StagedHash,
            "AIMonitor.Indexing.Tests",
            deferIndexRefresh: false,
            refreshPlan: new PostAcceptIndexRefreshPlan
            {
                ChangedFilePaths = [providerPath, consumerPath],
                OwningProjectPaths = [projectPath]
            },
            terminalValidationRecords:
            [
                workflowService.GetStagedRecord(providerRecord.StagedRecordId),
                workflowService.GetStagedRecord(consumerRecord.StagedRecordId)
            ]));

        Assert.Contains("Terminal planned pre-merge validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, workflowService.GetStagedRecord(consumerRecord.StagedRecordId).Decision);
    }

    [Fact]
    public async Task Record_allows_terminal_planned_accept_when_force_approved_despite_failed_overlay()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorIndexingTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string providerPath = Path.Combine(watchedRoot, "Provider.cs");
        string consumerPath = Path.Combine(watchedRoot, "Consumer.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(
            projectPath,
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
        File.WriteAllText(
            providerPath,
            """
            namespace Example;

            public static class Provider
            {
                public static string Value => "original";
            }
            """);
        File.WriteAllText(
            consumerPath,
            """
            namespace Example;

            public static class Consumer
            {
                public static string Read()
                {
                    return Provider.Value;
                }
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);

        // Build the index from the original (compiling) sources so the post-accept refresh probe has its tables.
        await new SolutionIndexRebuildService().RebuildAsync(settings);

        WorkflowEditService workflowService = new(settings);
        EditSessionStatus providerRefresh = workflowService.Refresh(providerPath);
        EditSessionStatus consumerRefresh = workflowService.Refresh(consumerPath);

        // The provider renames the symbol the consumer reads, so the overlaid batch will NOT build — same break
        // the blocking test relies on. Here the operator force-approves it before launch.
        File.WriteAllText(
            providerRefresh.WorkingFilePath,
            """
            namespace Example;

            public static class Provider
            {
                public static string RenamedValue => "candidate";
            }
            """);
        File.WriteAllText(
            consumerRefresh.WorkingFilePath,
            """
            namespace Example;

            public static class Consumer
            {
                public static string Read()
                {
                    return Provider.Value + ":candidate";
                }
            }
            """);

        StagedEditRecord providerRecord = workflowService.Stage(providerPath, sessionId: "planned-session");
        StagedEditRecord consumerRecord = workflowService.Stage(consumerPath, sessionId: "planned-session");

        // GATE 1 reported an error, and the operator explicitly approved the override before launch. The terminal
        // record (consumer) carries PreMergeValidationForceApproved == true.
        PreMergeValidationResult overlayFailed = new()
        {
            Status = "failed",
            IsError = true
        };
        workflowService.RecordPreMergeValidation(providerRecord.StagedRecordId, overlayFailed, forceApproved: true);
        workflowService.RecordPreMergeValidation(consumerRecord.StagedRecordId, overlayFailed, forceApproved: true);
        workflowService.RecordDiffLaunch(providerRecord.StagedRecordId, launched: true, "test launch");
        workflowService.RecordDiffLaunch(consumerRecord.StagedRecordId, launched: true, "test launch");
        File.Copy(providerRecord.StagedFilePath, providerPath, overwrite: true);

        ReviewDecisionWithIndexRefreshResult firstAccept = new StagedDecisionWorkflow().Record(
            settings,
            NullMonitorLogger.Instance,
            workflowService,
            providerRecord.StagedRecordId,
            "accepted",
            providerRecord.StagedHash,
            "AIMonitor.Indexing.Tests",
            deferIndexRefresh: true);

        Assert.Equal("deferred", firstAccept.IndexRefresh?.Status);

        File.Copy(consumerRecord.StagedFilePath, consumerPath, overwrite: true);
        ReviewDecisionWithIndexRefreshResult terminalAccept = new StagedDecisionWorkflow().Record(
            settings,
            NullMonitorLogger.Instance,
            workflowService,
            consumerRecord.StagedRecordId,
            "accepted",
            consumerRecord.StagedHash,
            "AIMonitor.Indexing.Tests",
            deferIndexRefresh: false,
            refreshPlan: new PostAcceptIndexRefreshPlan
            {
                ChangedFilePaths = [providerPath, consumerPath],
                OwningProjectPaths = [projectPath]
            },
            terminalValidationRecords:
            [
                workflowService.GetStagedRecord(providerRecord.StagedRecordId),
                workflowService.GetStagedRecord(consumerRecord.StagedRecordId)
            ]);

        // Force-approved: the terminal accept is recorded instead of thrown, and the failed validation is carried.
        Assert.Equal("accepted", terminalAccept.Classification);
        Assert.NotNull(terminalAccept.TerminalPreMergeValidation);
        Assert.True(terminalAccept.TerminalPreMergeValidation!.IsError);
        Assert.Equal("accepted", workflowService.GetStagedRecord(consumerRecord.StagedRecordId).Decision);
    }

    [Fact]
    public async Task RebuildAfterAcceptedDecision_clears_stale_flag_for_accepted_razor_code_behind_sibling()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorIndexingTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string servicePath = Path.Combine(watchedRoot, "Service.cs");
        string codeBehindPath = Path.Combine(watchedRoot, "Widget.razor.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(
            projectPath,
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
        File.WriteAllText(
            servicePath,
            """
            namespace Example;

            public static class Service
            {
                public static string Value => "original";
            }
            """);
        File.WriteAllText(
            codeBehindPath,
            """
            namespace Example;

            public partial class Widget
            {
                public string Caption => "original";
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);
        await new SolutionIndexRebuildService().RebuildAsync(settings);

        WorkflowEditService workflowService = new(settings);

        // Accept the .razor.cs code-behind so its manifest is flagged IndexStale, simulating an accepted Razor sibling.
        StagedEditRecord codeBehindRecord = AcceptEdit(
            workflowService,
            codeBehindPath,
            """
            namespace Example;

            public partial class Widget
            {
                public string Caption => "candidate";
            }
            """);

        Assert.True(workflowService.GetStatus(codeBehindPath).IndexStale);

        // Accept the plain .cs file too, then run the post-accept refresh scoped to the whole owning project. The
        // refresh plan lists BOTH accepted files; the whole-project rebuild reindexed the .razor.cs as well, so the
        // fix must clear its stale flag even though .razor.cs is filtered out of the cheap-path file list.
        StagedEditRecord serviceRecord = AcceptEdit(
            workflowService,
            servicePath,
            """
            namespace Example;

            public static class Service
            {
                public static string Value => "candidate";
            }
            """);

        PostAcceptIndexRefreshResult refresh = new PostAcceptIndexRefreshService().RebuildAfterAcceptedDecision(
            settings,
            NullMonitorLogger.Instance,
            serviceRecord,
            "AIMonitor.Indexing.Tests",
            new PostAcceptIndexRefreshPlan
            {
                ChangedFilePaths = [servicePath, codeBehindPath],
                OwningProjectPaths = [projectPath]
            });

        Assert.False(refresh.IsError, refresh.Message);
        Assert.Equal("project", refresh.RefreshMode);
        Assert.False(workflowService.GetStatus(codeBehindPath).IndexStale);
        _ = codeBehindRecord;
    }

    private static StagedEditRecord AcceptEdit(WorkflowEditService workflowService, string watchedFilePath, string candidate)
    {
        EditSessionStatus refresh = workflowService.Refresh(watchedFilePath);
        File.WriteAllText(refresh.WorkingFilePath, candidate);
        StagedEditRecord record = workflowService.Stage(watchedFilePath);
        workflowService.RecordPreMergeValidation(
            record.StagedRecordId,
            new PreMergeValidationResult { Status = "passed", IsError = false },
            forceApproved: false);
        workflowService.RecordDiffLaunch(record.StagedRecordId, launched: true, "test launch");
        File.Copy(record.StagedFilePath, watchedFilePath, overwrite: true);
        workflowService.RecordDecision(record.StagedRecordId, "accepted", record.StagedHash);
        return workflowService.GetStagedRecord(record.StagedRecordId);
    }

    private sealed class NullMonitorLogger : IMonitorLogger
    {
        public static readonly NullMonitorLogger Instance = new();

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
