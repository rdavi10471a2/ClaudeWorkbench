// Unit tests for the governance boundary on the agent's tool surface. Pure (no live stack): they
// pin deriveToolGating over the FALLBACK surface, which mirrors the host-authored one. This is the
// coverage that was missing — the smoke test only ever drove {autoApprove:true} and never asserted
// that reads gate, writers/shells are blocked, or the semantic family is withheld by default.

import test from "node:test";
import assert from "node:assert/strict";
import {
  type ToolPolicy,
  DEFAULT_TOOL_POLICY,
  FALLBACK_TOOL_SURFACE,
  enableableNative,
  deriveToolGating,
} from "./toolGating.js";

const PREFIX = "mcp__claude-workbench__";
const semantic = (t: string) => PREFIX + t;

// The write surface that must ALWAYS remain available to the agent (the governed keep-set). These
// are MCP tools, so they are never in the native disallow list; this guards against a future edit
// accidentally sweeping them into the withheld set.
const KEEP_SET = ["submit_file", "refresh_file", "replace_text_in_file"];

function policy(overrides: Partial<ToolPolicy> = {}): ToolPolicy {
  return { ...DEFAULT_TOOL_POLICY, ...overrides };
}

test("default policy: reads allowed, writers/shells blocked, semantic family withheld", () => {
  const { allowedNative, disallowed } = deriveToolGating(policy(), FALLBACK_TOOL_SURFACE, PREFIX);

  // reads on by default
  for (const r of FALLBACK_TOOL_SURFACE.readTools) {
    assert.ok(allowedNative.has(r), `${r} should be allowed`);
    assert.ok(!disallowed.includes(r), `${r} should not be disallowed`);
  }
  // native writers/shells blocked
  for (const w of FALLBACK_TOOL_SURFACE.blockableNative) {
    assert.ok(disallowed.includes(w), `${w} should be disallowed by default`);
  }
  // semantic family withheld (prefixed) by default
  for (const s of FALLBACK_TOOL_SURFACE.semanticEditMcpTools) {
    assert.ok(disallowed.includes(semantic(s)), `${s} should be withheld by default`);
  }
});

test("allowNativeReads:false disallows the read tools", () => {
  const { allowedNative, disallowed } = deriveToolGating(
    policy({ allowNativeReads: false }),
    FALLBACK_TOOL_SURFACE,
    PREFIX,
  );
  for (const r of FALLBACK_TOOL_SURFACE.readTools) {
    assert.ok(disallowed.includes(r), `${r} should be disallowed when reads off`);
    assert.ok(!allowedNative.has(r), `${r} should not be allowed when reads off`);
  }
});

test("allowSemanticEdits:true exposes the semantic family (none withheld)", () => {
  const { disallowed } = deriveToolGating(
    policy({ allowSemanticEdits: true }),
    FALLBACK_TOOL_SURFACE,
    PREFIX,
  );
  for (const s of FALLBACK_TOOL_SURFACE.semanticEditMcpTools) {
    assert.ok(!disallowed.includes(semantic(s)), `${s} should be available when semantic edits on`);
  }
});

test("enabling an opt-in tool removes it from disallowed and adds it to allowedNative", () => {
  const { allowedNative, disallowed } = deriveToolGating(
    policy({ enabledTools: ["Bash"] }),
    FALLBACK_TOOL_SURFACE,
    PREFIX,
  );
  assert.ok(!disallowed.includes("Bash"), "Bash should not be disallowed once enabled");
  assert.ok(allowedNative.has("Bash"), "Bash should be allowed once enabled");
  // other blockables stay blocked
  assert.ok(disallowed.includes("Write"), "Write should still be disallowed");
});

test("always-allowed native tools are never disallowed, in any policy", () => {
  for (const p of [policy(), policy({ allowNativeReads: false }), policy({ allowSemanticEdits: true })]) {
    const { allowedNative, disallowed } = deriveToolGating(p, FALLBACK_TOOL_SURFACE, PREFIX);
    for (const a of FALLBACK_TOOL_SURFACE.alwaysAllowedNative) {
      assert.ok(allowedNative.has(a), `${a} should always be allowed`);
      assert.ok(!disallowed.includes(a), `${a} should never be disallowed`);
    }
  }
});

test("the governed keep-set is never withheld, in any policy", () => {
  for (const p of [policy(), policy({ allowSemanticEdits: false }), policy({ allowNativeReads: false })]) {
    const { disallowed } = deriveToolGating(p, FALLBACK_TOOL_SURFACE, PREFIX);
    for (const k of KEEP_SET) {
      assert.ok(!disallowed.includes(k), `${k} (bare) must never be disallowed`);
      assert.ok(!disallowed.includes(semantic(k)), `${k} (prefixed) must never be disallowed`);
    }
  }
});

test("MultiEdit / NotebookEdit are blocked but NOT enableable (permanently denied)", () => {
  const enableable = enableableNative(FALLBACK_TOOL_SURFACE);
  for (const t of ["MultiEdit", "NotebookEdit"]) {
    assert.ok(FALLBACK_TOOL_SURFACE.blockableNative.includes(t), `${t} should be blockable`);
    assert.ok(!enableable.has(t), `${t} should not be enableable`);
  }
  // even if a crafted policy lists them, they cannot be enabled through the surface's set
  assert.ok(!enableable.has("MultiEdit"));
});
