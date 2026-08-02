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
    IReadOnlyList<string> EnableableNative);

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

    // NOTE: the semantic/structural Roslyn edit MCP tools are no longer part of the surface at all —
    // their [McpServerTool] attributes are commented out in AIMonitorTools.RoslynEdits.cs, so the server
    // never advertises them. There is nothing for the sidecar to gate; the server owns the MCP surface.

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
            enableable);
    }
}
