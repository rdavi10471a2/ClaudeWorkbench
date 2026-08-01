namespace ClaudeWorkbench.Host.Console;

public enum TranscriptKind
{
    User,
    Assistant,
    ToolCall,
    Image,
    Error,
    // A non-conversational marker the app injects (e.g. the "history restored" divider shown at the
    // bottom of a rehydrated-on-resume block). Text is the message; rendered as a muted separator.
    Notice,
}

// For Image entries, Text is the local file path (served via /local-file).
public sealed record TranscriptEntry(TranscriptKind Kind, string Text, string Time);
