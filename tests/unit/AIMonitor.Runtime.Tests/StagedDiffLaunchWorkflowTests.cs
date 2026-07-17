using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.Runtime;
using AIMonitor.Workflow;

namespace AIMonitor.Runtime.Tests;

public sealed class StagedDiffLaunchWorkflowTests
{
    [Fact]
    public void Launch_rejects_terminal_rejected_record_without_recreating_new_file_target()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorRuntimeTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string newFilePath = Path.Combine(watchedRoot, "Generated.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);
        WorkflowEditService workflowService = new(settings);
        EditSessionStatus newFile = workflowService.NewFile(newFilePath);
        File.WriteAllText(newFile.WorkingFilePath, "namespace Example; public sealed class Generated { }");
        StagedEditRecord record = workflowService.Stage(newFilePath);
        workflowService.RecordDecision(record.StagedRecordId, "rejected");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new StagedDiffLaunchWorkflow().Launch(
                settings,
                NullMonitorLogger.Instance,
                workflowService,
                record.StagedRecordId,
                "AIMonitor.Runtime.Tests"));

        Assert.Contains("already has a final decision", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(newFilePath));
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
