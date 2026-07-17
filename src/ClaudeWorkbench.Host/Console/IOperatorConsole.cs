namespace ClaudeWorkbench.Host.Console;

// The seam between the Blazor UI and whatever drives the agent. The UI depends
// only on this; the concrete adapter (sidecar today) lives in the Services layer
// and is the sole place aware of the transport and the agent's wire shapes.
public interface IOperatorConsole
{
    event Action? Changed;

    string WorkspacePath { get; }

    ConsoleStatus Status { get; }

    IReadOnlyList<TranscriptEntry> Transcript { get; }

    IReadOnlyList<ApprovalRequest> PendingApprovals { get; }

    IReadOnlyList<ActivityEntry> Activity { get; }

    Task SendAsync(string prompt);

    Task ResolveAsync(string approvalId, bool approve, string? reason = null);
}
