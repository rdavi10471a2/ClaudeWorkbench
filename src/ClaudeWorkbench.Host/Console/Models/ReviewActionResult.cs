namespace ClaudeWorkbench.Host.Console.Models;

public sealed class ReviewActionResult
{
    public ReviewActionResult(string message, string? agentSummary = null)
    {
        Message = message;
        AgentSummary = agentSummary;
    }

    public string Message { get; }

    // Compilation + index outcome to echo back to the agent (set only on the
    // terminal accept, when the build and index rebuild actually ran). Null when
    // there is nothing to report.
    public string? AgentSummary { get; }
}
