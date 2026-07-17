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

    ReviewActionResult Accept(string stagedRecordId, bool forceApproveValidation);

    ReviewActionResult Reject(string stagedRecordId);
}
