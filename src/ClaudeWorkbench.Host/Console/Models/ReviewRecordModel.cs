namespace ClaudeWorkbench.Host.Console.Models;

public sealed class ReviewRecordModel
{
    public string StagedRecordId { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string DecisionStatus { get; init; } = string.Empty;

    public string CurrentText { get; init; } = string.Empty;

    public string ProposedText { get; init; } = string.Empty;

    public bool IsDecided { get; init; }

    public bool IsNewFile { get; init; }

    public bool IsSessionComplete { get; init; }

    public bool PreMergeValidationIsError { get; init; }

    public bool PreMergeValidationForceApproved { get; init; }

    public int PreMergeValidationDiagnosticCount { get; init; }

    public string PreMergeValidationStatus { get; init; } = string.Empty;

    public static ReviewRecordModel SessionComplete()
    {
        return new ReviewRecordModel { IsSessionComplete = true };
    }
}
