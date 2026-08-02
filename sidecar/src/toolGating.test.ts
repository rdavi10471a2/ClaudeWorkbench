// Unit tests for the governance boundary on the agent's NATIVE tool surface. Pure (no live stack):
// they pin deriveToolGating over the FALLBACK surface, which mirrors the host-authored one. The MCP
// surface is owned by the C# server (a tool it doesn't register isn't served), so the sidecar gates
// only native SDK tools — that is what these tests cover.

import test from "node:test";
import assert from "node:assert/strict";
import {
  type ToolPolicy,
  DEFAULT_TOOL_POLICY,
  FALLBACK_TOOL_SURFACE,
  enableableNative,
  deriveToolGating,
} from "./toolGating.js";

function policy(overrides: Partial<ToolPolicy> = {}): ToolPolicy {
  return { ...DEFAULT_TOOL_POLICY, ...overrides };
}

test("default policy: reads allowed, writers/shells blocked", () => {
  const { allowedNative, disallowed } = deriveToolGating(policy(), FALLBACK_TOOL_SURFACE);

  for (const r of FALLBACK_TOOL_SURFACE.readTools) {
    assert.ok(allowedNative.has(r), `${r} should be allowed`);
    assert.ok(!disallowed.includes(r), `${r} should not be disallowed`);
  }
  for (const w of FALLBACK_TOOL_SURFACE.blockableNative) {
    assert.ok(disallowed.includes(w), `${w} should be disallowed by default`);
  }
});

test("allowNativeReads:false disallows the read tools", () => {
  const { allowedNative, disallowed } = deriveToolGating(
    policy({ allowNativeReads: false }),
    FALLBACK_TOOL_SURFACE,
  );
  for (const r of FALLBACK_TOOL_SURFACE.readTools) {
    assert.ok(disallowed.includes(r), `${r} should be disallowed when reads off`);
    assert.ok(!allowedNative.has(r), `${r} should not be allowed when reads off`);
  }
});

test("enabling an opt-in tool removes it from disallowed and adds it to allowedNative", () => {
  const { allowedNative, disallowed } = deriveToolGating(
    policy({ enabledTools: ["Bash"] }),
    FALLBACK_TOOL_SURFACE,
  );
  assert.ok(!disallowed.includes("Bash"), "Bash should not be disallowed once enabled");
  assert.ok(allowedNative.has("Bash"), "Bash should be allowed once enabled");
  assert.ok(disallowed.includes("Write"), "Write should still be disallowed");
});

test("always-allowed native tools are never disallowed, in any policy", () => {
  for (const p of [policy(), policy({ allowNativeReads: false })]) {
    const { allowedNative, disallowed } = deriveToolGating(p, FALLBACK_TOOL_SURFACE);
    for (const a of FALLBACK_TOOL_SURFACE.alwaysAllowedNative) {
      assert.ok(allowedNative.has(a), `${a} should always be allowed`);
      assert.ok(!disallowed.includes(a), `${a} should never be disallowed`);
    }
  }
});

test("MultiEdit / NotebookEdit are blocked but NOT enableable (permanently denied)", () => {
  const enableable = enableableNative(FALLBACK_TOOL_SURFACE);
  for (const t of ["MultiEdit", "NotebookEdit"]) {
    assert.ok(FALLBACK_TOOL_SURFACE.blockableNative.includes(t), `${t} should be blockable`);
    assert.ok(!enableable.has(t), `${t} should not be enableable`);
  }
});
