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

    // ADR-0005: the edit session is the atomic unit, so a per-file operator Accept is an
    // APPROVAL ("approved") and writes nothing; every approved file in the session is written
    // together on the terminal accept. That makes "has this record's bytes reached watched
    // source?" a real question, and it is answered here as a RECORDED FACT stamped at the
    // moment of the write — never inferred from Decision/Status/Classification, because those
    // move for reasons that have nothing to do with the filesystem.
    //
    // Empty = never written. Records written by older builds (which wrote per accept) also
    // deserialize to empty, which is safe: the only consumer is the terminal accept's
    // "approved AND unwritten" write set, and those records carry Decision "accepted", not
    // "approved", so they are never in it.
    public string WrittenAtUtc { get; set; } = string.Empty;

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
