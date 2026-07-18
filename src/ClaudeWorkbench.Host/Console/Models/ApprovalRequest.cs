namespace ClaudeWorkbench.Host.Console;

// A tool-use permission awaiting an operator decision (Claude's canUseTool).
// Title + Details are the human-readable presentation of the request; InputJson
// is the pretty-printed raw payload kept for a collapsible "raw request" view.
public sealed record ApprovalRequest(
    string Id,
    string Tool,
    string? Target,
    string Title,
    IReadOnlyList<ApprovalDetail> Details,
    string? InputJson);
