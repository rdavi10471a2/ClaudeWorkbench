# Components

One page per module (C4 **Component** level). Each follows the same shape — Purpose, Key
types, How it works (+ Mermaid), key flows, Owns / Does Not Own, gotchas, where to start
reading, tests — and is written from the actual source.

## The engine (`AIMonitor.*`, extracted from AIMonitor)

| Module | Layer | One-liner |
|---|---|---|
| [AIMonitor.Core](AIMonitor.Core.md) | foundation | Settings, workspace paths, stable identifiers (the leaf) |
| [AIMonitor.Logging](AIMonitor.Logging.md) | foundation | JSON-lines log sink + in-proc `EntryWritten` event source |
| [AIMonitor.MSBuild](AIMonitor.MSBuild.md) | load | MSBuild/Roslyn open → document/project snapshots (Razor aware) |
| [AIMonitor.Data](AIMonitor.Data.md) | persistence | SQLite solution-index store, schema, queries |
| [AIMonitor.Indexing](AIMonitor.Indexing.md) | orchestration | Rebuild/refresh; post-accept refresh (scoped vs full) |
| [AIMonitor.Workflow](AIMonitor.Workflow.md) | **safety core** | Edit sessions, staging, the two gates, hash integrity |
| [AIMonitor.Runtime](AIMonitor.Runtime.md) | launch | GATE-1 launch; legacy WinMerge launcher (MCP/CLI only) |
| [AIMonitor.McpServer](AIMonitor.McpServer.md) | surface | The governed MCP tool surface (~60 tools) |
| [AIMonitor.Cli](AIMonitor.Cli.md) | tooling | Engine-side console runner (not in the app runtime path) |

## The app (two processes)

| Module | Process | One-liner |
|---|---|---|
| [ClaudeWorkbench.Host](ClaudeWorkbench.Host.md) | .NET :6100 | Blazor UI + MCP HTTP surface + sidecar supervisor + **the sole watched-source writer** |
| [Sidecar](Sidecar.md) | Node :6110 | Claude Agent SDK, MCP client, the **deny-by-default operator gate**, SSE events |

> Read order for a newcomer: **Host → Sidecar → Workflow**, then the rest. Start from the
> [docs index](../README.md#guided-reading-path).
