# ClaudeWorkbench

A Blazor operator console for **governed, watched-source AI edits**, driven by **Claude** through the Claude Agent SDK.

An agent proposes changes to a watched .NET solution. Every change is composed against a local *Working* candidate, staged, and held at a human **accept / reject** gate before it ever touches real source. The engine that enforces this — Roslyn indexing, edit sessions, staging, two compile gates, and an MCP tool surface — runs UI-agnostic behind a Blazor host and a Node sidecar.

**Status: working end-to-end.** The full governed loop (index → governed edit → stage → in-app review → operator accept writes source → post-accept build + reindex) is built and operator-verified, along with session continuity, an operator questions dialog (`AskUserQuestion`), file upload, context/usage meters, a model + reasoning selector, a Tasks board with an agent task-memory MCP loop (the board + MCP tools are live; the **board UI tab is currently disabled** pending the thread↔task workflow), and single-start supervision of the sidecar. Browser-visible end-to-end tests drive the real UI through the whole loop.

---

## The governed loop

```
choose workspace → index → refresh_file / new_file → governed edit
   → stage session → operator review → accept / reject → post-accept reindex
```

- **Reason in the cloud, edit locally.** The model reasons from compact context; watched-source changes are composed against explicit local Working candidates and promoted only through review.
- **The gate is code, not a prompt.** Mutations (file writes, staged-review accept) are intercepted by the sidecar's `canUseTool` / `PreToolUse` hook, surfaced to the Blazor UI, and applied only on operator approval.
- **Review is an in-app diff/merge.** The staged candidate vs. current watched source is rendered by [DiffPlex](https://github.com/mmanela/diffplex) in a resizable **Merge Review** dialog; the operator accepts or rejects there, and Accept is the only path that writes watched source. No external diff tool is involved.
- **The edit session is atomic.** Watched source is written once, on the terminal accept, after the whole session's combined overlay passes a real `dotnet build`; a single reject voids the session (see [ADR-0005](docs/decisions/0005-edit-session-is-atomic.md)).
- **Freshness is restored at accept.** The solution index rebuilds after an accepted decision (scoped where safe, full otherwise).

## Architecture

```
Blazor host (ClaudeWorkbench)  ── spawns ──►  claude-sidecar (Node, Claude Agent SDK)
   │  hosts the engine + MCP surface             │  registers the MCP surface, drives Claude,
   │  renders UI + live log                      │  streams tool/turn events back to the host
   └── AIMonitor.* engine
        Core · Logging · MSBuild · Data · Workflow · Indexing · McpServer
```

Full detail is in **[docs/architecture/Architecture.md](docs/architecture/Architecture.md)**. In brief:

- **MCP binding** — the sidecar is the MCP client; the Agent SDK connects to the engine's MCP server via its `mcpServers` option. The host serves MCP in-proc over Streamable HTTP at `http://localhost:6100/mcp`, advertising `serverInfo.name` `claude-workbench`. Tools appear to the agent as `mcp__claude-workbench__*`. `strictMcpConfig: true` exposes only this server — the machine's other MCP connectors do not leak in.
- **Validation** — each candidate write gets a fast syntax-only check; the authoritative semantic checks are real `dotnet build`s at plan-complete (pre-stage) and at the terminal accept (before any write). There is no in-memory overlay compile.
- **Agent workspace (deny-by-default)** — the agent's working directory is the watched solution's folder. Native tool access is limited to read-only `Read` / `Grep` / `Glob` (+ `ToolSearch` / `TodoWrite`); the `claude-workbench` MCP tools are allowed but mutations pause at the operator gate; everything else (`Bash`, `Write`/`Edit`, `WebFetch`, …) is denied. Native reads can be disabled per turn to force all access through the MCP surface.
- **Single-start + role card** — the host launches and supervises the sidecar as a child process, and orients the agent with an injected governed role card (SDK `systemPrompt`) so it knows the read-only + staging contract from turn one. Guidance is injected programmatically (`settingSources: []`; no `CLAUDE.md`).
- **Auth** — a Claude **subscription** login cached in `~/.claude` runs it for yourself with no API key; an `ANTHROPIC_API_KEY` is only needed to ship to other users. See [the guide](docs/guide/settings-and-usage.md#auth).
- **Logging** — the engine writes JSON-lines to a runtime log (and raises in-proc events for a live view); MCP telemetry is re-emitted from the sidecar's tool events, not sniffed off a pipe.

## Requirements

A two-process app — a .NET Blazor host plus a Node sidecar running the Claude Agent SDK:

| Requirement | Why | Notes |
|---|---|---|
| **.NET 10 SDK** | Blazor host + engine + in-proc MCP server; the **SDK** because indexing uses MSBuild/Roslyn | `net10.0` — the runtime ClaudeWorkbench runs on, not a constraint on the watched solution |
| **Node.js** (LTS; tested on v24) | The sidecar runs the Claude Agent SDK (Node-only) | Small runtime; bundleable as a single-file executable (Node SEA) |
| **`claude` CLI** | The Agent SDK spawns the `claude` binary | Ships inside the SDK package — no separate install |
| **A Claude login** | Auth | Subscription login (`~/.claude`) for yourself; `ANTHROPIC_API_KEY` only to ship to others |
| Ports **6100** (host) + **6110** (sidecar) | The two processes talk over localhost HTTP/SSE | Configurable |

The watched solution does **not** have to target `net10.0` — the index is built via `MSBuildWorkspace`, so it only needs the installed SDK to evaluate the project. Verified against `net10.0`, `net9.0(-windows)`, `net8.0`, a multi-TFM solution, and a legacy non-SDK-style `net472` project.

## Build & test

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test  ClaudeWorkbench.slnx
```

**227 tests — 227 pass · 0 skipped · 0 failed.** Grouped by what is covered rather than by project:

| Capability | Tests |
|---|---|
| Semantic index & language coverage (incl. the language corpus) | 68 |
| MCP tool surface (out-of-process, real JSON-RPC) | 50 |
| Edit workflow & staging | 34 |
| Host & infrastructure (git panel, settings, logging) | 25 |
| Review gates & decisions (incl. ADR-0005 session atomicity) | 23 |
| Sample-driven authoring over `samples/watched-solutions/` | 19 |
| Agent-loop end-to-end (real `dotnet build`s) | 8 |
| **Total** | **227** |

- **Everything runs under `dotnet test`** — no console runners, no flags to remember. Suite-by-suite detail: **[docs/guide/testing.md](docs/guide/testing.md)**.
- **A build-time dead-code gate** (`EnforceCodeStyleInBuild` + `.editorconfig`) reports unused members/params as warnings.

### Browser-visible end-to-end tests

`tests/e2e` drives the **real Blazor UI** through Playwright — a scripted agent-loop that types a prompt, submits, watches tool calls stream in, and accepts in Merge Review, optionally recording video/trace. It is self-gating (skips cleanly when no Host or browsers are present), so it never breaks the main suite. See **[tests/e2e/ClaudeWorkbench.E2E.Tests/README.md](tests/e2e/ClaudeWorkbench.E2E.Tests/README.md)**.

A recorded example — the multi-function-session prompt driven end to end through the real UI (edit → build → Merge Review → accept): **[docs/media/agent-loop-multi-function.mp4](docs/media/agent-loop-multi-function.mp4)**.

## Deploy — publish a live install

```powershell
.\scripts\publish-live.ps1            # -> C:\ClaudeWorkBenchLive + a Desktop shortcut
```

Publishes the Blazor host, the sidecar, and the Launcher side by side:

```
C:\ClaudeWorkBenchLive\
  host\       ClaudeWorkbench.Host.exe + config\
  sidecar\    dist\index.js + production node_modules
  launcher\   ClaudeWorkbench.Launcher.exe
  samples\    seed workspaces (CalculatorSample, MixedTfmSample)
  runtime\    one folder per workspace, created on first Start
```

The **Launcher** runs several watched solutions side by side — each with its own port, runtime, and index, held in one Windows Job Object so an instance's host + sidecar + browser start and die together. The install is location-independent, and re-running the script updates it **without touching `runtime\`** (workspaces and indexes survive). Full detail: **[docs/guide/deploying.md](docs/guide/deploying.md)**.

## Repository layout

```
src/
  AIMonitor.Core/        settings, workspace paths, stable identifiers
  AIMonitor.Logging/     JSON-lines sink + in-proc log service
  AIMonitor.MSBuild/     MSBuild/Roslyn project + document loading
  AIMonitor.Data/        SQLite solution index store
  AIMonitor.Workflow/    edit sessions, staging, review gates, pre-merge build
  AIMonitor.Indexing/    Roslyn semantic extraction → index; post-accept refresh
  AIMonitor.McpServer/   MCP tool surface (governed discovery + mutation + review)
  ClaudeWorkbench.Host/  in-proc ASP.NET host: the tool surface over HTTP (:6100) + Blazor UI
  ClaudeWorkbench.Launcher/  WinForms control panel: one process per workspace
sidecar/                 Node/TS Claude Agent SDK driver: operator gate, event stream to the host
scripts/                 publish-live.ps1
tests/
  unit/                  xUnit per-layer tests (incl. the language corpus)
  integration/           end-to-end over the MCP surface + engine (incl. the agent-loop suite)
  e2e/                   Playwright browser tests against the real UI
samples/watched-solutions/   fixtures the tests operate on
docs/                    architecture, one page per component, user guide, ADRs
```

## Documentation

Start at **[docs/README.md](docs/README.md)** for a guided reading path and a system diagram:
[Architecture](docs/architecture/Architecture.md) ·
[Components](docs/components/) (one page per module, with Mermaid diagrams) ·
[User Guide](docs/guide/) ·
[Decisions](docs/decisions/).

## Prior art & credits

ClaudeWorkbench is built on and informed by:

- **[AIMonitor](https://github.com/rdavi10471a2/AIMonitor)** — the governed engine (Roslyn indexing, the compile gates, session staging, post-accept freshness) originates here and runs UI-agnostic in this project.
- **CodexAppServerDemo** — the Blazor control-surface and agent-driver pattern.
- **[Claude Agent SDK](https://github.com/anthropics/claude-agent-sdk-typescript)** — drives Claude and registers the MCP surface in the sidecar.
- **[DiffPlex](https://github.com/mmanela/diffplex)** — the in-app merge-review diff. **[Radzen Blazor](https://blazor.radzen.com/)** — UI components. **[Microsoft.Playwright](https://playwright.dev/dotnet/)** — the browser-visible E2E tests. **SQLite** (`Microsoft.Data.Sqlite`) — the solution index store.

Related open-source servers also expose Roslyn/C# semantics to agents over MCP, for comparison:
**[roslyn-codelens-mcp](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp)** and
**[RoslynMCP](https://github.com/carquiza/RoslynMCP)**. ClaudeWorkbench overlaps on the Roslyn-over-MCP
idea but differs in intent: it is a **governed edit loop** — staged local candidates, a human
accept/reject merge gate, and post-accept reindex — with the semantic index as one part of that
workflow rather than the whole product.

## Roadmap

Done: engine + in-proc MCP endpoint (71-tool surface); the Claude sidecar with the operator gate and event stream; the Blazor host (workspace picker, tabs, live transcript, gate dialog, DiffPlex merge review); session continuity + New Thread; `AskUserQuestion` operator dialog; per-thread auto-approve + Stop; file upload; context/usage meters; model + reasoning selector; the Tasks board + agent task-memory MCP loop (board UI tab currently disabled, WIP); single-start with the injected role card; and the Playwright E2E harness.

Next:

- [ ] **Thread ↔ task workflow** — on New Thread, offer save-as-task / keep-as-discussion / discard; link threads to a task.
- [ ] **Injected skill-cards** — bring guidance in as SDK skill-cards (today it is MCP-served via `get_staging_guide` + the role card).
- [ ] **Governed reversible delete** — there is no delete tool by design; a governed, reversible one is the leading candidate feature.
