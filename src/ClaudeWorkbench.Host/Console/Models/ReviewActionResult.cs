namespace ClaudeWorkbench.Host.Console.Models;

public sealed class ReviewActionResult
{
    public ReviewActionResult(string message, string? agentSummary = null, bool overrideAvailable = false)
    {
        Message = message;
        AgentSummary = agentSummary;
        OverrideAvailable = overrideAvailable;
    }

    public string Message { get; }

    // The accept was refused by a validation gate that the operator IS allowed to override.
    // Set by the workflow rather than inferred by the UI: the GATE-2 build failure is only
    // discoverable at accept time, and the record's own GATE-1 status stays clean through it,
    // so a UI keying off that status alone would leave the override unreachable.
    public bool OverrideAvailable { get; }

    // Compilation + index outcome to echo back to the agent (set only on the
    // terminal accept, when the build and index rebuild actually ran). Null when
    // there is nothing to report.
    public string? AgentSummary { get; }
}
