namespace ClaudeWorkbench.Host.Console;

public enum TranscriptKind
{
    User,
    Assistant,
    ToolCall,
    Image,
}

// For Image entries, Text is the local file path (served via /local-file).
public sealed record TranscriptEntry(TranscriptKind Kind, string Text, string Time);
