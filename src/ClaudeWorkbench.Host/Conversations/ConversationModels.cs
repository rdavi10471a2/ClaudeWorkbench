namespace ClaudeWorkbench.Host.Conversations;

// A conversation THREAD is the atom that replaced the Tasks board. It is a named, resumable
// pointer to a Claude Agent SDK session: the transcript itself stays in ~/.claude; this record
// carries the human-facing metadata (name/description/note), the resume coordinates
// (session_id + cwd), and lifecycle status. Stored in a dedicated per-workspace threads.sqlite —
// NOT the solution index DB.
//
// STATUS is deliberately tiny — there are only two buckets, and one of them isn't stored:
//   * "Current" is COMPUTED (the conversation whose session is the live one; single-operator => one).
//   * "Archived" is the ONLY stored status: every saved conversation that isn't the live one.
// Disposal is a hard Delete, so there's no "abandoned"/soft-deleted middle state.
public static class ConversationStatus
{
    public const string Archived = "archived";

    public static bool IsValid(string status) => status is Archived;
}

public sealed record ConversationRecord(
    string ConversationId,
    string Name,
    string? Description,
    string? UserNote,
    string? SessionId,
    string Cwd,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<string> AcceptedEditRefs,
    // The readable filename of this thread's app-owned transcript mirror (runtime\...\sessions\<file>),
    // named after the conversation — the DB, not the filename, maps it back to the thread. Null until
    // the first turn mirrors it.
    string? TranscriptFile = null)
{
    // A stub has no session yet (created by New Thread before its first turn); it can't be resumed
    // until a turn gives it one.
    public bool IsStub => string.IsNullOrWhiteSpace(SessionId);
}
