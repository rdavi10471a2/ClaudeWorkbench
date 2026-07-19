# 0003 — In-app merge review; retire WinMerge

**Status:** Accepted · **Date:** 2026-07

## Context

In AIMonitor, the operator reviewed a staged edit by launching **WinMerge** (an external
diff tool) and saving the merge, which wrote watched source. That required WinMerge on
disk, an external window, and a "did the human save?" handshake.

## Decision

Review and merge happen **in-app**. The operator reviews a staged edit in the Blazor
**Merge Review** dialog — a **DiffPlex** side-by-side view — and **Accept** writes the
staged bytes to watched source host-side (`EngineReviewWorkflow.Accept`), then records the
decision. No external tool.

The DiffPlex renderer is a shared **`DiffView`** component used by *both* the merge-review
dialog and the Git panel, so diffs look identical everywhere.

## Consequences

- WinMerge is **retired** for the operator console. No external diff tool to install.
- The `WinMergeDiffToolLauncher` + `StagedDiffLaunchWorkflow` launch path still exists for
  the **MCP/CLI** path (legacy) — see [AIMonitor.Runtime](../components/AIMonitor.Runtime.md).
- Residual "WinMerge" wording lingers in some staging-guide/validation text and settings
  (`WinMergeCandidatePaths`); the stale self-check *warning* was removed. A fuller text
  cleanup is outstanding.
- Trade-off (known gap): the in-app Accept path calls `StagedDecisionWorkflow.Record`
  without `terminalValidationRecords`, so the authoritative terminal overlay build (GATE 2)
  does not run from the Blazor path — see
  [AIMonitor.Indexing](../components/AIMonitor.Indexing.md) and
  [ClaudeWorkbench.Host](../components/ClaudeWorkbench.Host.md).

## Update — 2026-07-19

The trade-off recorded above has been **closed**. `EngineReviewWorkflow.Accept` now runs the
authoritative GATE-2 overlay build itself, and runs it **before** any byte reaches watched
source: still-pending check → staged re-hash → real overlay build → *then* an atomic
temp-file-plus-rename write. A failed build is a hard stop (nothing written) unless the operator
takes *Accept With Validation Override*. `StagedDecisionWorkflow.Record` is still called without
`terminalValidationRecords` — now deliberately, so the build does not run twice.

Also since: the retired diff tool left the *guardrails* too — the `diff-tool-available`
self-check was dropped, so no code path expects an external diff tool. `WinMergeCandidatePaths`
survives as a settings key (the Launcher writes it empty) and the
`WinMergeDiffToolLauncher` + `StagedDiffLaunchWorkflow` launch path still exists for the MCP/CLI
route, as recorded above.

See: [merge review guide](../guide/merge-review.md),
[Architecture §4](../architecture/Architecture.md#4-the-two-gates).
