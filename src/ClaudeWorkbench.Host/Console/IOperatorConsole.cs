namespace ClaudeWorkbench.Host.Console;

// The turn/session seam: what the operator is looking at and how they start work.
// Approvals live in IApprovalQueue; review in IReviewWorkflow; tasks in ITaskBoard.
public interface IOperatorConsole
{
    event Action? Changed;

    string WorkspacePath { get; }

    ConsoleStatus Status { get; }

    // Login state of the Claude and GitHub CLIs, for the command-bar dots. Orthogonal
    // to Status (turn/session), and probed out-of-band, so it lives on its own seam.
    AuthStatus Auth { get; }

    IReadOnlyList<TranscriptEntry> Transcript { get; }

    IReadOnlyList<ActivityEntry> Activity { get; }

    // autoApprove: for this turn, claude-workbench mutations skip the per-call operator
    // gate (the merge-review Accept still gates the write to watched source).
    Task SendAsync(string prompt, bool autoApprove);

    // Interrupt the in-flight turn.
    Task StopAsync();

    // Live token/context + subscription usage off the agent's Query handle.
    Task<UsageSnapshot> GetUsageAsync();

    // Start a fresh conversation thread (drops resumed context, clears the transcript).
    Task NewThreadAsync();
}
