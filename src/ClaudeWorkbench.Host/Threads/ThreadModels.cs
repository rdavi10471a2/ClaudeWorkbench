namespace ClaudeWorkbench.Host.Threads;

// A conversation THREAD is the atom that replaced the Tasks board. It is a named, resumable
// pointer to a Claude Agent SDK session: the transcript itself stays in ~/.claude; this record
// carries the human-facing metadata (name/description/note), the resume coordinates
// (session_id + cwd), and lifecycle status. Stored in a dedicated per-workspace threads.sqlite —
// NOT the solution index DB.
//
// STATUS is deliberately small. "Active" is NOT a stored value: it is computed as "the thread
// whose session is the current live one" (single-operator => at most one Active). Only the
// non-live states are persisted.
public static class ThreadStatus
{
    // A named stub with no session_id yet — pre-conversation intent.
    public const string Planned = "planned";

    // Has a session_id, resumable, not the live one.
    public const string Archived = "archived";

    // Discarded; kept for the record until hard-deleted.
    public const string Abandoned = "abandoned";

    public static bool IsValid(string status) =>
        status is Planned or Archived or Abandoned;
}

// KIND is a promotion flag, not a mode chosen up front (the old "discussion vs work" split is
// gone). Everything starts as a discussion; the human may promote it to a task.
public static class ThreadKind
{
    public const string Discussion = "discussion";

    public const string Task = "task";

    public static bool IsValid(string kind) =>
        kind is Discussion or Task;
}

public sealed record ThreadRecord(
    string ThreadId,
    string Name,
    string? Description,
    string? UserNote,
    string? SessionId,
    string Cwd,
    string Status,
    string Kind,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<string> AcceptedEditRefs)
{
    // A stub is Planned with no session yet; it cannot be resumed until a conversation gives it one.
    public bool IsStub => string.IsNullOrWhiteSpace(SessionId);
}
