# 0006 — Delete and rename are never auto-approvable

**Status:** Accepted · **Date:** 2026-07 · **Applies to:** file-level delete/rename MCP tools (not yet built)

## Context

Auto-approve is a per-thread toggle. When it is on, `canUseTool` short-circuits the operator
gate for every tool in `GATED_TOOLS` (`sidecar/src/index.ts`, the `activeAutoApprove` branch).

That is safe today, and the comment above it says exactly why:

> candidate mutations proceed without a per-call prompt; the merge-review Accept still gates
> the write to watched source

Every gated tool today mutates the **monitor-owned Working candidate**. None of them touch the
watched tree. The real irreversibility — bytes reaching your source — is gated *separately*, at
the operator's Accept, which auto-approve does not bypass. So auto-approve trades away
per-call prompts, not safety.

**File-level delete and rename break that assumption.** They have no Working candidate, so
there is nothing to stage, nothing to diff, and nothing for merge review to hold. Their effect
is not "propose bytes for review" but "remove or move a file". Riding the same auto-approve
path would let a turn delete watched files with no gate anywhere in the loop — the one thing
the whole design exists to prevent.

Deletion is also the least recoverable operation in the product. The retrieval backup
(`WorkflowEditPaths` retrieval-backups) only exists for files that went through `refresh_file`.
A file the agent never refreshed has no snapshot to restore from.

## Decision

**A class of tools is never auto-approvable.** Membership is a property of the tool, not of the
thread's policy, and the check runs **before** the `activeAutoApprove` branch — auto-approve
cannot reach it.

Initial members: any file-level **delete** and **rename/move** on watched source.

The operator sees a gate prompt for these every time, in every thread, regardless of settings.

## Consequences

- Auto-approve keeps its current meaning — *skip per-call prompts for candidate edits* — and
  regains the property that makes it defensible: **it can never be the reason something
  irreversible happened.**
- Adding a destructive tool cannot silently inherit auto-approve. Enforcement exists before the
  tools do, so the failure mode is "a new tool is gated more than intended", not "a new tool
  slipped through".
- The gate prompt must show what is actually at stake: the resolved path, and for a rename both
  source and destination. A prompt saying only `delete_file` is not consent.
- This does not settle **how** delete/rename fit the staged review model — see the open
  question below. It only settles that they cannot be auto-approved.

## Open — deliberately not decided here

How a deletion is *reviewed*. The merge-review surface is a content diff, and a deletion has no
diff; a rename is delete+create with an identity to preserve. Before those tools are built:

- what does Merge Review render for a deletion?
- does GATE 2 build the session overlay **without** the file, to prove the solution still
  compiles once it is gone?
- does a deletion get a retrieval backup first, so it is recoverable?

Given [ADR 0005](0005-edit-session-is-atomic.md), the likely answer is that a delete belongs in
the session as a decision alongside the file writes, and lands with them at the terminal accept.
That should be worked out before the tools exist, not after.
