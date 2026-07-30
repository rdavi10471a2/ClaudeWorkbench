# AIMonitor.McpServer

> The MCP tool surface the AI agent uses to read, index, and propose governed edits to the watched .NET solution — the only sanctioned way it can touch that solution.

**Project:** `src/AIMonitor.McpServer/AIMonitor.McpServer.csproj` · **Depends on:** `AIMonitor.Core`, `AIMonitor.Data`, `AIMonitor.Logging`, `AIMonitor.Workflow`, `AIMonitor.MSBuild`, `AIMonitor.Indexing`, plus `ModelContextProtocol` 1.3.0 and `Microsoft.Extensions.Hosting` · **Depended on by:** `ClaudeWorkbench.Host` (hosts the same `AIMonitorTools` over HTTP via `MapMcp("/mcp")`) and `AIMonitor.Integration.Tests`.

## Purpose

ClaudeWorkbench is a Blazor operator console for human-gated AI edits to a watched .NET solution. `AIMonitor.McpServer` is the tool boundary between the AI agent and that solution. Every method on the `AIMonitorTools` partial class carries `[McpServerTool]`, and this surface is the *only* way the agent can inspect or mutate watched source: native `Write`/`Edit`/`Bash` are blocked at the sidecar (deny-by-default), so all file touches route through these tools.

The agent never writes watched source directly. It reads through the tools, builds a candidate in a monitor-owned *Working* mirror, stages it, and stops. The operator reviews and accepts in the in-app Merge Review dialog; acceptance is the sole path a candidate reaches the watched solution.

The project ships two entrypoints for the same tool class:

- **stdio console** — `Program.cs` builds a `HostApplicationBuilder`, registers `WorkspaceManager` / `AIMonitorMcpRuntimeState` / `IMonitorLogger`, and calls `AddMcpServer().WithStdioServerTransport().WithTools<AIMonitorTools>()`. Config comes from `--repo-root` / `--config` args via `MonitorSettingsLoader`.
- **Blazor Host** — `ClaudeWorkbench.Host` registers the same `WithTools<AIMonitorTools>()` over `WithHttpTransport()`. This is the surface the running console uses.

## The tool surface

**70** `[McpServerTool]` methods across nine partial-class files (the tool-bearing partials of `AIMonitorTools`; `AIMonitorTools.cs` itself holds only the core helpers). Tool names are exposed in **snake_case**: `ToToolName` (in `AIMonitorTools.cs`) inserts an underscore before every non-leading uppercase char and lowercases, so `StageCandidateForReview` becomes `stage_candidate_for_review`. `AIMonitorMcpRuntimeState.ToSnakeCase` applies the identical transform for telemetry. These are all served under the **claude-workbench** MCP surface; the `git_*` tools the agent may also see (`git_status`, `git_diff`, `git_log`) are **not** defined here — they come from a separate, external MCP server and are counted separately.

