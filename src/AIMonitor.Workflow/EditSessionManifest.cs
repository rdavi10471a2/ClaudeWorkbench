namespace AIMonitor.Workflow;

public sealed class EditSessionManifest
{
    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string OriginalHash { get; set; } = string.Empty;

    public string OriginalNormalizedHash { get; set; } = string.Empty;

    public bool IsNewFile { get; set; }

    public bool RequiresRefresh { get; set; }

    public bool IndexStale { get; set; }

    public string RefreshedAtUtc { get; set; } = string.Empty;

    public string LastRetrievalBackupPath { get; set; } = string.Empty;

    public string LastRetrievalBackupHash { get; set; } = string.Empty;

    public string LastRetrievalBackupAtUtc { get; set; } = string.Empty;

    public string LastDecision { get; set; } = string.Empty;

    public string LastDecisionAtUtc { get; set; } = string.Empty;

    public string LastCompareRunId { get; set; } = string.Empty;

    public string LastCompareSnapshotPath { get; set; } = string.Empty;

    public string LastLedgerPath { get; set; } = string.Empty;

    public string LastStagedRecordId { get; set; } = string.Empty;

    public string LastStagedRecordPath { get; set; } = string.Empty;

    public string ManifestJson { get; set; } = string.Empty;

    public int OperationCount { get; set; }

    public EditSyntaxValidationResult? LastSyntaxValidation { get; set; }

    public EditOverlayValidationResult? LastOverlayValidation { get; set; }
}
