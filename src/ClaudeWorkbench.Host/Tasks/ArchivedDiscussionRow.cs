namespace ClaudeWorkbench.Host.Tasks;

public sealed record ArchivedDiscussionRow(
    string Id,
    string Name,
    string MarkdownPath,
    string? ThreadId,
    string TurnMode,
    string Trigger,
    DateTime CreatedAt);
