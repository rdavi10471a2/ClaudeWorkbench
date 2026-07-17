namespace AIMonitor.Workflow;

public sealed class EditSessionStatus
{
    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public bool HasSession { get; set; }

    public bool WatchedFileExists { get; set; }

    public bool WorkingFileExists { get; set; }

    public bool IsNewFile { get; set; }

    public bool RequiresRefresh { get; set; }

    public bool IndexStale { get; set; }

    public string OriginalHash { get; set; } = string.Empty;

    public string LastRetrievalBackupPath { get; set; } = string.Empty;

    public string LastRetrievalBackupHash { get; set; } = string.Empty;

    public string LastRetrievalBackupAtUtc { get; set; } = string.Empty;

    public string StagedHash { get; set; } = string.Empty;

    public string WatchedHash { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string LastCompareRunId { get; set; } = string.Empty;

    public string LastCompareSnapshotPath { get; set; } = string.Empty;

    public string LastLedgerPath { get; set; } = string.Empty;

    public string LastStagedRecordId { get; set; } = string.Empty;

    public string LastStagedRecordPath { get; set; } = string.Empty;

    public string LastDecision { get; set; } = string.Empty;

    public string LastDecisionAtUtc { get; set; } = string.Empty;

    public string ManifestJson { get; set; } = string.Empty;

    public int OperationCount { get; set; }

    public EditSyntaxValidationResult? SyntaxValidation { get; set; }

    public EditOverlayValidationResult? OverlayValidation { get; set; }
}
