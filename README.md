# ClaudeWorkbench

A Blazor operator console for **governed, watched-source AI edits**, driven by **Claude** through the Claude Agent SDK.

The agent proposes changes to a watched solution; every change is composed against a local *Working* candidate, staged, and held at a human **accept/reject** gate before it ever touches real source. The engine that enforces this — indexing, edit sessions, staging, review gates, and an MCP tool surface — is extracted from **AIMonitor** and runs UI-agnostic here, with **no WinForms**.

> Status: **engine extracted and green**; the Blazor host + Claude sidecar are the next phase. See [Roadmap](#roadmap).

---

## Why this exists

Two proven pieces, recombined, with the backend swapped to Claude:

- **AIMonitor** — the governed engine (the hard part: Roslyn indexing, the two compile gates, session staging, post-accept freshness). Extracted here without its WinForms shell, MCP proxy hub, or stdio bridge.
- **CodexAppServerDemo** — the Blazor control-surface pattern and the agent-driver shape. Codex is being replaced by Claude.
- **New** — a thin `claude-sidecar` (Agent SDK) that drives Claude and registers the MCP surface, replacing the Codex JSON-RPC client; and sidecar-event logging replacing the old man-in-the-middle MCP proxy.

The move to Claude is deliberate: **real skills, hooks, and a programmatic operator gate** instead of policy prose you fight every turn.

## The governed loop

```
choose workspace → discover (index) → refresh_file / new_file → governed edit
   → stage session → operator review → accept / reject → post-accept reindex
```

- **Reason in the cloud, edit locally.** The model reasons from compact context; watched-source changes are composed against explicit local Working candidates and promoted only through review.
- **The gate is code, not a prompt.** Mutations (file writes, `accept_staged_review`) are intercepted by the sidecar's `PreToolUse` hook, surfaced to the Blazor UI, and applied only on operator approval.
- **Freshness is restored at accept.** The solution index rebuilds after an accepted decision — that is the normal point where downstream truth is refreshed.

## Architecture

```
Blazor host (ClaudeWorkbench)  ── spawns ──►  claude-sidecar (Node, Claude Agent SDK)
   │  hosts the engine + MCP surface             │  registers the MCP surface, drives Claude,
   │  renders UI + live log                      │  streams tool/turn events back to the host
   └── AIMonitor.* engine  (extracted; no WinForms / proxy / bridge)
        Core · Logging(thin) · MSBuild · Data · Workflow · Runtime · Indexing · McpServer
```

Details, including exactly how the sidecar registers the MCP surface and the logging model, are in
**[docs/Architecture.md](docs/Architecture.md)**. Short version:

- **MCP binding** — the sidecar is the MCP client; the Agent SDK connects to the engine's MCP server via its `mcpServers` option (recommended: the Blazor host serves MCP in-proc over HTTP; the sidecar registers a URL). Tools appear to the agent as `mcp__claude-workbench__*` (the HTTP host advertises `serverInfo.name` `claude-workbench` on port `6100`, distinct from the real `ai-monitor`). No proxy or bridge in the path.
- **Auth** — *unresolved, see [docs/Architecture.md](docs/Architecture.md#auth--unresolved-decision-required).* The Agent SDK's documented auth is `ANTHROPIC_API_KEY` (API billing); running headless on the Pro/Max **subscription** is not the documented path (possible via the `claude` CLI's `setup-token` OAuth for personal use, but unverified and ToS-restricted for shipped products). A money/ToS decision that gates runtime only, not the build.
- **Logging** — the engine logs to a JSON-lines file (and raises in-proc events for a live view); MCP-call telemetry is re-emitted from the sidecar's `tool_use`/`tool_result` events and hooks, not sniffed off a pipe.

## Repository layout

```
src/
  AIMonitor.Core/        settings, workspace paths, stable identifiers
  AIMonitor.Logging/     thin sink: IMonitorLogger + JsonLinesMonitorLogger + in-proc MonitorLogService
  AIMonitor.MSBuild/     MSBuild/Roslyn project + document loading
  AIMonitor.Data/        SQLite solution index store
  AIMonitor.Workflow/    edit sessions, staging, review gates
  AIMonitor.Runtime/     runtime state / orchestration
  AIMonitor.Indexing/    Roslyn semantic extraction → index
  AIMonitor.McpServer/   MCP tool surface (governed discovery + mutation + review) — stdio console host
  AIMonitor.Cli/         engine-side console runner (not in the runtime path; runs the non-unit suites)
  ClaudeWorkbench.Host/  in-proc ASP.NET host: same tool surface over Streamable HTTP (:6100) + /health
tests/
  unit/                  xUnit per-layer tests
  integration/           end-to-end over the CLI + engine
  smoke/                 console smoke runners (built, run via CLI/manually — not dotnet test)
samples/watched-solutions/   fixtures the ClaudeSmokes/integration tests operate on
docs/                    Architecture.md and design notes
```

Project/namespace names are kept as `AIMonitor.*` from the extraction so the port stayed mechanical and the ported tests prove fidelity. Rebranding, if ever wanted, is an isolated later pass.

## Build & test

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test  ClaudeWorkbench.slnx
```

The engine builds with **0 errors** and no WinForms/proxy/bridge. Current test state:

| Layer | Tests |
|---|---|
| Core · Logging · Runtime | 7 · 3 · 2 |
| MSBuild · Indexing | 6 · 6 |
| Workflow (incl. ClaudeSmokes over `samples/`) | 42 |
| Data | 27 pass · 1 skipped |
| Integration | 68 pass · 1 skipped |

- **One `[Fact(Skip)]`** (Data): the `razor-generated:*` reference-row assertion is environment-dependent — those rows only index when the host Roslyn matches the SDK's Razor source generator. The `razor:*` code-behind path stays covered. (Document-don't-pin.)
- **Skipped in Integration**: a by-design skip carried over from AIMonitor.
- **Smoke runners** (`ToolSmokeTests`, `LanguageCorpusSmokeTests`, `SmokeTests`) are console `Main` programs — they build but are executed via the CLI/manually, not `dotnet test`.

## Roadmap

- [x] Extract the AIMonitor engine, layer by layer, no WinForms/proxy/bridge, tests green
- [x] In-proc ASP.NET MCP endpoint on the engine's tool classes (`ClaudeWorkbench.Host`, Streamable HTTP on `:6100`, server name `claude-workbench`, full 60-tool surface smoke-verified)
- [ ] `claude-sidecar` (Agent SDK): session lifecycle, `PreToolUse` operator gate, event stream
- [ ] Blazor host: workspace dashboard, staging/review queue, live log, operator gate UI
- [ ] Bring in AIMonitor's `docs/claude-skills/` cards as-is (the doctrine is already in skill form; the tool surface is identical, so reuse — don't rewrite the policy)

## Provenance

Lineage: [AIMonitor](https://github.com/rdavi10471a2/AIMonitor) (engine) + CodexAppServerDemo (Blazor control-surface pattern) → ClaudeWorkbench (Claude backend). The engine here is a faithful extraction; identical code compiles and the ported tests pass.
