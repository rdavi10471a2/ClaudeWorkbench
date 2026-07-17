namespace ClaudeWorkbench.Host.Console;

// A tool-use permission awaiting an operator decision (Claude's canUseTool). No
// session/persistent/MCP-variant flags — those were Codex/MCP concerns that do
// not belong in the UI model.
public sealed record ApprovalRequest(
    string Id,
    string Tool,
    string? Target,
    string? InputJson,
    string? Summary);
