namespace ClaudeWorkbench.Host.Console;

// Everything the agent is blocked on: tool-use approvals and structured
// elicitations. The UI renders and resolves these; the adapter maps them to
// whatever the backend's pause/resume mechanism is.
public interface IApprovalQueue
{
    event Action? Changed;

    IReadOnlyList<ApprovalRequest> PendingApprovals { get; }

    IReadOnlyList<Elicitation> PendingElicitations { get; }

    // remember: when approving, don't ask again for this tool for the rest of the
    // thread (cleared on New Thread). Ignored on deny.
    Task ResolveApprovalAsync(string approvalId, bool approve, string? reason = null, bool remember = false);

    Task AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> values);
}
