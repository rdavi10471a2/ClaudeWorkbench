# 0005 — The edit session is the atomic unit; a single reject voids it

**Status:** Accepted · **Date:** 2026-07

## Context

An edit session can stage several watched files as one logical change. Today the operator
reviews them one at a time in the Merge Review dialog, and **each Accept writes its file to
watched source immediately** — only the *terminal* accept (the last pending file) runs the
authoritative GATE-2 build over the combined session overlay
(`EngineReviewWorkflow.Accept`, `terminal` at line ~118).

Two problems follow from writing per file.

**1. Half a refactor is not a smaller refactor — it is broken source.** The repo's own
`CalculatorSample` fixture drives the agent to *"move methods across this file and
AdvancedCalculations.cs."* Under per-file accept the operator can accept the file a method
moved *into* and reject the one it moved *out of*, leaving the method defined twice; or the
reverse, leaving it defined nowhere. Neither file is individually wrong. The change is only
coherent as a set.

**2. A reject after earlier accepts leaves source changed with nobody informed.** Accept
files 1..n-1 (non-terminal, so no build and no agent summary), then reject the last pending
file: the session ends with no terminal accept, the already-written files produce no
`AgentSummary`, and the agent is never told that watched source now contains its edits.

There is also a mismatch worth naming: GATE 2 compiles the **combined session overlay**, but
per-file accept writes files **individually**. The thing validated is not the thing written.

## Decision

**The edit session is the atomic unit of decision.**

- Per-file **Accept is an approval, not a write.** It records the operator's decision for
  that file and advances the review.
- **Watched source is written once**, on the terminal accept, after the combined-overlay
  GATE-2 build passes — all approved files together.
- **A single Reject voids the entire session.** Every record in it becomes void, the review
  ends, and **nothing is written** — including files approved earlier in the same session.
- The agent is told on rejection (which file, why, and that nothing was written) so it can
  re-stage the whole set.

An operator who wants most of a session rejects it and says why. The agent re-stages. That
is the governed loop working as designed, not a workaround.

## Consequences

- **The "source changed, agent uninformed" hole disappears** rather than being patched:
  there is no longer any path where watched source changes without a terminal accept, and
  the terminal accept always produces a summary.
- **What is validated is what is written.** The combined overlay GATE-2 builds, then that
  same set is written.
- **Closing the dialog or crashing mid-session leaves nothing half-applied.** Today it
  leaves the first N files written. Unresolved sessions stay pending and re-reviewable.
- **No "accepted but not yet written" persisted state is needed.** A session either reaches
  full approval and writes everything, or it dies and writes nothing. This is *simpler* than
  deferring writes while still allowing partial acceptance.
- **Single-file sessions are unaffected.** Their first accept is already terminal, so the
  behaviour — and every single-file test — is identical before and after. The blast radius
  is limited to multi-file sessions, which is exactly the broken case.
- **Reject becomes cheap and safe**, which is the right incentive for a human gate: the
  operator never has to reason about what a partial reject already did to their source.
- Multi-file assertions in `CliIndexQueryTests` and `McpPlannedSessionSurfaceTests` that
  expect watched source to change after a non-terminal accept must be updated — they now
  correctly observe it unchanged until the session completes.

## Notes for implementation

- Check whether the engine already models a dead session (`EnsureSessionCanEdit`,
  `SupersedeActiveRecordsForFile`, session status in `WorkflowEditService`) and reuse it.
  Building a second, parallel notion of invalidation next to an existing one is the easy
  mistake here.
- Writing N files at the end is still not atomic. Keep the existing temp-file-then-rename
  per file to keep the window small. This is strictly better than today's guaranteed-partial
  behaviour, not a distributed transaction — say so rather than implying otherwise.
