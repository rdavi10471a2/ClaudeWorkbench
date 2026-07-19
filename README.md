# ClaudeWorkbench

A Blazor operator console for **governed, watched-source AI edits**, driven by **Claude** through the Claude Agent SDK.

The agent proposes changes to a watched solution; every change is composed against a local *Working* candidate, staged, and held at a human **accept/reject** gate before it ever touches real source. The engine that enforces this — indexing, edit sessions, staging, review gates, and an MCP tool surface — is extracted from **AIMonitor** and runs UI-agnostic here, with **no WinForms**.

> Status: **working end-to-end.** Engine extracted + green; Blazor host + Claude sidecar live; the full governed loop (stage → in-app **DiffPlex** review/merge → operator accept writes source → post-accept build + reindex), **session continuity** (resume + New Thread), the agent's **AskUserQuestion → operator questions dialog**, **file upload**, **context/usage meters**, a **model + reasoning-level selector**, a **Tasks kanban board** with an agent **task-memory** MCP loop (`get_current_task` / `update_agent_notes`), and **single-start** (the host launches + supervises the sidecar) with an injected **governed role card** are all built and operator-verified on the subscription. See [Roadmap](#roadmap).

---

## Documentation

Full docs live in **[`docs/`](docs/)** — start at **[docs/README.md](docs/README.md)** for a
guided reading path and a system diagram. Highlights:
[Architecture](docs/architecture/Architecture.md) ·
[Components](docs/components/) (one page per module, with Mermaid diagrams) ·
[User Guide](docs/guide/) ·
[Decisions](docs/decisions/).

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
- **Review is an in-app diff/merge — [DiffPlex](https://github.com/mmanela/diffplex).** The staged candidate vs. current watched source is rendered by DiffPlex in a resizable **Merge Review** dialog (`Components/Dialogs/MergeReviewDialog.razor`); the operator accepts/rejects there and the Accept writes watched source. There is **no external diff tool** (no WinMerge) in the path.
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
**[docs/architecture/Architecture.md](docs/architecture/Architecture.md)** (or the guided **[docs/](docs/)** index). Short version:

- **MCP binding** — the sidecar is the MCP client; the Agent SDK connects to the engine's MCP server via its `mcpServers` option (recommended: the Blazor host serves MCP in-proc over HTTP; the sidecar registers a URL). Tools appear to the agent as `mcp__claude-workbench__*` (the HTTP host advertises `serverInfo.name` `claude-workbench` on port `6100`, distinct from the real `ai-monitor`). No proxy or bridge in the path.
- **Auth** — **subscription verified for personal use** (see [the guide](docs/guide/settings-and-usage.md#auth)). A full turn ran headless with no `ANTHROPIC_API_KEY`: the SDK inherits the local `claude` CLI's cached subscription login. `ANTHROPIC_API_KEY` (API billing) is only needed to ship to other people, not to run this yourself.
- **Logging** — the engine logs to a JSON-lines file (and raises in-proc events for a live view); MCP-call telemetry is re-emitted from the sidecar's `tool_use`/`tool_result` events and hooks, not sniffed off a pipe.
- **Single-start + role card** — the host **launches and supervises the sidecar** as a child process (`SidecarProcessHost`; skips if a sidecar is already on the port, kills it on shutdown; override via `Sidecar:AutoStart` / `Sidecar:NodeExecutable` / `Sidecar:Directory`). The agent is oriented by an injected **governed role card** (SDK `systemPrompt`, `preset: claude_code` + append) so it knows the read-only + staging contract from turn one instead of discovering it by hitting a deny. There is **no `CLAUDE.md`** (isolation via `settingSources: []`); all guidance is injected programmatically.
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
docs/                    developer + user docs — start at docs/README.md
  architecture/            C4 architecture, the governed loop, the two gates
  components/              one page per module (C4 component level, Mermaid)
  guide/                   user help (getting-started, merge-review, git-panel, …)
  decisions/               ADRs (why the gate is code, two-process, argv git, …)
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
- [x] Blazor host: workspace picker + runtime provisioning, Tasks/Workbench/Source/Activity tabs, live transcript, operator gate dialog, in-app **merge review** (DiffPlex, terminal-only build/reindex)
- [x] Session continuity (`resume`) + New Thread; agent turn survives a host rebuild mid-turn
- [x] `AskUserQuestion` → operator **questions dialog** (tabs, choice cards, always-on "Other" free-text) via `canUseTool`
- [x] Auto-approve toggle (per-thread) + Stop button
- [x] **File upload** — streaming input mode; per-workspace `uploads` folder provisioned + granted via `additionalDirectories`; composer attach/drop UI (text, code, and images/PDFs via the agent's Read)
- [x] **Context/usage meters** — dropdown off the SDK `Query` handle (`getContextUsage` + experimental `usage`): context fill + auto-compact headroom, weekly/5-hour utilization, plan
- [x] **Model + reasoning-level selector** — settings-dialog dropdowns → per-thread `model` + `effort` on the sidecar query options
- [x] **Tasks kanban board** — ported from CodexAppServerDemo (Radzen; right-click state moves; single-Active invariant) as the first tab, over the existing `board.sqlite`
- [x] **Task MCP loop** — `get_current_task` (task + user/agent notes content), `list_tasks`, `update_agent_notes` (agent task-memory to `planning/task-memory`, never watched source)
- [x] **Single-start** — host launches + supervises the sidecar; injected **governed role card** as `systemPrompt` so the agent knows its read-only + staging role from turn one
- [ ] **Thread ↔ task workflow** (design pending): on New Thread, prompt *save-as-task / keep-as-discussion / discard*; auto-name threads `discussion-<datetime>`; add a `task_id` link so a task groups its threads; thread-provenance on agent notes
- [ ] Bring in AIMonitor's `docs/claude-skills/` cards as injected skill-cards (guidance is MCP-served today via `get_staging_guide` + the role card)

## Related projects

Other open-source servers also expose Roslyn / C# semantics to AI agents over MCP — worth a look for comparison:

- **[Roslyn CodeLens](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp)** (`MarcelRoozekrans/roslyn-codelens-mcp`) — a Roslyn-based MCP server providing semantic code intelligence for .NET codebases (type hierarchies, call sites, DI registrations, reflection usage) for Claude Code.
- **[RoslynMCP](https://github.com/carquiza/RoslynMCP)** (`carquiza/RoslynMCP`) — an MCP server providing C# code-analysis capabilities (wildcard symbol search, reference tracking, dependency and complexity analysis) using Microsoft Roslyn.

ClaudeWorkbench overlaps on the Roslyn-over-MCP idea but differs in intent: it is not only read/analysis but a **governed edit loop** — staged local *Working* candidates, a human accept/reject **merge gate** (DiffPlex), and post-accept reindex — with the Roslyn semantic index as one part of that workflow rather than the whole product.

## Provenance

Lineage: [AIMonitor](https://github.com/rdavi10471a2/AIMonitor) (engine) + CodexAppServerDemo (Blazor control-surface pattern) → ClaudeWorkbench (Claude backend). The engine here is a faithful extraction; identical code compiles and the ported tests pass.
