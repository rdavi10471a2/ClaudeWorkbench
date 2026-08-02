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

    // Per-turn + thread-cumulative token anatomy folded from the sidecar's usage events (round-trips,
    // fresh input, cache-read, cache-creation, output). Synchronous — no SDK round-trip; updates live
    // as the turn streams. Distinct from GetUsageAsync (context-window/subscription %).
    TokenUsageBreakdown TokenUsage { get; }

    // Start a fresh conversation thread (drops resumed context, clears the transcript).
    Task NewThreadAsync();

    // Repaint the chat pane on resume: prepend a restored history window (the last N interactions read
    // from the runtime mirror) ahead of this run's live events. Replaces any previously restored
    // history. Live turns after the resume append below it. Cleared by NewThreadAsync.
    void RestoreHistory(IReadOnlyList<TranscriptEntry> entries);
}
