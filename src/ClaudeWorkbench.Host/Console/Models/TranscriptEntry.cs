namespace ClaudeWorkbench.Host.Console;

public enum TranscriptKind
{
    Assistant,
    ToolCall,
}

public sealed record TranscriptEntry(TranscriptKind Kind, string Text);
