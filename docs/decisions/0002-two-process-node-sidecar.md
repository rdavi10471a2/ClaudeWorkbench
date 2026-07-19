# 0002 — Two processes: a Node sidecar for the Agent SDK

**Status:** Accepted · **Date:** 2026-07

## Context

The engine and UI are .NET. But the **Claude Agent SDK is Node-only** — there is no .NET
Agent SDK. We need the SDK to drive Claude, register the MCP surface, and enforce the
`canUseTool` gate.

## Decision

Run **two processes**:

- **Blazor Host (.NET 10, :6100)** — hosts the extracted `AIMonitor.*` engine, serves the
  `claude-workbench` MCP surface over Streamable HTTP, renders the operator console, and is
  the only writer of watched source.
- **Node sidecar (:6110)** — runs the Claude Agent SDK, is the MCP *client* back to the
  host, enforces the operator gate, and streams neutral events back over SSE.

The host **launches and supervises** the sidecar (single-start), so the installed app is
one `dotnet run` / one exe.

MCP binding is **in-proc HTTP**: the host serves MCP; the sidecar registers a URL. This
keeps engine + MCP + UI in one process, so logging and state are in-process — **no
cross-process pipe** (which is exactly why the predecessor's log pipe + MCP proxy were
dropped).

## Consequences

- Deployment needs the .NET runtime **plus** a small Node runtime (bundleable) **plus** the
  sidecar folder (`dist` + `node_modules`, which includes the bundled `claude` CLI).
- The sidecar stays **thin** — a driver + gate + event bus. Business logic stays in .NET.
- Auth is the local `claude` CLI's subscription login; no API key for personal use.

## Update — 2026-07-19

The two-process shape is unchanged, but it is no longer the whole picture: **one host+sidecar
pair is one workspace**. To run several watched solutions at once, the optional
[`ClaudeWorkbench.Launcher`](../components/ClaudeWorkbench.Launcher.md) starts a *pair per
workspace* — its own free port pair, its own config, its own runtime folder under
`<workbench root>\runtime\<workspace>` — with host + sidecar + browser window held in a Windows
Job Object so they die together. The `:6100` / `:6110` above remain the plain-`dotnet run`
defaults. The Launcher also sets `CWB_EXIT_WITH_BROWSER=1`, making the browser window own the
instance's lifetime.

See: [Architecture §2](../architecture/Architecture.md#2-containers-c4-level-2--the-two-processes),
[Sidecar](../components/Sidecar.md), [ClaudeWorkbench.Host](../components/ClaudeWorkbench.Host.md).
