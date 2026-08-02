using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Tests;

// The governance card and staging guide must name ONLY the tools that are actually on the agent's
// surface. The semantic Roslyn edit family is off-surface entirely (its [McpServerTool] attributes
// are commented out), so the card must not mention it — naming a tool the agent can't retrieve sends
// it hunting (the card/gate contradiction that cost ~7 round-trips/edit). The agent edits via text.
public sealed class AgentGuidanceCardTests
{
    private static readonly string[] OffSurfaceSemanticTools =
        ["add_method", "submit_symbol", "replace_span_in_file", "add_property", "add_symbol"];

    [Fact]
    public void Card_does_not_name_the_off_surface_semantic_tools()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx");

        foreach (string tool in OffSurfaceSemanticTools)
        {
            Assert.DoesNotContain(tool, card);
        }
    }

    [Fact]
    public void Card_names_the_live_edit_tools_the_agent_actually_has()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx");

        Assert.Contains("replace_text_in_file", card);
        Assert.Contains("submit_file", card);
    }

    [Fact]
    public void Card_embraces_native_reads_for_discovery()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx");

        // Native reads own exploration; the index tools cover what native can't (outline + blast radius).
        Assert.Contains("Read/Grep/Glob", card);
        Assert.Contains("get_source_map", card);
        Assert.Contains("find_indexed_references", card);
    }

    [Fact]
    public void Staging_guide_names_only_the_text_edit_tools()
    {
        string guide = AgentGuidance.ComposeStagingGuide();

        Assert.DoesNotContain("add_method", guide);
        Assert.DoesNotContain("submit_symbol", guide);
        Assert.Contains("replace_text_in_file", guide);
        Assert.Contains("submit_file", guide);
    }
}
