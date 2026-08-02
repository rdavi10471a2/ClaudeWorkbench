// The governed tool surface: the deny-by-default derivation that turns an operator's per-turn
// tool policy into the SDK's `disallowedTools` list plus the canUseTool allow-set. This is the
// governance boundary on the agent's tool surface, so:
//   1. the AUTHORITATIVE surface (which native tools exist, which MCP tools are withheld) is
//      authored in C# (AgentToolSurface.Compose) and fetched from GET /guidance/tool-policy —
//      the same single-source-in-C# pattern as the role card. FALLBACK_TOOL_SURFACE below is only
//      used when the host is unreachable (mirrors the card's minimal fallback).
//   2. the DERIVATION is a pure function (deriveToolGating) with unit tests (toolGating.test.ts) —
//      no live stack needed to pin the governance matrix.

export interface ToolPolicy {
  allowNativeReads: boolean;
  // When OFF (the default), the semantic/structural Roslyn edit family is withheld from the agent
  // (it edits via submit_file / replace_text_in_file). An operator can flip it on from Settings to
  // compare — the tools cost extra round-trips without token savings, so off is the governed default.
  allowSemanticEdits: boolean;
  strictMcpConfig: boolean;
  enabledTools: string[];
  autoApprove: boolean;
  model: string;
  effort: string;
}

export const DEFAULT_TOOL_POLICY: ToolPolicy = {
  allowNativeReads: true,
  allowSemanticEdits: false,
  strictMcpConfig: true,
  enabledTools: [],
  autoApprove: false,
  model: "",
  effort: "",
};

// The tool surface authored by the host (AgentToolSurface.Compose) and served as JSON. Shapes the
// deny-by-default derivation. Kept as an interface so the fetched JSON and the fallback share a type.
export interface ToolSurfaceSpec {
  // Native tools the agent always needs (ToolSearch loads MCP schemas on demand).
  alwaysAllowedNative: string[];
  // Native read tools, gated by allowNativeReads.
  readTools: string[];
  // Native writers/shells hard-removed unless the operator opts them in. Superset of enableableNative:
  // MultiEdit/NotebookEdit are blocked but NOT enableable, so they stay permanently denied.
  blockableNative: string[];
  // The tools an operator may re-enable from Settings (the offered writers + the opt-in web readers).
  // A policy's enabledTools are validated against this so a bogus toggle can't widen the surface. This
  // is the OptionalAgentTools catalog — NOT every blockable tool.
  enableableNative: string[];
  // The semantic/structural Roslyn edit MCP tools. Withheld from the agent UNLESS the operator turns
  // allowSemanticEdits on (default off). Names are unprefixed; deriveToolGating prefixes them. Always
  // registered in the MCP server — gating only removes them from the AGENT's advertised surface.
  semanticEditMcpTools: string[];
}

// Built-in fallback, used ONLY when the host's /guidance/tool-policy is unreachable. Must mirror
// AgentToolSurface.Compose() in C# — but the host is the source of truth; this is the lifeboat.
export const FALLBACK_TOOL_SURFACE: ToolSurfaceSpec = {
  alwaysAllowedNative: ["ToolSearch", "TodoWrite"],
  readTools: ["Read", "Grep", "Glob"],
  blockableNative: ["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash", "PowerShell"],
  // The offered opt-in set (writers Bash/PowerShell/Write/Edit + web) — NOT MultiEdit/NotebookEdit.
  enableableNative: ["Bash", "PowerShell", "Write", "Edit", "WebFetch", "WebSearch"],
  semanticEditMcpTools: [
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
  ],
};

// The set an operator may re-enable from Settings (H3): a policy's enabledTools are validated against
// this so a bogus toggle can't widen the surface. It is the host-authored catalog, NOT every blockable
// tool — MultiEdit/NotebookEdit are blocked but not in it, so they can never be opted back in.
export function enableableNative(surface: ToolSurfaceSpec): Set<string> {
  return new Set<string>(surface.enableableNative);
}

export interface ToolGating {
  // Native tools canUseTool will allow without pausing at the operator gate.
  allowedNative: Set<string>;
  // Fully-qualified tool names handed to the SDK's disallowedTools (never offered to the agent).
  disallowed: string[];
}

// Derive the governed tool surface from a policy + the host-authored surface. Pure: same inputs ->
// same outputs, no I/O. `mcpPrefix` is the workbench MCP tool prefix (e.g. "mcp__claude-workbench__")
// so the withheld MCP family can be disallowed by fully-qualified name.
export function deriveToolGating(
  policy: ToolPolicy,
  surface: ToolSurfaceSpec,
  mcpPrefix: string,
): ToolGating {
  const enabled = new Set(policy.enabledTools);

  const allowedNative = new Set<string>([
    ...surface.alwaysAllowedNative,
    ...(policy.allowNativeReads ? surface.readTools : []),
    ...policy.enabledTools,
  ]);

  // Native writers/shells the operator did not opt into.
  const disallowed = surface.blockableNative.filter((tool) => !enabled.has(tool));
  if (!policy.allowNativeReads) {
    disallowed.push(...surface.readTools);
  }
  // The semantic/structural edit family is withheld unless the operator opted in. Default off:
  // it costs extra round-trips without token savings (the runtime re-feeds the whole file anyway).
  if (!policy.allowSemanticEdits) {
    disallowed.push(...surface.semanticEditMcpTools.map((tool) => mcpPrefix + tool));
  }

  return { allowedNative, disallowed };
}
