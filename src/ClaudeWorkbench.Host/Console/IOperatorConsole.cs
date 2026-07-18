namespace ClaudeWorkbench.Host.Console;

// The turn/session seam: what the operator is looking at and how they start work.
// Approvals live in IApprovalQueue; review in IReviewWorkflow; tasks in ITaskBoard.
public interface IOperatorConsole
{
    event Action? Changed;

    string WorkspacePath { get; }

    ConsoleStatus Status { get; }

    IReadOnlyList<TranscriptEntry> Transcript { get; }

    IReadOnlyList<ActivityEntry> Activity { get; }

    // autoApprove: for this turn, claude-workbench mutations skip the per-call operator
    // gate (the merge-review Accept still gates the write to watched source).
    Task SendAsync(string prompt, bool autoApprove);

    // Interrupt the in-flight turn.
    Task StopAsync();

    // Start a fresh conversation thread (drops resumed context, clears the transcript).
    Task NewThreadAsync();
}
