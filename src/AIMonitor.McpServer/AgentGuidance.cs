using System.Text;

namespace AIMonitor.McpServer;

// The single source of truth for the agent's staging procedure.
//
// This text has two consumers and used to be hand-maintained in both:
//   1. `get_staging_guide` over MCP (AIMonitorTools.ComposeStagingGuide), and
//   2. the sidecar's injected governance card (buildGovernanceCard in sidecar/src/index.ts),
//      which restated the same numbered steps as TypeScript string literals.
//
// They drifted, as duplicated prose does: edf83c8 reworded the guide for the in-app merge
// flow and left the sidecar's copy telling the agent about a review path that no longer
// existed, for weeks. The failure mode is nasty — the agent reads both, they disagree, and
// whichever it weights higher wins silently.
//
// So the procedure is authored here, once. The sidecar fetches it at startup over
// GET /guidance/staging (it is not an MCP client itself; it already polls /health the same
// way). Anything the sidecar keeps in its card must be a fact the HOST cannot know — which
// native tools the SDK exposes, gate behaviour, task-board policy.
public static class AgentGuidance
{
    public static string StagingGuide { get; } = ComposeStagingGuide();

    private static string ComposeStagingGuide()
    {
        StringBuilder builder = new();
        builder.AppendLine("# Staging Guide (ClaudeWorkbench)");
        builder.AppendLine();
        builder.AppendLine("Use this sequence for watched-project edits. You never write watched source directly; you build and stage a candidate, and the operator reviews and accepts it in the in-app Merge Review dialog.");
        builder.AppendLine();
        builder.AppendLine("1. Check `get_self_check`, `get_workflow_status`, and `get_monitor_status` when starting a session.");
        builder.AppendLine("2. There is exactly ONE edit session per run. Call `start_monitor_session(filesPlanned: [...])` before editing and declare EVERY watched file the change will touch up front — including any file you will move code OUT of — even for one-file edits, and include `owningProjectPath` when the index cannot prove a single owner. If you discover another file mid-change, add it to the SAME session — either `add_monitor_session_planned_file(file)` (one file at a time, preferred) or another `start_monitor_session` call (which merges, never forks). Staging or editing a file that is not in the plan is refused, so declare it first.");
        builder.AppendLine("3. Pass that same `sessionId` to `refresh_file`, `new_file`, every mutation tool, and `stage_candidate_for_review`.");
        builder.AppendLine("4. For existing files, call `refresh_file`. For future watched files, call `new_file`.");
        builder.AppendLine("5. Edit the monitor-owned Working candidate with the typed tools (`submit_symbol`, `add_method`, `add_property`, `replace_span_in_file`, `replace_text_in_file`, `submit_file`). For C# symbol edits, call `get_source_map` (selector mode) first.");
        builder.AppendLine("6. Stage every planned file with `stage_candidate_for_review(path, sessionId)`, then STOP.");
        builder.AppendLine("7. The operator reviews the staged diff in the ClaudeWorkbench Merge Review dialog and accepts or rejects each file. Do NOT call `record_diff_decision` — review, the accept-time write to watched source, and the decision record are host/operator actions in this environment.");
        builder.AppendLine("8. After the operator accepts, that file has been written to the watched solution. Call `refresh_file` before editing the same file again.");
        builder.AppendLine();
        builder.AppendLine("Notes:");
        builder.AppendLine();
        builder.AppendLine("- NEVER dismiss a compiler diagnostic as a tooling artifact from reasoning alone. If overlay validation reports a diagnostic, either prove the claim with a tool or report it to the operator as unexplained and let them decide. The authoritative build runs at the operator's accept, so a wrong dismissal is not discovered until after they have approved it.");
        builder.AppendLine("- A reject voids the WHOLE edit session, including files the operator already approved — nothing is written. Re-stage the entire set, not just the rejected file.");
        builder.AppendLine("- `blocked`, `dirty-unexpected`, `superseded`, missing Working files, and stale hashes require recovery before follow-up edits.");
        builder.AppendLine("- Pre-merge (GATE 1) validation and the full build/index (GATE 2) validation are run by the host around the operator's accept, not by you.");
        builder.AppendLine("- The operator's Accept in the Merge Review dialog is the only path candidates reach watched source. Never try to copy a candidate into watched source yourself.");
        return builder.ToString();
    }
}
