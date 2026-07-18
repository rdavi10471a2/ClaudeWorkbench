# ClaudeWorkbench

A Blazor operator console for **governed, watched-source AI edits**, driven by **Claude** through the Claude Agent SDK.

The agent proposes changes to a watched solution; every change is composed against a local *Working* candidate, staged, and held at a human **accept/reject** gate before it ever touches real source. The engine that enforces this — indexing, edit sessions, staging, review gates, and an MCP tool surface — is extracted from **AIMonitor** and runs UI-agnostic here, with **no WinForms**.

> Status: **working end-to-end.** Engine extracted + green; Blazor host + Claude sidecar live; the full governed loop (stage → in-app review/merge → operator accept writes source → post-accept build + reindex), **session continuity** (resume + New Thread), and the agent's **AskUserQuestion → operator questions dialog** are all built and operator-verified on the subscription. See [Roadmap](#roadmap).

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
- **Auth** — **subscription verified for personal use** (see [docs/Architecture.md](docs/Architecture.md#auth--subscription-verified-for-personal-use)). A full turn ran headless with no `ANTHROPIC_API_KEY`: the SDK inherits the local `claude` CLI's cached subscription login. `ANTHROPIC_API_KEY` (API billing) is only needed to ship to other people, not to run this yourself.
- **Logging** — the engine logs to a JSON-lines file (and raises in-proc events for a live view); MCP-call telemetry is re-emitted from the sidecar's `tool_use`/`tool_result` events and hooks, not sniffed off a pipe.
- **Agent workspace (CWD & tools)** — the agent's working directory is the **watched solution's folder** (auto-derived from the host's config, so it tracks whatever `WatchedSolutionPath` points at). Tool access is **deny-by-default**: the only native tools allowed are the read-only `Read`/`Grep`/`Glob` (+ `ToolSearch`/`TodoWrite`); the `claude-workbench` MCP tools are allowed (mutations paused at the operator gate); **everything else is denied** — `PowerShell`, `Bash`, `Write`/`Edit`, `Agent`, `Workflow`, `WebFetch`, and any future/unknown tool. So the watched workspace is read-only to the agent and every change must go through the governed MCP (`submit_file` → stage → operator review). Native read tools are optional **per turn** (`POST /prompt { readTools: false }`) to force *all* access — reads included — through the MCP surface. `strictMcpConfig: true` exposes only `claude-workbench`; the machine's account/user MCP connectors (e.g. claude.ai connectors) do **not** leak in.

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
sidecar/                 Node/TS Claude Agent SDK driver: canUseTool operator gate,
                         neutral event contract, SSE stream to the host (events/gate/bus/index)
```

Project/namespace names are kept as `AIMonitor.*` from the extraction so the port stayed mechanical and the ported tests prove fidelity. Rebranding, if ever wanted, is an isolated later pass.

## Requirements

ClaudeWorkbench is a **two-process** app — a .NET Blazor host **plus a Node sidecar that runs the Claude Agent SDK** — so any machine that runs it needs *all* of:

| Requirement | Why | Notes |
|---|---|---|
| **.NET 10 SDK/runtime** | Blazor host + extracted engine + in-proc MCP server | `net10.0` |
| **Node.js** (LTS; tested on v24) | The sidecar runs the **Claude Agent SDK, which is Node-only** — there is no .NET Agent SDK | Runtime is small (~50–90 MB) and can be **bundled as a single-file executable (Node SEA)** — no system-wide install needed |
| **`claude` CLI** | The Agent SDK **spawns the `claude` binary** | **Bundled with the SDK** — the `@anthropic-ai/claude-agent-sdk-win32-x64` package ships/extracts the CLI; **no separate install**. This is most of the ~300 MB `sidecar/node_modules`. |
| **A Claude login** | Auth | A **subscription login** (cached in `~/.claude`) runs it for yourself with **no API key**; an `ANTHROPIC_API_KEY` is only needed to ship to *other* users |
| Ports **6100** (host) + **6110** (sidecar) | The two processes talk over localhost HTTP/SSE | configurable |

> **Deployment footprint:** the agent driver is the Node-based Agent SDK, which bundles the `claude` CLI. So a target needs the **.NET runtime + a Node runtime (small, bundleable) + the sidecar folder (`dist` + `node_modules` ≈ 300 MB, self-contained CLI included) + a Claude login**. There's no pure-.NET/no-Node build, but Node itself is light and can be shipped as a single binary. WinMerge is **not** required (review/merge is in-app).

### Sidecar setup

```powershell
cd sidecar
npm install      # restores @anthropic-ai/claude-agent-sdk + express
npx tsc          # builds dist/  (host launches it via `node dist/index.js`)
```

Ship `sidecar/dist` + `node_modules` (or run `npm ci` on the target).

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
- [x] `claude-sidecar` (Agent SDK): drives Claude, registers the `claude-workbench` MCP over HTTP, `canUseTool` operator gate (read-only auto-allow, mutations pause), neutral SSE event stream — end-to-end verified on the subscription (read-only turn)
- [x] Blazor host: workspace picker + runtime provisioning, Workbench/Source/Activity tabs, live transcript, operator gate dialog, in-app **merge review** (DiffPlex, terminal-only build/reindex)
- [x] Session continuity (`resume`) + New Thread; agent turn survives a host rebuild mid-turn
- [x] `AskUserQuestion` → operator **questions dialog** (tabs, choice cards, always-on "Other" free-text) via `canUseTool`
- [ ] **File upload** (next): pivot the sidecar to streaming input mode + content blocks; per-workspace `uploads` folder provisioned + granted via `additionalDirectories`; composer attach UI
- [ ] Bring in AIMonitor's `docs/claude-skills/` cards as-is (guidance is MCP-served today via `get_staging_guide`)

## Provenance

Lineage: [AIMonitor](https://github.com/rdavi10471a2/AIMonitor) (engine) + CodexAppServerDemo (Blazor control-surface pattern) → ClaudeWorkbench (Claude backend). The engine here is a faithful extraction; identical code compiles and the ported tests pass.
