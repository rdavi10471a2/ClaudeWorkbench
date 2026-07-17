namespace AIMonitor.Workflow;

public sealed class StagedEditSummary
{
    public string StagedRecordId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string WatchedFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SupersededByStagedRecordId { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string StagedHash { get; set; } = string.Empty;

    public string LaunchStatus { get; set; } = string.Empty;

    public string RecordPath { get; set; } = string.Empty;

    public string CreatedAtUtc { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
