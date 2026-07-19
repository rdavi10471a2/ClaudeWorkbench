# ClaudeWorkbench.Host

> The single .NET process that fuses the AIMonitor engine, the claude-workbench MCP tool surface, the Blazor operator console, and a supervised Node sidecar into one governed workbench — and is the only code that ever writes watched source.

**Project:** `src/ClaudeWorkbench.Host/ClaudeWorkbench.Host.csproj` (`Microsoft.NET.Sdk.Web`, `net10.0`) · **Depends on:** every engine project — `AIMonitor.Core`, `AIMonitor.Logging`, `AIMonitor.Data`, `AIMonitor.MSBuild`, `AIMonitor.Workflow`, `AIMonitor.Indexing`, `AIMonitor.Runtime`, `AIMonitor.McpServer` (plus `ModelContextProtocol.AspNetCore`, `Radzen.Blazor`, `DiffPlex`, `Markdig`, `Microsoft.Data.Sqlite`) · **Serves:** MCP over Streamable HTTP at `/mcp`, the Blazor UI, and a `/health` probe; launches and supervises the sidecar.

## Purpose

ClaudeWorkbench.Host is the runtime that turns the extracted AIMonitor engine into an operator-driven, human-gated workbench for AI edits to a watched .NET solution. It runs as a **single start** (`dotnet run` / the published exe) on port `:6100` and does four jobs in one process, sharing one engine instance and one logging sink in-process:

1. hosts the AIMonitor engine (via `WorkspaceManager` + the `AIMonitor.*` project references);
2. exposes the `claude-workbench` MCP tool surface over HTTP for the sidecar's agent to call;
3. renders the Blazor operator console (Radzen) the human uses to prompt, gate, review, and merge;
4. launches and supervises the Node sidecar as a child process.

The load-bearing invariant: the agent **never** writes watched source. It stages candidates through the governed MCP; the operator reviews the diff and accepts; `EngineReviewWorkflow.Accept` — an operator-authorized Blazor action — performs the only write into the watched tree.

## What it hosts (three surfaces in one process)

