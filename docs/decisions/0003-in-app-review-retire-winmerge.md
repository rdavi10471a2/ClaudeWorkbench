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

See: [merge review guide](../guide/merge-review.md).
