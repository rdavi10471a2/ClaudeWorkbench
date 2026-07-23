import { randomUUID } from "node:crypto";
import type { GateDecision } from "./events.js";

// Governed mutations. Read-only discovery tools (get_*, find_*, list_*, query_*,
// compare_*, check_*) auto-allow so the operator is only interrupted for changes
// that can reach watched source or the review queue. Names are the tool basename
// after the mcp__claude-workbench__ prefix is stripped.
const GATED_TOOLS = new Set<string>([
  "new_file",
  "submit_file",
  "replace_text_in_file",
  "replace_span_in_file",
  "submit_symbol",
  "add_using",
  "remove_using",
  "set_type_partial",
  "add_symbol",
  "add_field",
  "add_property",
  "add_method",
  "add_constructor",
  "add_nested_type",
  "remove_symbol",
  "stage_candidate_for_review",
  // launch_staged_diff is gone — the external diff-tool path was retired; review is in-app.
  "record_diff_decision",
  // download_url fetches a URL to the workspace uploads folder (network + a file write), so the
  // operator approves each download at the gate.
  "download_url",
  // Git writes are intentionally NOT MCP tools: the agent has read-only git access
  // (git_status/git_diff/git_log/git_list_branches, all auto-allowed because they are
  // not listed here), and every git write — commit, push, branch, merge — is done by
  // the operator in the Git page instead. With no git-write tool to call, there is
  // nothing here to gate and nothing auto-approve could bypass.
]);

export function baseName(toolName: string): string {
  const idx = toolName.lastIndexOf("__");
  return idx >= 0 ? toolName.slice(idx + 2) : toolName;
}

export function isGatedTool(toolName: string): boolean {
  return GATED_TOOLS.has(baseName(toolName));
}

// Tools auto-approve may NEVER skip (ADR-0006). Auto-approve is defensible today because every
// gated tool mutates the monitor-owned Working candidate and the irreversible step — bytes
// reaching watched source — is gated separately at the operator's Accept. File-level delete and
// rename break that: no candidate, nothing to diff, nothing for merge review to hold, and no
// retrieval backup unless the file happened to go through refresh_file.
//
// Populated before the tools exist on purpose. A destructive tool added later cannot silently
// inherit auto-approve; the worst case is that it is gated more than someone intended.
const NEVER_AUTO_APPROVED_TOOLS = new Set<string>([
  "delete_file",
  "remove_file",
  "rename_file",
  "move_file",
]);

export function isNeverAutoApproved(toolName: string): boolean {
  return NEVER_AUTO_APPROVED_TOOLS.has(baseName(toolName));
}

export interface GateResolution {
  decision: GateDecision;
  reason?: string;
  // On allow: also stop gating this tool for the rest of the thread.
  remember?: boolean;
}

interface PendingGate extends GateResolution {
  gateId: string;
  tool: string;
  input: unknown;
  filePath?: string;
  resolve: (resolution: GateResolution) => void;
}

// Holds tool calls paused at the human accept/reject gate. request() returns a
// promise the PreToolUse hook awaits; resolve() is driven by the host UI.
export class OperatorGate {
  private readonly pending = new Map<string, Omit<PendingGate, keyof GateResolution>>();

  request(
    tool: string,
    input: unknown,
    filePath?: string,
  ): { gateId: string; decided: Promise<GateResolution> } {
    const gateId = randomUUID();
    let resolve!: (resolution: GateResolution) => void;
    const decided = new Promise<GateResolution>((r) => {
      resolve = r;
    });
    this.pending.set(gateId, { gateId, tool, input, filePath, resolve });
    return { gateId, decided };
  }

  resolve(gateId: string, decision: GateDecision, reason?: string, remember?: boolean): boolean {
    const gate = this.pending.get(gateId);
    if (!gate) {
      return false;
    }
    this.pending.delete(gateId);
    gate.resolve({ decision, reason, remember });
    return true;
  }

  list(): { gateId: string; tool: string; input: unknown; filePath?: string }[] {
    return [...this.pending.values()].map(({ gateId, tool, input, filePath }) => ({
      gateId,
      tool,
      input,
      filePath,
    }));
  }
}
