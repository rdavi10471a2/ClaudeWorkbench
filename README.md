# ClaudeWorkbench

A Blazor operator console for **governed, watched-source AI edits**, driven by **Claude** through the Claude Agent SDK.

An agent proposes changes to a watched .NET solution. Every change is composed against a local *Working* candidate, staged, and held at a human **accept / reject** gate before it ever touches real source. The engine that enforces this — Roslyn indexing, edit sessions, staging, two compile gates, and an MCP tool surface — runs UI-agnostic behind a Blazor host and a Node sidecar.

**Status: working end-to-end.** The full governed loop (index → governed edit → stage → in-app review → operator accept writes source → post-accept build + reindex) is built and operator-verified, along with session continuity, an operator questions dialog (`AskUserQuestion`), file upload, context/usage meters, a model + reasoning selector, named/resumable **conversations** (autosaved per session, with a Conversations modal to resume/rename/delete; transcripts mirrored into an app-owned runtime copy), an agent notes scratchpad (`write_note`), a read-only **Source** browser (Solution + Files trees, an in-app Monaco viewer with markdown rendering, in-app Build/Run to real output, a NuGet package manager, and an elevated Admin shell), a per-solution **workspace config** editor (`.claudeworkbench.json` + the solution's `.gitignore`), and single-start supervision of the sidecar. Merge Review defaults to a Monaco diff (change-navigation, word-level) with the classic DiffPlex view still available; the chat renders Markdown, images, and Mermaid, with per-code-block copy; and the launcher ships in both a WPF (primary) and WinForms (fallback) UI. Browser-visible end-to-end tests drive the real UI through the whole loop.

---

## The governed loop

```
choose workspace → index → refresh_file / new_file → governed edit
   → stage session → operator review → accept / reject → post-accept reindex
```

- **You work in conversations.** The working unit is a named, resumable **conversation** with Claude — it autosaves, you name/reopen/delete it from the Conversations list, and it records the exact edits it produced. (It replaced the old Tasks board; see [docs/guide/conversations.md](docs/guide/conversations.md).)
- **Reason in the cloud, edit locally.** The model reasons from compact context; watched-source changes are composed against explicit local Working candidates and promoted only through review.
- **The gate is code, not a prompt.** Mutations (file writes, staged-review accept) are intercepted by the sidecar's `canUseTool` / `PreToolUse` hook, surfaced to the Blazor UI, and applied only on operator approval.
- **Review is an in-app diff/merge.** The staged candidate vs. current watched source is rendered in a resizable **Merge Review** dialog — by default an in-app **[Monaco](https://microsoft.github.io/monaco-editor/)** diff editor (F7 / Shift+F7 change navigation, an overview ruler, word-level diff, syntax highlighting), with the original **[DiffPlex](https://github.com/mmanela/diffplex)** side-by-side kept as a switchable option (a **Diff viewer** setting, plus proposed-on-left orientation and a reverse-colors toggle). The operator accepts or rejects there, and Accept is the only path that writes watched source. No external diff tool is involved.
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

### Browser-visible end-to-end tests — a tested, verified capability kept on the shelf

`tests/e2e` drives the **real Blazor UI** through Playwright — a scripted agent-loop that types a prompt, submits, watches tool calls stream in, and accepts in Merge Review, optionally recording video/trace. See **[tests/e2e/ClaudeWorkbench.E2E.Tests/README.md](tests/e2e/ClaudeWorkbench.E2E.Tests/README.md)**.

This is a **proven, verified capability — deliberately not part of the default build/test.** It is *not* referenced by `ClaudeWorkbench.slnx`, so `dotnet build/test ClaudeWorkbench.slnx` doesn't touch it. Driving a **real** agent against the live UI costs real tokens and wall-clock, can't be fully deterministic, and the Host is single-operator (a run can even contend with your own use), so it is **too expensive to run in general**. The value is having *proven agent-in-the-middle E2E works* and keeping it ready — not running it every change.

It stays in the repo and is **one line from live**: `dotnet sln add tests/e2e/ClaudeWorkbench.E2E.Tests` folds it back into the solution, or build/run it standalone with `dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests` (it has no project references — it drives the app through the browser, so it builds on its own). The tests are self-gating anyway (`[SkippableFact]` + an opt-in `LiveEnabled` switch): even when present they skip cleanly unless a Host is up and you opt in, so they never redden the main suite. The one maintenance cost of shelving it: `data-testid` selectors can drift on a UI refactor without anything flagging it — but a re-add then fails **loud and obvious** ("selector not found"), never silently wrong.

Requirements beyond `dotnet test`: a **one-time `playwright install`** for the browser binaries (Chromium etc., in `%LOCALAPPDATA%\ms-playwright`), a running Host, and — for the live driver — a Claude sign-in. Only a **minimal set of `data-testid` hooks** exists today (the composer → transcript → merge-review path); broader UI coverage (Source viewer, Git panel, Settings, the questions dialog, …) will need more hooks added per scenario. The **Conversations** modal is instrumented (`open-conversations`, `threads-tab`, `thread-row`, resume/rename/delete hooks) and covered by a smoke test plus the live driver.

A recorded example — the multi-function-session prompt driven end to end through the real UI (edit → build → Merge Review → accept): **[docs/media/agent-loop-multi-function.mp4](docs/media/agent-loop-multi-function.mp4)**.

The live driver runs the **real** agent, so it isn't fully deterministic — it can raise an unscripted prompt (e.g. an `AskUserQuestion` elicitation) and pause waiting on you. Monitor a live run rather than leaving it unattended.

## Deploy — publish a live install

```powershell
.\scripts\publish-live.ps1            # -> C:\ClaudeWorkBenchLive + a Desktop shortcut
```

Publishes the Blazor host, the sidecar, and the Launcher side by side:

```
C:\ClaudeWorkBenchLive\
  host\       ClaudeWorkbench.Host.exe + config\
  sidecar\    dist\index.js + production node_modules
  launcher\
    wpf\      ClaudeWorkbench.Launcher.Wpf.exe   (primary)
    winforms\ ClaudeWorkbench.Launcher.exe       (fallback)
  samples\    seed workspaces (CalculatorSample, MixedTfmSample, BlazorSample)
  runtime\    one folder per workspace, created on first Start
```

Both launcher UIs publish side by side into their own subfolders and share the same `launcher.json` + `runtime\` in the install root, so either exe drives the same workspaces — only the window you open differs. The publish creates a desktop shortcut for each.

Re-running the script updates an install **without touching `runtime\`** (workspaces and indexes survive).

### The Launcher

The Launcher is a small control panel — the normal way to start the app. It runs **several watched solutions side by side, one window per solution, each fully isolated** (its own port, runtime, and index). It ships in **two interchangeable UIs**: **`ClaudeWorkbench.Launcher.Wpf`** (the primary, a modernized WPF rewrite with DPI-correct layout) and the original **`ClaudeWorkbench.Launcher`** (WinForms, kept as a fallback). Both read the same `launcher.json` and drive the same workspaces — pick either shortcut.

- **Workspaces** — **Add workspace** (pick a `.sln`/`.slnx`; it gets a free port and an isolated runtime), **New blank solution** (pick/create an empty folder → writes an empty `.slnx` named after it and registers it, so you can start greenfield from nothing and fill it from the Source tab), **Start** (launches that workspace's host + sidecar and opens a browser window), **Stop**, **Remove**, and **Settings** (host exe path, sidecar folder, runtime folder, browser choice).
- **Sign in (Claude / GitHub)** — buttons that open a terminal on each CLI's own login flow (Sign in / Check status / Sign out). Sign-in is **machine-wide, not per-workspace**: Claude caches under `~/.claude` (the sidecar inherits it) and `gh` under the user profile (the Git panel uses it), so it's once per machine until the credential expires. Force a fresh login by signing out first.
- **Lifecycle — kill one, kill all.** Closing the browser window stops that instance's backend on its own; **Stop** (or closing the Launcher) terminates that instance's host + sidecar + browser together; and a Launcher crash can't orphan a backend, because every instance is held in a **Windows Job Object** that dies with the Launcher.
- **Isolation & placement.** Each instance provisions into `<workbench>\runtime\<workspace>` — its index, config, and `host.log` live there; the folder is claimed on first Start and kept, so renaming a workspace never strands its index. The Launcher exe can live anywhere (shortcut, Release build, publish folder); it finds the workbench from the host exe in Settings, so instances land next to the code they watch. Browser choice: Chrome/Edge open a clean `--app` window that closes as a unit; a custom Chromium path is supported; the default browser just opens a tab.

Full detail: **[docs/guide/deploying.md](docs/guide/deploying.md)**.

### The Source tab

A read-only browser of the watched solution, with two trees in sidebar sub-tabs:

- **Solution** — projects → files → symbols, built from the in-process index (the code model). Types and members are navigable; click a symbol to jump to its line.
- **Files** — a plain file browser fed by `git ls-files --cached --others --exclude-standard` in the watched folder: tracked **plus** new-but-not-ignored files (so a just-created file shows up before it's committed), minus `.gitignore`'d junk (`bin/obj`, generated output, `.git`) — no hand-maintained ignore list. It's index-independent, so it works even before the first index build, and surfaces the non-code files the index never had (README, docs, scripts, `.slnx`, decision docs).

The **Files** tree also has a filesystem fallback: when the watched folder isn't a git repo (or git is unavailable), it falls back to a pruned recursive walk — skipping `bin`/`obj`/`.git`/`node_modules` and other noise (extendable per-solution via `.claudeworkbench.json`) — so the tree still works before the first commit or without git.

Both trees drive one persistent in-app **Monaco** viewer (vendored locally, not an iframe), model-swapped per file. **Markdown** files render as formatted HTML (via the same `MarkdownRenderer` the chat uses) with a **Rendered / Raw** toggle — Raw drops back to Monaco source. The top toolbar adds operator **Build** / **Run** (Debug/Release, with a startup-project picker) that produce real `bin/<config>` output, **Refresh** (re-read source) / **Rebuild Index**, **Packages** (a NuGet manager — browse/install/update/uninstall by project or solution, driving the SDK's `dotnet package` commands out-of-process), **Admin shell** (opens an elevated command window rooted at the solution folder, via UAC — for hand-running dotnet/git or killing a stuck process), and **Add project** (below). These are all **operator** actions run host-side — they are *not* part of the agent's tool surface. Full detail: **[docs/guide/source-tab.md](docs/guide/source-tab.md)**.

### Creating projects

Greenfield-to-running without leaving the app: the Launcher's **New blank solution** writes an empty
`.slnx` you can Start, and the **Source** tab's **Add project** popup fills it. Add-project enumerates
the installed SDK's templates (`dotnet new list`) and target frameworks (`dotnet --list-sdks`) at open
time — so the dropdowns show exactly what this machine can create — then scaffolds with `dotnet new`,
registers the project in the `.slnx` (`dotnet sln add`), restores, and reindexes. **C# only**, and new
projects must live inside the solution folder (containment). This is an **operator** action run
host-side out-of-process — it is *not* part of the agent's tool surface; the agent never runs the SDK.

**Requires the `dotnet` CLI on `PATH`** (the same **.NET 10 SDK** the app already needs to run and
index). By design this feature invokes the SDK's own tooling (`dotnet new` / `dotnet sln` /
`dotnet restore`) as subprocesses rather than embedding the templating libraries — so it uses each
machine's real, version-matched engine and *every* installed template for free. Any machine with
Visual Studio or VS Code (or a bare SDK install) already has this on `PATH`; nothing extra to install.
Because it shells the SDK, a template that needs a framework/workload you don't have will fail with the
SDK's own message — install it and retry.

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
  ClaudeWorkbench.Launcher.Wpf/  WPF control panel (primary): one process per workspace
  ClaudeWorkbench.Launcher/      WinForms control panel (fallback)
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
- **[Monaco](https://microsoft.github.io/monaco-editor/)** — the default in-app diff (and the read-only source viewer), vendored locally. **[DiffPlex](https://github.com/mmanela/diffplex)** — the classic side-by-side merge-review diff, kept as an option. **[Radzen Blazor](https://blazor.radzen.com/)** — UI components. **[Microsoft.Playwright](https://playwright.dev/dotnet/)** — the browser-visible E2E tests. **SQLite** (`Microsoft.Data.Sqlite`) — the solution index store.

Related open-source servers also expose Roslyn/C# semantics to agents over MCP, for comparison:
**[roslyn-codelens-mcp](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp)** and
**[RoslynMCP](https://github.com/carquiza/RoslynMCP)**. ClaudeWorkbench overlaps on the Roslyn-over-MCP
idea but differs in intent: it is a **governed edit loop** — staged local candidates, a human
accept/reject merge gate, and post-accept reindex — with the semantic index as one part of that
workflow rather than the whole product.

## Roadmap

Done: engine + in-proc MCP endpoint (74-tool surface — 70 engine + 4 git); the Claude sidecar with the operator gate and event stream; the Blazor host (workspace picker, tabs, live transcript, gate dialog, Monaco/DiffPlex merge review); named, resumable **Conversations** that replaced the retired Tasks board (autosaved per session; a Conversations modal to resume/rename/delete; Current + Archived; transcripts mirrored into an app-owned runtime copy; all host-side — the agent needs no tools or awareness of it); **greenfield project creation** (Launcher *New blank solution* + Source-tab *Add project*, SDK-template-driven, operator-run, C# only); the **Source** browser (Solution + Files sub-tabs, an in-app Monaco viewer with markdown Rendered/Raw rendering, and in-app Build/Run to real `bin/<config>` output with a startup-project picker); `AskUserQuestion` operator dialog; per-conversation auto-approve + Stop; file upload; context/usage meters; model + reasoning selector; NuGet restore (`restore_solution` + auto-restore); the agent notes scratchpad (`write_note` MCP tool, path-confined to `runtime\<workspace>\agent-notes`, outside watched source); a **NuGet package manager** and an elevated **Admin shell** on the Source tab; a per-solution **workspace config** editor (`.claudeworkbench.json` + `.gitignore`); a **Monaco** Merge Review diff (default; change-nav + word-level; DiffPlex still selectable) with orientation/color settings; per-code-block **copy** with CRLF-normalized clipboard; auto-approve on by default; failed turns surfaced in the transcript; the **WPF** launcher rewrite shipped alongside WinForms; single-start with the injected role card; and the Playwright E2E harness.

Next:

- [ ] **Injected skill-cards** — bring guidance in as SDK skill-cards (today it is MCP-served via `get_staging_guide` + the role card).
- [ ] **Governed reversible delete** — there is no delete tool by design; a governed, reversible one is the leading candidate feature.
