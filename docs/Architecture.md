# ClaudeWorkbench Architecture

## Shape

```
Blazor host (ClaudeWorkbench)  ──spawns──►  claude-sidecar (Node, Claude Agent SDK)
   │  hosts the engine + MCP surface           │  registers the MCP surface + drives Claude
   │  renders UI + live log                     │  streams events back to the host
   └── AIMonitor.* engine (extracted, no WinForms/proxy/bridge)
        Core · Logging(thin) · MSBuild · Data · Workflow · Runtime · Indexing · McpServer
```

## Engine extraction

The governed engine was extracted from AIMonitor, one testable layer at a time, keeping the
`AIMonitor.*` project/namespace names so the port stayed mechanical and the ported tests prove
fidelity. Left behind: WinForms App, the MCP proxy hub, the stdio bridge, and the cross-process
log pipe. Kept a thin logging sink (`IMonitorLogger` + `JsonLinesMonitorLogger`, plus the in-proc
`MonitorLogService` which is both a file sink and an event source for a live UI view).

## Logging

- Engine narration (index rebuilds, staging, gate results, errors) logs via `IMonitorLogger` to a
  JSON-lines file — and, in-process, `MonitorLogService` raises `EntryWritten` for a live view.
- The man-in-the-middle MCP proxy telemetry is **not** ported. Its equivalent (tool name, args,
  result, duration) is re-emitted from the sidecar's Agent SDK `tool_use`/`tool_result` events and
  `PreToolUse`/`PostToolUse` hooks.

## MCP binding — how the TS backend registers the MCP surface

The engine's MCP surface is `AIMonitor.McpServer` (ModelContextProtocol server; the same governed
tools). The **sidecar** is the MCP client: the Claude Agent SDK connects to the server via the
`mcpServers` option — the SDK performs the initialize/list handshake and routes tool calls. There
is no proxy or bridge in the path.

Two hosting shapes:

**A. In-process HTTP (recommended).** The Blazor host hosts the MCP server in-proc over HTTP
(ASP.NET MCP endpoint, `ModelContextProtocol.AspNetCore`), reusing the same tool classes; only the
transport differs from the stdio console entrypoint. The sidecar registers a URL:

```ts
const q = query({
  prompt,
  options: {
    mcpServers: {
      workbench: { type: "http", url: "http://localhost:6289/mcp" }
    },
    // gate + observe every governed tool call
    hooks: { PreToolUse: [{ matcher: "mcp__workbench__.*", hooks: [operatorGate] }] },
  },
});
```

Why in-proc HTTP: engine + MCP + UI live in one process, so logging is in-process
(`MonitorLogService` event source → Blazor live view) with **no cross-process pipe** — which is
exactly why the pipe transport was dropped. The sidecar stays thin.

**B. Stdio subprocess.** The SDK spawns the server as a child over stdio:

```ts
mcpServers: {
  workbench: {
    type: "stdio",
    command: "dotnet",
    args: ["<abs>/AIMonitor.McpServer.dll", "--repo-root", "<repo>", "--config", "<config>"],
  }
}
```

Either way the agent sees the tools as `mcp__workbench__<tool>` (e.g. `mcp__workbench__refresh_file`,
`mcp__workbench__stage_edit_session_for_review`, `mcp__workbench__accept_staged_review`). Allow them
via `allowedTools`/`permissionMode`; gate mutations (accept/reject, file writes) through the
`PreToolUse` hook, which pauses and asks the Blazor operator UI, then returns allow/deny.

The extracted `AIMonitor.McpServer` today is the stdio console host. Adding the in-proc ASP.NET HTTP
endpoint is a host-layer task (reuses the tool classes unchanged), scheduled with the Blazor host +
sidecar work, not part of the engine extraction.

## Auth

The sidecar uses the machine's existing Claude subscription login (do not set `ANTHROPIC_API_KEY`;
the SDK falls back to the cached OAuth / `CLAUDE_CODE_OAUTH_TOKEN` from `claude setup-token`). No
API key or corporate account required for interactive/subscription use. Confirm entitlement for
headless SDK automation with Anthropic if you need it airtight.
