using AIMonitor.Workflow;

namespace AIMonitor.Indexing;

public sealed class ReviewDecisionWithIndexRefreshResult
{
    public string StagedRecordId { get; set; } = string.Empty;

    public string WatchedFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public StagedEditSummary? StagedRecordSummary { get; set; }

    public string StagedRecordPath { get; set; } = string.Empty;

    public StagedEditRecord? StagedRecord { get; set; }

    public PostAcceptIndexRefreshResult? IndexRefresh { get; set; }

    public PreMergeValidationResult? TerminalPreMergeValidation { get; set; }

    public string NextStep { get; set; } = string.Empty;
}
