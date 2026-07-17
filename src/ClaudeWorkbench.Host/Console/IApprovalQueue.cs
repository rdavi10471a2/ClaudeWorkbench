namespace ClaudeWorkbench.Host.Console;

// Everything the agent is blocked on: tool-use approvals and structured
// elicitations. The UI renders and resolves these; the adapter maps them to
// whatever the backend's pause/resume mechanism is.
public interface IApprovalQueue
{
    event Action? Changed;

    IReadOnlyList<ApprovalRequest> PendingApprovals { get; }

    IReadOnlyList<Elicitation> PendingElicitations { get; }

    Task ResolveApprovalAsync(string approvalId, bool approve, string? reason = null);

    Task AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> values);
}
