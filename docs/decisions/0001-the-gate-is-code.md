# 0001 — The gate is code, not policy prose

**Status:** Accepted · **Date:** 2026-07

## Context

The product lets an AI agent change real source. The failure we must prevent is the agent
mutating watched source without human approval. AIMonitor's predecessor tried to enforce
this partly through **policy prose** injected into the model's context every turn — which
you fight every turn and can never fully trust.

## Decision

Enforcement is **code**, not prompt text.

- The agent is **deny-by-default**. The sidecar's `canUseTool` allows only read-only native
  tools (`Read`/`Grep`/`Glob` + `ToolSearch`/`TodoWrite`) and the `claude-workbench` MCP
  tools; `Write`/`Edit`/`Bash`/`PowerShell`/`Agent`/`WebFetch`/anything unknown are denied.
- Every mutation routes through the sidecar operator gate **and** the server-side
  planned-session gate (`EnsurePlannedMutationAllowed`).
- Watched source is written **only** by the operator's merge-review Accept, host-side —
  never by the agent.
- Guidance (staging doctrine, formatting) is **on-demand** (MCP-served / skill cards), not
  a per-turn prose dump.

## Consequences

- A denied gate cannot be talked around, regardless of what the model was told.
- The system prompt stays minimal; correctness comes from the gate + on-demand guidance.
- The move to Claude was made partly *because* real hooks + a programmatic gate replace
  policy prose you fight every turn.
- Trade-off: the agent is genuinely constrained — it cannot self-serve a shell or write a
  file. That is the point.

## Update — 2026-07-19

Two hardening passes strengthened the same decision:

- **The allow-set can no longer be widened from the wire.** A turn's `enabledTools` is
  intersected with a fixed blockable set (`Write`, `Edit`, `MultiEdit`, `NotebookEdit`, `Bash`,
  `PowerShell`) — the writers an operator may deliberately opt back in. Any other name in a
  `/prompt` body is discarded, so the deny-by-default surface can't be talked *or posted* around.
- **The control surface is loopback-only.** The sidecar binds `127.0.0.1` and rejects requests
  with a non-localhost `Host` header or a non-local `Origin` (DNS-rebinding defense).
- **The write is validate-then-write.** The host-side accept runs the authoritative build before
  it touches watched source — see [0003](0003-in-app-review-retire-winmerge.md).

See: [Sidecar](../components/Sidecar.md), [AIMonitor.Workflow](../components/AIMonitor.Workflow.md),
[Architecture §5](../architecture/Architecture.md#5-governance--the-gate-is-code).
