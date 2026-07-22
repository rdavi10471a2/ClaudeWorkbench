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

    // Model id for the agent (empty => inherit the sidecar/subscription default).
    public string Model { get; set; } = string.Empty;

    // Reasoning effort: "", low, medium, high, xhigh, max (empty => default).
    public string Effort { get; set; } = string.Empty;

    public AgentToolPolicy Clone()
    {
        return new AgentToolPolicy
        {
            AllowNativeReads = AllowNativeReads,
            StrictMcpConfig = StrictMcpConfig,
            EnabledOptionalTools = new HashSet<string>(EnabledOptionalTools, StringComparer.Ordinal),
            Model = Model,
            Effort = Effort,
        };
    }
}

// Model choices offered in the settings dialog. Empty value = inherit the default.
public sealed record AgentModelOption(string Label, string Value);

public static class AgentModelOptions
{
    public static readonly IReadOnlyList<AgentModelOption> All =
    [
        new("Default (inherit)", ""),
        new("Opus 4.8", "claude-opus-4-8"),
        new("Sonnet 5", "claude-sonnet-5"),
        new("Haiku 4.5", "claude-haiku-4-5-20251001"),
        new("Fable 5", "claude-fable-5"),
    ];
}

// Reasoning-effort choices (empty value = default). Maps to the SDK `effort` option.
public static class ReasoningLevels
{
    public static readonly IReadOnlyList<string> All = ["", "low", "medium", "high", "xhigh", "max"];
}

// Catalog of tools the operator may opt into from the settings dialog. Kept off
// by default because each widens what the agent can do outside the governed gate.
public sealed record OptionalAgentTool(string Name, string Description, bool Risky);

public static class OptionalAgentTools
{
    // MUST stay in sync with the sidecar's ENABLEABLE_NATIVE set (sidecar/src/index.ts):
    // every tool offered here has to be one the sidecar will actually honor, or the toggle
    // silently no-ops. Agent/Workflow are deliberately NOT offered — multi-agent orchestration
    // is out of scope for the governed workbench.
    public static readonly IReadOnlyList<OptionalAgentTool> All =
    [
        new("Bash", "Run shell commands (can read and WRITE files, run builds/tests).", true),
        new("PowerShell", "Run PowerShell commands (can read and WRITE files).", true),
        new("Write", "Create/overwrite files directly (bypasses the staged-review gate).", true),
        new("Edit", "Edit files directly (bypasses the staged-review gate).", true),
        new("WebFetch", "Fetch content from a URL (reaches outside the workspace).", false),
        new("WebSearch", "Search the web (reaches outside the workspace).", false),
    ];
}
