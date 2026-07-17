namespace AIMonitor.Workflow;

public sealed class CompareSnapshotResult
{
    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string ProposedSnapshotPath { get; set; } = string.Empty;

    public string LedgerPath { get; set; } = string.Empty;

    public string RunLogPath { get; set; } = string.Empty;

    public string TelemetryPath { get; set; } = string.Empty;

    public string RunId { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool HasDifferences { get; set; }
}
