namespace ClaudeWorkbench.Host.Console;

// Agent-agnostic view models the Blazor UI binds to. Deliberately free of any
// Claude / sidecar / MCP vocabulary (no "gate", "SidecarEvent", "turn"): if the
// backend changes, only the adapter that produces these changes — not the UI.

public enum TranscriptKind
{
    Assistant,
    ToolCall,
}

public sealed record TranscriptEntry(TranscriptKind Kind, string Text);

public sealed record ApprovalRequest(string Id, string Action, string? Target);

public sealed record ActivityEntry(string Category, string Detail);

public sealed record ConsoleStatus(bool Connected, bool Working);
