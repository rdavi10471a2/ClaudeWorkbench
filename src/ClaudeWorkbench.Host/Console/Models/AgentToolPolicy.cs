namespace ClaudeWorkbench.Host.Console;

// Operator-controlled tool surface for the agent. Defaults are the governed,
// read-only-on-watched-workspace posture; the operator can widen it from the
// settings dialog. Persisted host-side and sent to the sidecar per turn.
public sealed class AgentToolPolicy
{
    // Native read tools (Read/Grep/Glob). Off => force all access through the MCP.
    public bool AllowNativeReads { get; set; } = true;

    // Expose only the claude-workbench MCP server; ignore machine/account connectors.
    public bool StrictMcpConfig { get; set; } = true;

    // Extra native tools the operator has explicitly turned on (by tool name),
    // e.g. "Bash", "PowerShell", "Write", "WebFetch". Empty by default.
    public HashSet<string> EnabledOptionalTools { get; set; } = new(StringComparer.Ordinal);

    public AgentToolPolicy Clone()
    {
        return new AgentToolPolicy
        {
            AllowNativeReads = AllowNativeReads,
            StrictMcpConfig = StrictMcpConfig,
            EnabledOptionalTools = new HashSet<string>(EnabledOptionalTools, StringComparer.Ordinal),
        };
    }
}

// Catalog of tools the operator may opt into from the settings dialog. Kept off
// by default because each widens what the agent can do outside the governed gate.
public sealed record OptionalAgentTool(string Name, string Description, bool Risky);

public static class OptionalAgentTools
{
    public static readonly IReadOnlyList<OptionalAgentTool> All =
    [
        new("Bash", "Run shell commands (can read and WRITE files, run builds/tests).", true),
        new("PowerShell", "Run PowerShell commands (can read and WRITE files).", true),
        new("Write", "Create/overwrite files directly (bypasses the staged-review gate).", true),
        new("Edit", "Edit files directly (bypasses the staged-review gate).", true),
        new("WebFetch", "Fetch content from a URL.", false),
        new("WebSearch", "Search the web.", false),
        new("Agent", "Spawn sub-agents.", false),
        new("Workflow", "Run multi-agent orchestration workflows.", false),
    ];
}
