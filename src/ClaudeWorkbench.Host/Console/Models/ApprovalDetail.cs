namespace ClaudeWorkbench.Host.Console;

// One readable row in an approval dialog: a friendly label and its value.
// IsCode renders the value as a multi-line code block instead of inline text.
public sealed record ApprovalDetail(string Label, string Value, bool IsCode);
