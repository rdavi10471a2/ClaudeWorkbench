namespace AIMonitor.Workflow;

public sealed class StagedEditRecord
{
    public string StagedRecordId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string StagedFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string OriginalHash { get; set; } = string.Empty;

    public string OriginalNormalizedHash { get; set; } = string.Empty;

    public bool IsNewFile { get; set; }

    public string ReviewBaselineFilePath { get; set; } = string.Empty;

    public string StagedHash { get; set; } = string.Empty;

    public string StagedNormalizedHash { get; set; } = string.Empty;

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SupersededByStagedRecordId { get; set; } = string.Empty;

    public string SupersededAtUtc { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;

    public string DecisionAtUtc { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string LaunchStatus { get; set; } = string.Empty;

    public string LaunchMessage { get; set; } = string.Empty;

    public string LaunchedAtUtc { get; set; } = string.Empty;

    public string PreMergeValidationStatus { get; set; } = string.Empty;

    public bool PreMergeValidationIsError { get; set; }

    public bool PreMergeValidationForceApproved { get; set; }

    public int PreMergeValidationDiagnosticCount { get; set; }

    public string PreMergeValidationAtUtc { get; set; } = string.Empty;

    public string LastCompareRunId { get; set; } = string.Empty;

    public string LastCompareSnapshotPath { get; set; } = string.Empty;

    public string LastLedgerPath { get; set; } = string.Empty;
}