| Category (file) | Count | Notable tools | Read vs mutation |
| --- | --- | --- | --- |
| **Editing** (`.Editing.cs`) | 13 | `get_file`, `find_file`, `refresh_file`, `new_file`, `get_file_outline`, `get_source_map`, `get_symbol`, `submit_file`, `replace_text_in_file`, `replace_span_in_file`, `find_text_span`, `check_file_hash`, `get_edit_status` | Reads auto-allow; the three text/full-file writers (`submit_file`, `replace_text_in_file`, `replace_span_in_file`) are gated mutations |
| **Index** (`.Index.cs`) | 15 | `query_solution_index`, `find_indexed_symbols`, `get_indexed_symbol`, `find_indexed_references`, `find_references_in_file`, `find_indexed_callers`, `find_indexed_relationships`, `list_package_references`, `find_project_dependencies`, `get_solution_index_tree`, `refresh_solution_index`, `refresh_file_and_index` | All read/query against the SQLite index (auto-allow); refreshes rebuild monitor-owned index state, not watched source |
| **Status** (`.Status.cs`) | 13 | `get_monitor_status`, `get_workflow_status`, `get_self_check`, `get_tool_manifest`, `get_staging_guide`, `list_ledgers`, `get_ledger`, `list_watched_projects`, `shutdown_server` | Read/introspection (auto-allow); `shutdown_server` is destructive but relies on default-deny gating |
| **RoslynEdits** (`.RoslynEdits.cs`) | 11 | `submit_symbol`, `remove_symbol`, `add_using`, `remove_using`, `set_type_partial`, `add_symbol`, `add_field`, `add_property`, `add_method`, `add_constructor`, `add_nested_type` | All gated mutations (typed C# edits into the Working candidate) |
| **Sessions** (`.Sessions.cs`) | 8 | `start_monitor_session`, `add_monitor_session_planned_file`, `complete_edit_plan`, `set_monitor_session_edit_plan`, `list_monitor_sessions`, `get_monitor_session`, `record_monitor_session_event`, `list_session_staged_records` | Session bookkeeping — writes monitor-owned session JSON, not watched source. `complete_edit_plan` triggers the plan-complete pre-merge build. |
| **Review** (`.Review.cs`) | 4 | `stage_candidate_for_review`, `record_diff_decision`, `get_staged_record`, `compare_file` | `stage_candidate_for_review` is gated; `record_diff_decision` is an operator/host review action. `launch_staged_diff` was removed with the external diff-tool path. |
| **Notes** (`.Notes.cs`) | 4 | `write_note`, `list_notes`, `read_note`, `delete_note` | The agent's **ungoverned** scratchpad under `runtime\<workspace>\agent-notes` — outside watched source, no operator gate, path-confined (see below). `write_note` is the *only* place the agent writes directly. |
| **Nuget** (`.Nuget.cs`) | 1 | `restore_solution` | Host-run `dotnet restore` from the solution root; called after the agent stages a `<PackageReference>` edit so assets restore for the pre-merge build and index |
| **Download** (`.Download.cs`) | 1 | `download_url` | Gated fetch of an http(s) file (e.g. an image) into the workspace `uploads/` folder behind a manual-redirect SSRF guard; returns ready-to-embed `markdown` |

**Governed mutation path:** candidate write (`submit_*` / `replace_*` / Roslyn edits into the Working mirror) → `stage_candidate_for_review` (immutable staged record) → operator review decision (`record_diff_decision` / in-app Accept). Only the accept step writes watched source. Mutations are gated twice: at the **sidecar operator gate** (where native tools are also blocked) and server-side by `EnsurePlannedMutationAllowed`.

## Key types

| Type | File | Role |
| --- | --- | --- |
| `AIMonitorTools` | `AIMonitorTools*.cs` (partial) | `[McpServerToolType]` class holding all tool methods. Ctor-injected with `WorkspaceManager`, `AIMonitorMcpRuntimeState`, `IHostApplicationLifetime`, `IMonitorLogger`. |
| `WorkspaceManager` | `WorkspaceManager.cs` | Owns the current watched workspace and the engine services bound to it (`Query`, `EditService`, `RoslynEditService`, `EditPaths`, `Settings`). `SwitchTo()` rebuilds them for a new solution at runtime; `ProvisionAsync()` builds the index. Tools read services through it so the whole surface retargets on a workspace switch. |
| `AIMonitorMcpRuntimeState` | `AIMonitorMcpRuntimeState.cs` | Tracks `LastActivityUtc` / `ShutdownRequested`. `Touch()` (called first in every tool via `[CallerMemberName]`) stamps activity and emits an `adapter.mcp.tool.called` log line. |
| Contracts (`AIMonitorSessionState`, `AIMonitorSessionEditPlan`, `AIMonitorSessionPlannedFile(Input)`, `AIMonitorFileReadResult`, `AIMonitorToolErrorResult`, `AIMonitorSelfCheckResult`, `PlannedSessionDecisionOptions`, …) | `AIMonitorToolContracts.cs` | `sealed record` DTOs returned/accepted by tools. `AIMonitorToolErrorResult(IsError, Message, Expected, Received)` is the guided error shape; `AIMonitorSessionState.EditPlan` holds the planned-file scope that gates mutations. |

## How it works

Every tool call flows through the `WorkspaceManager`, which resolves the engine services for the *current* watched workspace. The tools themselves hold no engine state — `settings`, `queryService`, `workflowService`, `roslynEditService`, and `workflowPaths` are all forwarding properties onto `workspace.*`. Read tools hit the query service or the filesystem; mutation tools funnel into `WorkflowEditService` / `RoslynEditService`, which write only the monitor-owned Working mirror.

```mermaid
flowchart TD
    agent[AI agent] -->|MCP call over stdio or HTTP| tools["AIMonitorTools<br/>every method Touch-es runtime state"]
    tools --> rt["AIMonitorMcpRuntimeState<br/>activity + shutdown"]
    tools --> wm["WorkspaceManager<br/>current workspace + services"]
    wm --> settings[MonitorSettings]
    wm --> query[SolutionIndexQueryService]
    wm --> edit[WorkflowEditService]
    wm --> roslyn[RoslynEditService]
    wm --> paths["WorkflowEditPaths<br/>containment + Working mirror"]
    query --> db[(SQLite solution index)]
    edit --> working[("monitor-owned Working / Staged / History")]
    roslyn --> working
    working -.operator Accept only.-> watched[(watched .NET solution)]
```

## Path containment (security)

Every path a tool receives is normalized and confined to the watched solution before use:

- `AIMonitorTools.ResolveWatchedPath(path)` rejects blank input, resolves relative paths against `settings.WatchedProjectFolder`, `Path.GetFullPath`-normalizes (collapsing `..\` traversal), then calls `workflowPaths.GetRelativeWatchedPath(fullPath)` purely for its side effect: to throw if the result escapes the watched root.
- `WorkflowEditPaths.GetRelativeWatchedPath` is the boundary. It requires the full path to `.Equals(watchedRoot)` **or** `.StartsWith(watchedRoot + Path.DirectorySeparatorChar)`. The **`+ separator` guard is load-bearing**: a bare `StartsWith(watchedRoot)` would let a sibling directory whose name merely *prefixes* the root (e.g. `C:\Watched` vs `C:\WatchedEvil`) slip through. Anchoring on `root + \` defeats that sibling-prefix escape, and full-path normalization already neutralizes `..\` traversal. Anything outside throws `File is not under the watched solution folder`.

`ResolveWatchedPath` is the front door for nearly every path-taking tool, so the containment check is uniform across the surface. `get_ledger` adds a second, independent guard (rejecting `..`-relative ledger paths under the ledger root).

## Session/plan gate

Watched-source mutations require an active session whose declared plan includes the target file. This is the server-side complement to the sidecar operator gate.

- `start_monitor_session(filesPlanned)` requires a non-empty planned list and persists an `AIMonitorSessionEditPlan` of `AIMonitorSessionPlannedFile` entries (each resolved + given an owning MSBuild project) to session JSON under the workspace `workflow/sessions` root.
- `EnsurePlannedMutationAllowed(sessionId, sourceFilePath)` is invoked at the top of every mutating tool (`submit_file`, `replace_text_in_file`, `replace_span_in_file`, all Roslyn edits, `stage_candidate_for_review`). A missing/blank `sessionId` throws (`Session edit scope is required…`); otherwise `RequireSessionEditPlan` loads the plan and `EnsurePlannedFile` throws `Source file is not in the session edit plan` unless the target's full path matches a planned file.
- **Plan-complete build + deferred index refresh:** there is no per-edit overlay validation — each candidate write is syntax-only. Once every planned file has a submitted candidate, `complete_edit_plan(sessionId)` runs the REAL pre-merge `dotnet build` once over the whole working set (`WorkflowEditService.ValidatePlannedOverlayBuild`) and echoes the actual compiler error lines back so the agent fixes and re-runs before staging. Separately, `BuildPlannedSessionDecisionOptions` (at `record_diff_decision`) defers the expensive post-accept index rebuild via a `PostAcceptIndexRefreshPlan` until every planned file reaches a terminal decision. (The former per-edit overlay-defer plumbing — `ShouldDeferPlannedOverlayValidation` and the `deferOverlayValidation` → `validateOverlay` parameter chain — has been removed; `EnsurePlannedMutationAllowed` alone enforces the planned-file guard.)

## Agent notes scratchpad (ungoverned, path-confined)

`AIMonitorTools.Notes.cs` gives the agent an **ungoverned** scratchpad — the second, distinct write destination the governance card is careful to separate from watched source. `write_note` / `list_notes` / `read_note` / `delete_note` operate under `MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings)\agent-notes`, which lives under the per-workspace runtime folder, **physically outside watched source**. There is no session, no staging, no operator gate, and no hash check: the agent writes plans and working notes freely, and nothing here can mutate governed source.

The containment guarantee lives in the tool, not in caller good behaviour. `ResolveNotePath` rejects a blank path, rejects any rooted path, and requires the resolved absolute path to sit strictly under the notes root (`fullPath.StartsWith(root + separator)`, both `\` and `/`), so no `..` escape can reach source or any other runtime folder — the same `root + separator` discipline `WorkflowEditPaths` uses for watched paths. `write_note` supports `append` and returns the relative/absolute path and byte count.

This is durable memory: the notes folder persists across conversations (a transcript does not), and `user-preferences.md` in that folder is the agent's long-term memory (see the guidance card below).

## Agent guidance: the governance card + staging guide (`AgentGuidance.cs`)

`AgentGuidance` is the **single source of truth** for the agent's operating rules, authored once in C# and served over HTTP so the sidecar carries none of the prose itself (duplicated prose drifted — the reason it was consolidated here):

- `ComposeGovernanceCard(watchedProject)` builds the full role card the sidecar injects as a system-prompt append, served at `GET /guidance/card`. It establishes: this is a governed chat app (not a terminal), the operator sets which tools exist per turn, inline Markdown/image display via `download_url`, native Mermaid diagrams (built from the source index / read source, never memory, with an index-freshness check), the index-vs-live distinction, and the two write destinations.
- `StagingGuide` (a cached `ComposeStagingGuide()`) is the numbered edit procedure, served at `GET /guidance/staging` and returned by the `get_staging_guide` tool (`ComposeStagingGuide` in `AIMonitorTools` just delegates to `AgentGuidance.StagingGuide`).

Points reflected in the current card worth calling out:

- **`user-preferences.md` auto-load.** The card documents the notes scratchpad as durable cross-session memory and names `user-preferences.md` as the agent's long-term memory. Its contents are **already appended to the card** (under "Your durable preferences") when the file exists, so the agent never reads it to know standing preferences — its job is to *keep it current* via `write_note` whenever the operator states a durable preference.
- **Edit at the precision you read.** The staging guide's step 5 tells the agent to locate code with `get_source_map` at the level the change needs and edit at that same precision — a source-map selector is the exact coordinate for a targeted semantic edit (`submit_symbol` / `add_method` / `replace_span_in_file`); `submit_file` is reserved for a new file or wholesale rewrite.
- **Index for blast radius.** The card frames the index as existing for *blast radius* — `get_source_map` and the edit tools are live (no index), so a contained/internal change needs no index; index freshness matters only for an external-interface change, where `find_indexed_references` / `find_indexed_callers` must enumerate every affected file (including `.razor` markup and cross-project usages) to declare in `start_monitor_session`.
- **`replace_text_in_file` is last-resort.** It is reserved for where a semantic edit cannot reach — Razor/markup, Markdown, config — or as a true last resort; the Roslyn symbol tools are C#-only, and references resolve inside `.razor` but *editing* markup is text-based.
- **NuGet the governed way.** The card tells the agent to add a package by editing the owning `.csproj`'s `<PackageReference>` (or `Directory.Packages.props` under central package management) as a planned file, then call `restore_solution` — it never shells out to `dotnet add package`. (The operator's direct package UI is backed separately by `NuGetPackageService` in `AIMonitor.Workflow`.)

## Owns / Does Not Own

**Owns:**
- The MCP tool surface (**70** `[McpServerTool]` tools across nine partial-class files — including the semantic Roslyn edit tools `submit_symbol` / `add_method` / `add_symbol` / `add_field` / `add_property` / `add_constructor` / `add_nested_type` / `remove_symbol` / `add_using` / `remove_using` / `set_type_partial`, the ungoverned notes scratchpad `write_note` / `list_notes` / `read_note` / `delete_note`, and the gated `download_url` / `restore_solution`) and its snake_case naming. The external `git_*` tools are not part of this surface.
- `WorkspaceManager` (current watched workspace + per-workspace engine service graph, runtime switching).
- `AIMonitorMcpRuntimeState` (activity/shutdown) and the tool-call telemetry line.
- The MCP tool DTO contracts (`AIMonitorToolContracts.cs`).
- Session-scope enforcement (`EnsurePlannedMutationAllowed`), path resolution into the watched root, self-check guardrails, and the tool manifest. The staging guide + sidecar governance card are authored in `AgentGuidance.cs` (single source, served at `GET /guidance/staging` and `/guidance/card`); `ComposeStagingGuide` just delegates to `AgentGuidance.StagingGuide`. The card includes the session-splitting rule (one interdependent change → one session), the `user-preferences.md` auto-load, and the current editing guidance (edit-at-precision-you-read, index-for-blast-radius, `replace_text_in_file` as last resort).
- The agent's ungoverned notes scratchpad (`write_note` / `list_notes` / `read_note` / `delete_note`) and its `agent-notes` path confinement (outside watched source, no operator gate).
- The stdio console entrypoint (`Program.cs`).

**Does not own:**
- The edit engine itself — `WorkflowEditService`, `RoslynEditService`, `WorkflowEditPaths`, `StagedDecisionWorkflow`, `StagedDiffLaunchWorkflow` all live in `AIMonitor.Workflow`.
- The solution index — `SolutionIndexQueryService` / `SolutionIndexRebuildService` live in `AIMonitor.Indexing`.
- The HTTP hosting, the sidecar operator gate, and the in-app Merge Review UI — those are `ClaudeWorkbench.Host`.
- `MonitorSettings` and workspace path layout (`AIMonitor.Core`).

## Gotchas & invariants

- **`get_indexed_symbol` full-scan cap:** it calls `FindSymbols(string.Empty, maxResults: 50000)` and `FirstOrDefault`s for the stable key. A symbol whose row sits beyond the 50,000 cap returns **`null` silently** — indistinguishable from "not found."
- **Read-tool IO errors leak raw:** `get_file`, `get_ledger`, `list_monitor_runs`, etc. call `File.ReadAllText` / `Deserialize` directly. A locked/deleted/corrupt file surfaces as an opaque framework exception, not the guided `AIMonitorToolErrorResult` shape used elsewhere (e.g. the Roslyn markup guidance path or `TryCreateIndexedStableSymbolKeyError`).
- **`shutdown_server` is destructive:** it flips runtime state and schedules `applicationLifetime.StopApplication()` after a 100 ms delay. It has no per-call auth of its own — its safety rests entirely on default-deny gating at the sidecar.
- **WinMerge is fully gone.** The external diff-tool path is retired end to end: `launch_staged_diff` and the launcher machinery are deleted, `WinMergeCandidatePaths` is removed from settings/status/contracts, and the tool descriptions, `get_tool_manifest` Safety Note and `get_staging_guide` no longer mention it. This matters because those strings are **agent-facing** — they were telling the model to expect a review path that did not exist. `record_diff_decision` remains: it is the decision path, not a diff-tool artifact.
- **`get_monitor_status` surfaces an "index blocked on a bad build" signal.** It projects `MonitorStatusResult.IndexUpdateBlocked` / `LastBuildError` / `BlockedAtUtc` straight through. Per ADR-0007 (the index rides the build), a red build leaves the symbol index untouched — it is the *last-good* view — and records the block out-of-band in a sibling `index-health.json` (`IndexHealthMarker`, beside the index DB, deliberately not a row inside it). So `StaleFileCount` alone cannot distinguish "stale, just reindex" from "stale AND frozen until the build compiles"; `IndexUpdateBlocked` names the latter, and a reindex will not advance the index until the build is green. The governance card tells the agent not to trust freshness for blast radius while blocked.
- **Services retarget at runtime:** tools capture no service singletons; a `WorkspaceManager.SwitchTo()` mid-run repoints the entire surface. Anything caching a path/handle across calls must re-resolve.
- **`find_text_span` / `replace_span_in_file` call `EnsureSession`** (ensures an editable Working session for the path) in addition to (for the span writer) `EnsurePlannedMutationAllowed`.

## Where to start reading

1. `Program.cs` — DI wiring and the stdio entrypoint; shows the four injected services.
2. `AIMonitorTools.cs` — the partial-class core: `ToToolName`, `ResolveWatchedPath`, `EnsurePlannedMutationAllowed`, `RequireSessionEditPlan`, session persistence helpers, and the manifest/staging-guide text.
3. `WorkspaceManager.cs` — how the current workspace and engine services are owned and swapped.
4. `AIMonitorTools.Editing.cs` then `.RoslynEdits.cs` — the read-then-candidate-then-stage mutation shape.
5. `AIMonitorTools.Review.cs` + `.Sessions.cs` — staging, decisions, and the planned-session gate in action.

## Tests

Integration coverage lives in `tests/integration/AIMonitor.Integration.Tests/`:

- `McpServerSmokeTests.cs` — broad smoke pass over the tool surface.
- `McpReadIndexSurfaceTests.cs` — read/index tools (`query_solution_index`, `find_indexed_*`, tree/status).
- `McpPlannedSessionSurfaceTests.cs` — the planned-session gate: `start_monitor_session`, plan enforcement, staging, and decision/refresh deferral.
