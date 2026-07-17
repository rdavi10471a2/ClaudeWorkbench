using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

public sealed class ReviewDecisionClassifierTests
{
    [Fact]
    public void Accepted_requires_watched_hash_to_match_staged_hash()
    {
        ReviewDecisionResult result = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput("accepted", "original", "staged", "staged"));

        Assert.Equal("accepted", result.Classification);
    }

    [Fact]
    public void Rejected_requires_watched_hash_to_match_original_hash()
    {
        ReviewDecisionResult result = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput("rejected", "original", "staged", "original"));

        Assert.Equal("rejected", result.Classification);
    }

    [Fact]
    public void Mismatched_vote_and_hash_becomes_dirty_unexpected()
    {
        ReviewDecisionResult result = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput("accepted", "original", "staged", "other"));

        Assert.Equal("dirty-unexpected", result.Classification);
    }
}
