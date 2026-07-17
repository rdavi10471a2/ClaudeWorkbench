# ClaudeWorkbench

Blazor operator console for governed, watched-source AI edits — driven by Claude via the Agent SDK.

## Lineage

- **AIMonitor** — the governed engine (indexing, edit sessions, staging, review gates, MCP tool surface). Extracted here, UI-agnostic, **no WinForms**.
- **CodexAppServerDemo** — the Blazor control-surface pattern and agent-driver shape (Codex is being replaced by Claude).
- **New** — a `claude-sidecar` (Claude Agent SDK) replacing the Codex JSON-RPC client, and sidecar-event logging replacing the man-in-the-middle MCP proxy.

## What is deliberately left behind from AIMonitor

- WinForms App, the MCP proxy hub, and the stdio bridge (the man-in-the-middle telemetry path).
- The pipe-based log mirror. The engine keeps a thin file sink (`IMonitorLogger` + `JsonLinesMonitorLogger`); MCP-call telemetry is re-emitted from Agent SDK `tool_use`/`tool_result` + hooks in the sidecar.

## Extraction (one testable layer at a time)

Engine dependency order (each layer builds + its ported tests pass before the next):

- [x] **Core** — settings, workspace paths, stable identifiers (7/7)
- [x] **Logging** — thinned: `IMonitorLogger` + `JsonLinesMonitorLogger` + in-proc `MonitorLogService`; pipe/proxy dropped (3/3)
- [x] **MSBuild** (6/6)
- [x] **Data** (27/28; 1 skipped — razor-generated env skew, see below)
- [x] **Workflow** (42/42; includes ClaudeSmokes over ported `samples/`)
- [x] **Runtime** (2/2)
- [x] **Indexing** (6/6)
- [x] **McpServer** — pipe-client logger swapped for the thin `JsonLinesMonitorLogger` sink; no proxy/bridge (builds)
- [x] **Cli** — engine-side console (kept as the runner for the non-unit/Integration suites; not in the ClaudeWorkbench runtime path)
- [ ] Blazor host + `claude-sidecar` (Agent SDK) + sidecar-event logging — see [docs/Architecture.md](docs/Architecture.md)

Engine builds with 0 errors, no WinForms/proxy/bridge. One test is `[Fact(Skip)]`: the
razor-generated-reference row assertion is environment-dependent (host Roslyn vs SDK Razor
generator); the `razor:*` code-behind path stays covered.

The non-unit smoke runners (`ToolSmokeTests`, `LanguageCorpusSmokeTests`, `SmokeTests`) are console
`Main` programs — they build but are run via the CLI/manually, not `dotnet test`.

Project/namespace names are kept as `AIMonitor.*` during extraction so the port stays mechanical and the ported tests prove fidelity (identical code compiles, identical tests pass). Rebranding, if wanted, is a later isolated pass.

## Build & test

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test ClaudeWorkbench.slnx
```
