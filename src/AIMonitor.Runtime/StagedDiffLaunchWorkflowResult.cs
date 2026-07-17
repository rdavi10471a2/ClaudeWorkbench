using AIMonitor.Workflow;

namespace AIMonitor.Runtime;

public sealed class StagedDiffLaunchWorkflowResult
{
    public StagedEditSummary StagedRecordSummary { get; set; } = new();

    public StagedEditRecord? StagedRecord { get; set; }

    public PreMergeValidationResult PreMergeValidation { get; set; } = new();

    public DiffLaunchResult DiffLaunch { get; set; } = new();

    public string NextStep { get; set; } = string.Empty;
}
