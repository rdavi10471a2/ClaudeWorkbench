namespace ClaudeWorkbench.Host.Console;

// A structured question the agent needs answered before it can continue. Kept
// generic (agent-agnostic); the adapter decides how a given backend raises it.
public enum ElicitationFieldKind
{
    Text,
    Boolean,
    Number,
    Enum,
}

public sealed record ElicitationFieldOption(string Value, string Label);

public sealed record ElicitationField(
    string Name,
    string Label,
    string? Description,
    ElicitationFieldKind Kind,
    bool Required,
    IReadOnlyList<ElicitationFieldOption> Options);

public sealed record Elicitation(
    string Id,
    string Question,
    IReadOnlyList<ElicitationField> Fields);