- **MCP HTTP surface** — `app.MapMcp("/mcp")`. An `AddMcpServer(...).WithHttpTransport()` server named `claude-workbench` v0.1.0, composed from three tool types: `WithTools<AIMonitorTools>()` (the engine's edit/index/search surface, from `AIMonitor.McpServer`), `WithTools<Tasks.TaskMcpTools>()` (task board read + agent-notes write), and `WithTools<GitMcpTools>()` (governed git). Together these are the **71-tool surface** the sidecar's Claude Agent SDK query consumes = `AIMonitorTools` (60) + `TaskMcpTools` (3) + `GitMcpTools` (8). (Verified against a live `tools/list`.)
- **Blazor operator console** — `app.MapRazorComponents<App>().AddInteractiveServerRenderMode()`. Radzen + interactive Server components; the `Home` page hosts the tab shell (Tasks / Workbench / Source / Git / Activity).
- **Sidecar supervisor** — `SidecarProcessHost` (an `IHostedService`) launches `node dist/index.js` as a child process, passing `SIDECAR_PORT` and `WORKBENCH_MCP_URL=http://localhost:6100/mcp` so the sidecar loops back to this process's MCP surface.

## Service graph

Wiring lives entirely in `Program.cs`. `MonitorSettingsLoader.Load` produces the `MonitorSettings` that seeds the `WorkspaceManager` (the shared engine handle) and the `JsonLinesMonitorLogger` sink. From there the DI container assembles the MCP server, the sidecar trio (process host + event stream + control client) behind the `IOperatorConsole` / `IApprovalQueue` seams, the review workflow, the git pair, and the task/source/upload/provisioner services.

```mermaid
flowchart TD
    settings["MonitorSettings\n(MonitorSettingsLoader.Load)"]
    wm["WorkspaceManager\n(singleton, shared engine handle)"]
    logger["IMonitorLogger\n(JsonLinesMonitorLogger)"]
    settings --> wm
    settings --> logger

    subgraph mcp["MCP server (/mcp)"]
        aim["AIMonitorTools"]
        task["TaskMcpTools"]
        gitmcp["GitMcpTools"]
    end
    wm --> aim

    subgraph sidecar["Sidecar seam"]
        sph["SidecarProcessHost\n(IHostedService)"]
        ses["SidecarEventStream\n(BackgroundService, SSE)"]
        sc["SidecarClient\n(typed HttpClient)"]
        soc["SidecarOperatorConsole\n(IOperatorConsole + IApprovalQueue)"]
    end
    ses --> soc
    sc --> soc
    agentcfg["AgentSettingsService"] --> soc
    wm --> soc

    erw["EngineReviewWorkflow\n(IReviewWorkflow)"]
    wm --> erw
    logger --> erw

    subgraph git["Git seam"]
        gsvc["GitService\n(argv git CLI)"]
        gws["GitWorkspaceService\n(bound to watched repo)"]
    end
    gsvc --> gws
    wm --> gws
    gws --> gitmcp

    subgraph tasks["Task board"]
        tf["TaskBoardRepositoryFactory"]
        repo["WorkflowTaskBoardRepository\n(board.sqlite)"]
        tvs["WorkflowTaskBoardViewService"]
    end
    tf --> repo
    tf --> task
    tf --> tvs

    src["Source.SourceWorkspace"]
    wm --> src
    prov["RuntimeProvisioner"] --> wm
    coord["WorkspaceCoordinator"] --> prov
    upl["UploadService"] --> wm
    dir["DirectoryBrowserService"]

    ui["Blazor operator console\n(Home + tabs)"]
    soc --> ui
    erw --> ui
    gws --> ui
    tvs --> ui
    src --> ui
```

## Key services

| Service | File | Role |
|---|---|---|
| `WorkspaceManager` | (engine, `AIMonitor.McpServer`) | The shared, singleton engine handle. Holds `Settings`, `EditService`, `RepositoryRoot`, `WatchedSolutionPath`, `HasWorkspace`; every host service that touches the watched solution goes through it. |
| `EngineReviewWorkflow` | `Services/EngineReviewWorkflow.cs` | `IReviewWorkflow` (singleton). In-process adapter over the AIMonitor workflow engine that replaces the external WinMerge step. **The only place watched source is written** (`File.Copy` into the watched tree on operator Accept). |
| `SidecarProcessHost` | `Services/SidecarProcessHost.cs` | `IHostedService` that launches + supervises `node dist/index.js`; skips launch if the port is already open; kills the child tree on shutdown. |
| `SidecarEventStream` | `Services/SidecarEventStream.cs` | `BackgroundService` that reads the sidecar's SSE `/events`, maintaining bounded event history (max 500), the pending-gate set, elicitations, connection + active-turn state; raises `Changed`. Reconnects every 2 s on drop; re-seeds gates/elicitations on each connect. |
| `SidecarClient` | `Services/SidecarClient.cs` | Typed `HttpClient` for the sidecar's control surface: `/prompt`, `/gates/{id}`, `/elicitations/{id}`, `/stop`, `/new-thread`, `/review-outcome`, `/usage`. |
| `SidecarOperatorConsole` (+ `.Approvals`) | `Services/SidecarOperatorConsole*.cs` | Scoped adapter implementing both `IOperatorConsole` (transcript/activity/status, SendAsync, StopAsync, NewThread, usage) and `IApprovalQueue` (pending gates → `ApprovalRequest`, elicitations, resolve). The one place aware of sidecar event shapes. |
| `GitService` | `Services/GitService.cs` | Stateless wrapper that launches the `git` executable via `ProcessStartInfo.ArgumentList` (argv, `UseShellExecute=false`) — **no shell, no injection surface**. Never throws on non-zero exit; caller inspects `GitResult.Ok`. |
| `GitWorkspaceService` | `Services/GitWorkspaceService.cs` | Singleton binding `GitService` to the current watched repo (resolves the repo root so porcelain paths line up). Shared by both the operator Git panel and the agent's `GitMcpTools`. |
| `TaskMcpTools` | `Tasks/TaskMcpTools.cs` | MCP task surface: agent reads the Active task + notes and writes back agent-notes (durable memory) into the runtime task-memory store — never watched source. |
| `WorkflowTaskBoardRepository` | `Tasks/WorkflowTaskBoardRepository.cs` | SQLite-backed (`board.sqlite`) task board + task-memory markdown store; created per-workspace via `TaskBoardRepositoryFactory`. |
| `Source.SourceWorkspace` | `Source/SourceWorkspace.cs` | Builds the source-browser snapshot from the in-process AIMonitor index and rebuilds it; retargets when the watched workspace changes. |
| `WorkspaceCoordinator` / `RuntimeProvisioner` | `Services/*.cs` | Selecting a watched solution: point the manager at it, persist the choice, provision its runtime skeleton + task DB (idempotent, also on startup). Persistence targets the registered `MonitorConfigPath` — **the same file the host was started with** (`--config`), not a default path, so reader and writer cannot drift. |
| `AgentSettingsService` / `UploadService` / `DirectoryBrowserService` | `Services/*.cs` | Operator tool-policy (persisted `AgentToolPolicy`), file attachments into the runtime `uploads/` folder, and filesystem navigation for the workspace picker (opening at the watched solution's folder, or the user profile on first run — never the process cwd, which under the Launcher is the install folder). |
| `BrowserPresenceTracker` / `BrowserLifetimeCircuitHandler` | `Services/*.cs` | Opt-in browser-owned lifetime (`CWB_EXIT_WITH_BROWSER=1`, set by the Launcher): the circuit handler tracks live Blazor circuits and the tracker shuts the host down shortly after the last tab closes — so closing an instance's window stops its backend from the browser side. |

## The two critical flows

### (a) Operator Accept writes watched source

`EngineReviewWorkflow.Accept` validates, then copies the staged bytes into the watched tree, then records the decision (which runs the terminal build + index rebuild). Note honestly the **write-before-validate ordering**: `File.Copy(record.StagedFilePath, record.WatchedFilePath, overwrite: true)` executes *before* `StagedDecisionWorkflow.Record` runs GATE 2's authoritative build. The pre-accept guard is only GATE 1 (a fast staged-overlay readiness check, no `dotnet build`); if the full build then fails, the watched file has already been overwritten. A rejected/failed build does not roll the file back.

```mermaid
sequenceDiagram
    participant Op as Operator (MergeReviewDialog)
    participant ERW as EngineReviewWorkflow
    participant Val as PreMergeValidationService (GATE 1)
    participant FS as Watched file
    participant SDW as StagedDecisionWorkflow (GATE 2)
    Op->>ERW: Accept(stagedRecordId, forceApproveValidation)
    ERW->>Val: EnsureValidatedAndLaunched (staged-overlay check, no build)
    Val-->>ERW: PreMergeValidationResult
    Note over ERW: if error & not force-approved → return, no write
    ERW->>FS: File.Copy(staged → watched, overwrite:true)
    Note right of FS: WRITE happens here — before GATE 2 build
    ERW->>SDW: Record("accepted", ...) → terminal build + index rebuild
    SDW-->>ERW: build + index outcome
    ERW-->>Op: ReviewActionResult (message + outcome summary)
```

### (b) Sidecar SSE → Blazor UI

```mermaid
sequenceDiagram
    participant Side as Node sidecar
    participant SES as SidecarEventStream (BackgroundService)
    participant SOC as SidecarOperatorConsole
    participant UI as Blazor tabs (Workbench / Activity)
    Side-->>SES: SSE line "data: {SidecarEvent}" on /events
    SES->>SES: Apply(evt) → update events / gates / elicitations / activeTurn
    SES-->>SOC: Changed
    SOC-->>UI: Changed (relayed) → StateHasChanged
    Note over UI: gate_request → AgentActionModal (approve/deny)
    Note over UI: elicitation_request → question dialog
    UI->>SOC: ResolveApprovalAsync(gateId, allow/deny)
    SOC->>Side: POST /gates/{gateId} (via SidecarClient)
```

## UI tabs

Hosted on `Components/Pages/Home.razor`:

- **Tasks** (`Tabs/Tasks/*`) — the task board the operator curates; sets which task is Active, its state and notes. Backed by `WorkflowTaskBoardViewService` over `board.sqlite`.
- **Workbench / Assistant** (`Tabs/AssistantTab`) — the chat surface: prompt the agent, watch the transcript, resolve gates/elicitations via `AgentActionModal`, start a new thread.
- **Source** (`Pages/Source/*`) — read-only browser of the watched solution from the in-process AIMonitor index (`SourceWorkspace`).
- **Git** (`Tabs/GitTab`) — the operator's git panel over `GitWorkspaceService`: status, stage/unstage/discard, diff, commit, push, branches.
- **Activity** (`Tabs/ActivityTab`) — the reverse-chronological event feed derived from `SidecarEventStream` snapshots.

## Git integration

Two consumers, one backing service. `GitService` is a stateless CLI wrapper that spawns `git` via `ProcessStartInfo.ArgumentList` (argv only, `UseShellExecute=false`, `CreateNoWindow=true`) — there is no shell and thus no injection surface. `GitWorkspaceService` (singleton) binds it to the currently watched solution, resolving the repo root so porcelain paths and diffs align even when the `.sln` sits in a subfolder. Both the operator **Git panel** and the agent's **`GitMcpTools`** share that one singleton.

`GitMcpTools` is the governed surface for the agent: reads (`git_status`, `git_diff`, `git_log`, `git_list_branches`) auto-allow at the sidecar gate, while the four mutations — `git_commit`, `git_push`, `git_create_branch`, `git_switch_branch` — are marked **GOVERNED** and pause at the operator's approval gate before running, so nothing outward or irreversible happens without the operator's OK. The shared `Components/Shared/DiffView` renders side-by-side diffs for both the Git panel and the `MergeReviewDialog`; `GitWorkspaceService.GetDiffContentAsync` normalizes both sides to `\n` so line-ending differences don't render as spurious changes.

## Owns / Does Not Own

**Owns:** the *only* write to watched source — `EngineReviewWorkflow.Accept`'s `File.Copy` into the watched tree, gated behind an operator Blazor action. Owns the MCP HTTP surface composition, the sidecar lifecycle, the Blazor console, and the git seam.

**Does not own:** engine internals. `WorkspaceManager`, `EditService`, `StagedDecisionWorkflow`, `PreMergeValidationService`, the index, and the underlying `AIMonitorTools` all live in the `AIMonitor.*` projects; the Host adapts and orchestrates them but does not implement staging, validation, indexing, or search.

## Gotchas & invariants

- **Write-before-validate (Accept).** `File.Copy` into the watched tree runs *before* GATE 2's authoritative build in `StagedDecisionWorkflow.Record`. The pre-write guard is only GATE 1's fast overlay check. A build that fails after the copy leaves the overwritten file in place — there is no rollback. Known issue.
- **Non-atomic `File.Copy`.** The watched write is a direct `File.Copy(..., overwrite: true)` — not a write-to-temp-then-rename. A crash mid-copy can leave a truncated watched file.
- **`runtime/**` is excluded from compilation.** `DefaultItemExcludes` in the `.csproj` excludes `runtime\**`. Those folders hold working/staged mirrors of watched-solution `.cs` source; if globbed into the Host assembly they produce duplicate-type build errors (CS0101/CS0111) as soon as a watched solution is configured. Do not remove the exclude.
- **SSE fragility.** `SidecarEventStream.ReadStreamAsync` deserializes each `data:` line independently; a malformed JSON payload throws out of the read loop, marks the stream disconnected, and forces the 2 s reconnect — a single bad line tears the current stream. On reconnect it re-seeds gates and elicitations from `/gates` and `/elicitations` so stale state self-heals.
- **DI lifetimes.** `WorkspaceManager`, `EngineReviewWorkflow`, `SidecarEventStream`, `GitService`, `GitWorkspaceService`, `TaskBoardRepositoryFactory`, and `SourceWorkspace` are **singletons**; `SidecarOperatorConsole`, `IOperatorConsole`, `IApprovalQueue`, `UploadService`, and the task view service are **scoped** (per Blazor circuit). The scoped `SidecarOperatorConsole` subscribes to the singleton stream's `Changed` and unsubscribes on `Dispose` — leaking that subscription would pin dead circuits.
- **Single-start / port reuse.** `SidecarProcessHost` skips launching if something already listens on the sidecar port (a dev/standalone sidecar), so a stray process silently takes over supervision.

## Where to start reading

1. `Program.cs` — the whole wiring story: settings load, MCP composition, sidecar options, and every DI registration in ~120 lines.
2. `Services/EngineReviewWorkflow.cs` — the accept path and the one watched-source write; read `Accept` and `EnsureValidatedAndLaunched` together.
3. `Services/SidecarEventStream.cs` — how sidecar events become UI state (the `Apply` switch and reconnect loop).

## Tests

`tests/unit/ClaudeWorkbench.Host.Tests` — the first host test project (`ClaudeWorkbench.Host.Tests.csproj`). `GitServiceTests.cs` covers `GitService`/`GitWorkspaceService`: **15 green** (12 `[Fact]` + a 3-case `[Theory]` over `DraftCommitMessage`), exercising status parsing, staging, diff, commit/push argv, and the ahead/behind regex against a real temp git repo.
