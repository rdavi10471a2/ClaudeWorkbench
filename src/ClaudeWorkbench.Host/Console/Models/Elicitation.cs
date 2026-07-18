namespace ClaudeWorkbench.Host.Console;

// The agent's AskUserQuestion: 1-4 clarifying questions, each with 2-4 options.
// The operator picks an option (or types their own via the always-present "Other"
// free-text). Shapes mirror the Agent SDK's AskUserQuestion input/answers contract.
public sealed record ElicitationOption(string Label, string? Description);

public sealed record ElicitationQuestion(
    string Question,
    string Header,
    IReadOnlyList<ElicitationOption> Options,
    bool MultiSelect);

public sealed record Elicitation(
    string Id,
    IReadOnlyList<ElicitationQuestion> Questions);
