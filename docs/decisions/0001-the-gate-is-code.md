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

See: [Sidecar](../components/Sidecar.md), [AIMonitor.Workflow](../components/AIMonitor.Workflow.md),
[Architecture §5](../architecture/Architecture.md#5-governance--the-gate-is-code).
