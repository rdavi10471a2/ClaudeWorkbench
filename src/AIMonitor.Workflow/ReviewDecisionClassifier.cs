using AIMonitor.Core;

namespace AIMonitor.Workflow;

public sealed class ReviewDecisionClassifier
{
    public ReviewDecisionResult Classify(ReviewDecisionInput input)
    {
        string normalizedDecision = input.OperatorDecision.Trim().ToLowerInvariant();

        if (normalizedDecision == "accepted")
        {
            if (HashesEqual(input.WatchedHash, input.StagedHash))
            {
                return new ReviewDecisionResult("accepted", "Watched content matches staged candidate.");
            }

            if (input.NormalizedWatchedHash is not null
                && input.NormalizedStagedHash is not null
                && HashesEqual(input.NormalizedWatchedHash, input.NormalizedStagedHash))
            {
                return new ReviewDecisionResult("accepted-normalized", "Watched and staged content match after normalization.");
            }
        }

        if (normalizedDecision == "rejected"
            && input.IsNewFile
            && !input.WatchedFileExists)
        {
            return new ReviewDecisionResult("rejected", "New file was not created in watched source.");
        }

        if (normalizedDecision == "rejected" && HashesEqual(input.WatchedHash, input.OriginalHash))
        {
            return new ReviewDecisionResult("rejected", "Watched content remains at original baseline.");
        }

        return new ReviewDecisionResult("dirty-unexpected", "Operator decision and watched file hash do not agree.");
    }

    private static bool HashesEqual(string left, string right)
    {
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ReviewDecisionInput(
    string OperatorDecision,
    string OriginalHash,
    string StagedHash,
    string WatchedHash,
    string? NormalizedWatchedHash = null,
    string? NormalizedStagedHash = null,
    bool IsNewFile = false,
    bool WatchedFileExists = true);

public sealed record ReviewDecisionResult(
    string Classification,
    string Message);
