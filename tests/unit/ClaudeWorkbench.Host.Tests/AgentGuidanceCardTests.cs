using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Tests;

// Regression guard for the card/gate contradiction that cost ~7 round-trips per edit: the governance
// card USED to recommend the Roslyn semantic tools (add_method/submit_symbol/…) as the primary edit
// path, then the surface gate withheld them — so the agent kept ToolSearching for tools it could never
// retrieve. The card must now match the actual surface: name the withheld family ONLY when semantic
// edits are enabled.
public sealed class AgentGuidanceCardTests
{
    private static readonly string[] SemanticToolNames =
        ["add_method", "submit_symbol", "replace_span_in_file", "add_property", "add_symbol"];

    [Fact]
    public void Default_card_does_not_name_the_withheld_semantic_tools()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx", allowSemanticEdits: false);

        foreach (string tool in SemanticToolNames)
        {
            Assert.DoesNotContain(tool, card);
        }
    }

    [Fact]
    public void Default_card_names_the_live_edit_tools_the_agent_actually_has()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx", allowSemanticEdits: false);

        Assert.Contains("replace_text_in_file", card);
        Assert.Contains("submit_file", card);
    }

    [Fact]
    public void Semantic_on_card_restores_the_semantic_edit_guidance()
    {
        string card = AgentGuidance.ComposeGovernanceCard("Sample.slnx", allowSemanticEdits: true);

        Assert.Contains("add_method", card);
        Assert.Contains("submit_symbol", card);
    }

    [Fact]
    public void Staging_guide_default_is_lean_and_toggled_variant_is_not()
    {
        Assert.DoesNotContain("add_method", AgentGuidance.ComposeStagingGuide(allowSemanticEdits: false));
        Assert.Contains("add_method", AgentGuidance.ComposeStagingGuide(allowSemanticEdits: true));
    }
}
