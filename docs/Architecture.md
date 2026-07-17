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
      "claude-workbench": { type: "http", url: "http://localhost:6100/mcp" }
    },
    // gate + observe every governed tool call
    hooks: { PreToolUse: [{ matcher: "mcp__claude-workbench__.*", hooks: [operatorGate] }] },
  },
});
```

Why in-proc HTTP: engine + MCP + UI live in one process, so logging is in-process
(`MonitorLogService` event source → Blazor live view) with **no cross-process pipe** — which is
exactly why the pipe transport was dropped. The sidecar stays thin.

**B. Stdio subprocess.** The SDK spawns the server as a child over stdio:

```ts
mcpServers: {
  "claude-workbench": {
    type: "stdio",
    command: "dotnet",
    args: ["<abs>/AIMonitor.McpServer.dll", "--repo-root", "<repo>", "--config", "<config>"],
  }
}
```

Either way the agent sees the tools as `mcp__claude-workbench__<tool>` (e.g.
`mcp__claude-workbench__refresh_file`, `mcp__claude-workbench__stage_candidate_for_review`,
`mcp__claude-workbench__record_diff_decision`). Allow them via `allowedTools`/`permissionMode`; gate
mutations (staging, `record_diff_decision`, file writes) through the `PreToolUse` hook, which pauses
and asks the Blazor operator UI, then returns allow/deny.

## HTTP MCP host (implemented)

`src/ClaudeWorkbench.Host` is the in-proc ASP.NET host (option A above). It reuses the engine's
`AIMonitorTools` tool class unchanged (`.AddMcpServer(...).WithHttpTransport().WithTools<AIMonitorTools>()`),
so the same governed surface the stdio console exposes is served over Streamable HTTP at `/mcp`, plus a
`/health` probe. It advertises `serverInfo.name` = **`claude-workbench`** (deliberately distinct from
the real monitor's `ai-monitor`, so it is unmistakable in an MCP server list) and defaults to
**`http://localhost:6100`** (`appsettings.json` → `Kestrel:Endpoints:Http`; the real monitor is left
its own port). It takes the same `--repo-root`/`--config` arguments as the console host to locate the
watched-solution `Monitor` settings. Smoke-verified: `initialize` → `tools/list` returns the full
60-tool surface.

## Governance model — gate + on-demand skills, no turn-start policy

Governance is **not** policy prose pushed into the model's context. Two mechanisms carry it, and
neither is a per-turn text dump:

- **Enforcement is code.** Every mutation routes through the sidecar `PreToolUse` hook (operator
  accept/reject) and the server-side planned-session gate (`EnsurePlannedMutationAllowed`). A denied
  gate cannot be talked around, regardless of what the model was or wasn't told.
- **Guidance is on-demand.** AIMonitor's existing `docs/claude-skills/` cards are **reused as-is** —
  the doctrine is already in skill form and the tool surface is identical, so it is not rewritten
  into new skills. The agent loads the relevant card when a task needs it (Roslyn-first discovery,
  staging, review gates, formatting), not before.

**Explicitly removed:** loading policy at turn start, and injecting a monitor-style `CLAUDE.md`
doctrine block on every turn. Fighting compliance through ever-present prose is the pattern
ClaudeWorkbench moves *away* from — it is the reason for the switch to Claude. The sidecar system
prompt stays minimal; correctness comes from the gate plus skills pulled on demand.

**Concrete sidecar enforcement (not just intent).** The sidecar constrains the agent so the gate
cannot be side-stepped:

- **CWD = the watched solution's folder**, auto-derived from the host `/health` (tracks whatever
  `WatchedSolutionPath` is configured). The agent reasons in the real workspace context.
- **Read-only on the watched workspace.** `Read`/`Grep`/`Glob` are allowed; `Write`, `Edit`,
  `MultiEdit`, `NotebookEdit`, and `Bash` are in `disallowedTools`. The agent therefore cannot mutate
  watched source with its native tools — every change must go through the `claude-workbench` MCP
  (`refresh_file`/`submit_file` → `stage_candidate_for_review` → operator review → `record_diff_decision`),
  which is where the `canUseTool` approval gate lives.
- **Optional MCP-only reads.** Per turn (`POST /prompt { readTools: false }`) the native read tools are
  also disallowed, forcing *all* access — reads included — through the MCP surface (`get_file`,
  `find_indexed_symbols`). Off by default (reads via native tools are ergonomic and safe).
- **`strictMcpConfig: true`.** Only the `claude-workbench` server is exposed; the machine's account/
  user MCP connectors (project `.mcp.json`, `~/.claude` connectors, plugins) are ignored, so nothing
  unrelated leaks into the agent's tool surface.

## Auth — subscription verified for personal use

**Verified end-to-end (2026-07-17), SDK `@anthropic-ai/claude-agent-sdk` 0.3.212.** With
`ANTHROPIC_API_KEY` explicitly unset, the sidecar drove a full turn on the machine's **Claude
subscription login**: the SDK spawned the native `claude` CLI, which authenticated from the cached
`~/.claude/.credentials.json` (subscription, no API key), registered the `claude-workbench` MCP
server over HTTP, called `get_monitor_status`, and returned `success`. So headless Agent-SDK use on a
Pro/Max subscription **works for personal use of your own login** — the API key is not required.

Boundaries, kept honest:

- **Personal operator console, your own login** — works today, proven. No `ANTHROPIC_API_KEY`, no
  per-token API billing; it consumes the subscription. (`claude setup-token` →
  `CLAUDE_CODE_OAUTH_TOKEN` is the route if you need a headless token without an interactive login
  present.)
- **Shipping to teammates / a hosted product** — the Agent SDK docs state Anthropic does not allow
  third-party products to offer *other people's* claude.ai login. That path needs
  `ANTHROPIC_API_KEY` (or Bedrock/Vertex/Foundry routing), i.e. API billing. This is a distribution
  decision, not a code one, and does not affect the build.

The sidecar sets no `ANTHROPIC_API_KEY` and injects no auth; it inherits whatever the local `claude`
CLI is logged into.
