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
  "launch_staged_diff",
  "record_diff_decision",
  // git mutations — pause at the operator gate. Push is outward-facing; commit and
  // branch changes alter repo/working-tree state. Reads (git_status/git_diff/
  // git_log/git_list_branches) are not listed, so they auto-allow.
  "git_commit",
  "git_push",
  "git_create_branch",
  "git_switch_branch",
]);

export function baseName(toolName: string): string {
  const idx = toolName.lastIndexOf("__");
  return idx >= 0 ? toolName.slice(idx + 2) : toolName;
}

export function isGatedTool(toolName: string): boolean {
  return GATED_TOOLS.has(baseName(toolName));
}

export interface GateResolution {
  decision: GateDecision;
  reason?: string;
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

  resolve(gateId: string, decision: GateDecision, reason?: string): boolean {
    const gate = this.pending.get(gateId);
    if (!gate) {
      return false;
    }
    this.pending.delete(gateId);
    gate.resolve({ decision, reason });
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
