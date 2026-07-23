using ClaudeWorkbench.Host.Console.Models;

namespace ClaudeWorkbench.Host.Console;

// Operator-facing review of staged edits. The agent stages candidates through the
// governed MCP; this surface lets the operator review the diff and accept/reject.
// Accept is the only place watched source is written, and it is an operator action
// (the Blazor accept handler), never the agent.
public interface IReviewWorkflow
{
    IReadOnlyList<ReviewQueueItem> ListPending();

    ReviewRecordModel Load(string stagedRecordId);

    ReviewRecordModel LoadNextForSession(string sessionId);

    // rebuildIndex is honored ONLY on the terminal accept (the one that writes the session).
    // Default true = current behavior. False defers the (expensive) post-accept index rebuild:
    // the files still write, but the index goes stale until the next reindex — for tight
    // single-file/markup loops where the cross-file graph isn't needed yet.
    ReviewActionResult Accept(string stagedRecordId, bool forceApproveValidation, bool rebuildIndex = true);

    ReviewActionResult Reject(string stagedRecordId);
}
