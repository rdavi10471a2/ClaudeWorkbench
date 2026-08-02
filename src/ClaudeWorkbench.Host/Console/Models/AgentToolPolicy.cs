namespace ClaudeWorkbench.Host.Console;

// Operator-controlled tool surface for the agent. Defaults are the governed,
// read-only-on-watched-workspace posture; the operator can widen it from the
// settings dialog. Persisted host-side and sent to the sidecar per turn.
public sealed class AgentToolPolicy
{
    // Native read tools (Read/Grep/Glob). Off => force all access through the MCP.
    public bool AllowNativeReads { get; set; } = true;

    // The semantic/structural Roslyn edit tools (add_method, submit_symbol, replace_span, …).
    // OFF by default: the runtime re-feeds the whole file every round-trip, so structure-keyed edits
    // buy no token saving over submit_file / replace_text_in_file and often add a discovery
    // round-trip. Kept as an operator toggle so the effect can be compared. See AgentToolSurface.
    public bool AllowSemanticEdits { get; set; }

    // Expose only the claude-workbench MCP server; ignore machine/account connectors.
    public bool StrictMcpConfig { get; set; } = true;

    // Extra native tools the operator has explicitly turned on (by tool name),
    // e.g. "Bash", "PowerShell", "Write", "WebFetch". The non-risky web tools are ON by default so
    // the agent can look things up; the risky writers (Bash/PowerShell/Write/Edit) stay off.
    public HashSet<string> EnabledOptionalTools { get; set; } =
        new(StringComparer.Ordinal) { "WebFetch", "WebSearch" };

    // Model id for the agent (empty => inherit the sidecar/subscription default).
    public string Model { get; set; } = string.Empty;

    // Reasoning effort: "", low, medium, high, xhigh, max (empty => default).
    public string Effort { get; set; } = string.Empty;

    // Which diff viewer Merge Review shows: "monaco" (default) is the rich Monaco diff editor
    // (F7/Shift+F7 change nav, overview ruler, word-level diff, syntax highlighting); "classic" is
    // the original DiffPlex side-by-side. The non-selected one stays present in the dialog but
    // disabled — kept, not removed — so flipping this switches which viewer is live.
    public string DiffViewer { get; set; } = DiffViewerOptions.Monaco;

    // Merge Review orientation: when true (default), the PROPOSED (new) file is on the LEFT and the
    // current (old) on the RIGHT — matching the classic reviewer, which is the order most people expect
    // when accepting a change. The Git page ignores this and always uses old-left/new-right (the
    // conventional git diff order). Applies to both the Monaco and classic viewers in Merge Review.
    public bool MergeReviewNewOnLeft { get; set; } = true;

    // Reverse the diff colors (insertions red, deletions green) in the Monaco viewer. Pairs with
    // proposed-on-left so additions still read green on the proposed side. Monaco themes are global, so
    // this applies to every Monaco diff (Merge Review and the Git page alike).
    public bool DiffSwapColors { get; set; }

    public AgentToolPolicy Clone()
    {
        return new AgentToolPolicy
        {
            AllowNativeReads = AllowNativeReads,
            AllowSemanticEdits = AllowSemanticEdits,
            StrictMcpConfig = StrictMcpConfig,
            EnabledOptionalTools = new HashSet<string>(EnabledOptionalTools, StringComparer.Ordinal),
            Model = Model,
            Effort = Effort,
            DiffViewer = DiffViewer,
            MergeReviewNewOnLeft = MergeReviewNewOnLeft,
            DiffSwapColors = DiffSwapColors,
        };
    }
}

// Diff-viewer choices offered in the settings dialog. Monaco is the default; the classic DiffPlex
// view is kept (not removed) so the operator can switch back.
public sealed record DiffViewerOption(string Label, string Value);

public static class DiffViewerOptions
{
    public const string Monaco = "monaco";
    public const string Classic = "classic";

    public static readonly IReadOnlyList<DiffViewerOption> All =
    [
        new("Monaco — rich diff (F7 nav · word-level)", Monaco),
        new("Classic — DiffPlex side-by-side", Classic),
    ];
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
