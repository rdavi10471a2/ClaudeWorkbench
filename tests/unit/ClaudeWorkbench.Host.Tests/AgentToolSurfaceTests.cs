using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Tests;

// AgentToolSurface.Compose() is the single C# source of truth for the governed tool surface, fetched
// by the sidecar at GET /guidance/tool-policy. These tests pin the governance invariants on the C#
// side (the sidecar's toolGating.test.ts pins the derivation over the mirrored fallback). This is the
// coverage that had fallen out: nothing asserted which tools the agent may/may not reach.
public sealed class AgentToolSurfaceTests
{
    private static readonly string[] KeepSet = ["submit_file", "refresh_file", "replace_text_in_file"];

    [Fact]
    public void Enableable_is_the_offered_catalog_not_every_blockable_tool()
    {
        AgentToolSurfaceSpec spec = AgentToolSurface.Compose();

        // The enableable set must equal exactly the OptionalAgentTools catalog (what the dialog offers).
        string[] catalog = OptionalAgentTools.All.Select(t => t.Name).ToArray();
        Assert.Equal(catalog.OrderBy(x => x), spec.EnableableNative.OrderBy(x => x));
    }

    [Fact]
    public void MultiEdit_and_NotebookEdit_are_blocked_but_never_enableable()
    {
        AgentToolSurfaceSpec spec = AgentToolSurface.Compose();

        foreach (string tool in new[] { "MultiEdit", "NotebookEdit" })
        {
            Assert.Contains(tool, spec.BlockableNative);
            Assert.DoesNotContain(tool, spec.EnableableNative);
        }
    }

    [Fact]
    public void Every_risky_optional_tool_maps_to_a_real_block_it_can_lift()
    {
        AgentToolSurfaceSpec spec = AgentToolSurface.Compose();

        // Any offered WRITER (risky) must actually be in the deny list — otherwise enabling it is a
        // no-op toggle. (Web readers are not blockable; they gate via canUseTool, so exclude them.)
        foreach (OptionalAgentTool tool in OptionalAgentTools.All.Where(t => t.Risky))
        {
            Assert.Contains(tool.Name, spec.BlockableNative);
        }
    }

    [Fact]
    public void Governed_keep_set_is_never_blocked()
    {
        AgentToolSurfaceSpec spec = AgentToolSurface.Compose();

        // The keep-set (how the agent actually edits) must never appear in the native deny list.
        foreach (string tool in KeepSet)
        {
            Assert.DoesNotContain(tool, spec.BlockableNative);
        }
    }

    [Fact]
    public void Reads_and_always_allowed_are_distinct_from_the_deny_surface()
    {
        AgentToolSurfaceSpec spec = AgentToolSurface.Compose();

        // ToolSearch/TodoWrite and the read tools must not also be blocked (that would deadlock the agent).
        foreach (string tool in spec.AlwaysAllowedNative.Concat(spec.ReadTools))
        {
            Assert.DoesNotContain(tool, spec.BlockableNative);
        }
    }
}
