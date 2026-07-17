namespace ClaudeWorkbench.Host.Console.Models;

public sealed class ReviewQueueItem
{
    public string StagedRecordId { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string CreatedAtUtc { get; init; } = string.Empty;

    public string LaunchStatus { get; init; } = string.Empty;

    public string PreMergeValidationStatus { get; init; } = string.Empty;
}
