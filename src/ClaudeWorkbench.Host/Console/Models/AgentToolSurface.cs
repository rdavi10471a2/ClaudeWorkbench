using System.Linq;

namespace ClaudeWorkbench.Host.Console;

// The complete governed tool surface for the agent, authored HERE (single source) and served to the
// sidecar at GET /guidance/tool-policy — the same C#-authoritative pattern as the role card
// (AgentGuidance.ComposeGovernanceCard). The sidecar FETCHES this and derives its disallowedTools
// from it, keeping only a minimal built-in fallback for when the host is unreachable. Keeping the
// whole surface in one C# place means the governance boundary can be read and reviewed at a glance,
// and the sidecar's old hand-copied TypeScript lists can no longer drift from it.
public sealed record AgentToolSurfaceSpec(
    IReadOnlyList<string> AlwaysAllowedNative,
    IReadOnlyList<string> ReadTools,
    IReadOnlyList<string> BlockableNative,
    IReadOnlyList<string> EnableableNative,
    IReadOnlyList<string> SemanticEditMcpTools);

public static class AgentToolSurface
{
    // Native tools the agent always needs (ToolSearch loads the MCP tool schemas on demand).
    private static readonly string[] AlwaysAllowedNative = ["ToolSearch", "TodoWrite"];

    // Native read tools, gated by AgentToolPolicy.AllowNativeReads.
    private static readonly string[] ReadTools = ["Read", "Grep", "Glob"];

    // Native writers/shells hard-removed unless the operator opts them in. NOTE this is a SUPERSET of
    // what OptionalAgentTools offers: MultiEdit/NotebookEdit are blocked but never offered for
    // re-enable (nothing to opt them back in), so they stay permanently denied. Keep this list and
    // OptionalAgentTools.All (the risky entries) consistent.
    private static readonly string[] BlockableNative =
        ["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash", "PowerShell"];

    // The semantic/structural Roslyn edit MCP tools. Withheld from the agent unless the operator turns
    // AllowSemanticEdits on (default off). The runtime re-feeds the whole file as cache-read every
    // round-trip, so structure-keyed edits buy no token saving over submit_file / replace_text_in_file
    // and often add a get_source_map discovery round-trip (round-trips are the real cost driver). They
    // remain registered in the MCP server (callable by tests / by design); withholding only removes
    // them from the AGENT's advertised surface. The agent edits via:
    //   refresh_file (read/prep) -> submit_file (whole-file) | replace_text_in_file (coarse delta).
    private static readonly string[] SemanticEditMcpTools =
    [
        "submit_symbol",
        "add_symbol",
        "add_field",
        "add_property",
        "add_method",
        "add_constructor",
        "add_nested_type",
        "remove_symbol",
        "replace_span_in_file",
        "set_type_partial",
        "add_using",
        "remove_using",
    ];

    public static AgentToolSurfaceSpec Compose()
    {
        // The enableable set (what an operator may re-enable) IS the settings catalog — the offered
        // writers (Bash/PowerShell/Write/Edit) plus the web readers. It is deliberately NARROWER than
        // BlockableNative: MultiEdit/NotebookEdit are blocked but never offered, so they can never be
        // opted back in. Sourcing it from OptionalAgentTools means it never drifts from the dialog.
        string[] enableable = OptionalAgentTools.All.Select(tool => tool.Name).ToArray();

        return new AgentToolSurfaceSpec(
            AlwaysAllowedNative,
            ReadTools,
            BlockableNative,
            enableable,
            SemanticEditMcpTools);
    }
}
