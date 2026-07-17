# UI migration — CodexAppServerDemo Blazor UI → ClaudeWorkbench

Decision (operator): pull the CodexAppServerDemo Blazor UI over as the **starting point**, keep the
look/layout, and rework the *interaction data structures + code* to the new Claude/sidecar surface.
Keep the Node sidecar (two-process: .NET host serves MCP + UI; Node sidecar drives Claude).

## Why this is tractable: the surfaces map ~1:1

The Codex UI binds to one service (`CodexConnectionService`: a `Changed` event + `GetSnapshot()`)
and a handful of event records. Every one has a direct sidecar equivalent:

| Codex UI concept | Codex source | Sidecar equivalent (ours) |
|---|---|---|
| `CodexConnectionService` (Changed + snapshot) | `Services/CodexConnectionService.cs` | `WorkbenchConnectionService` over [[SidecarEventStream]] (`Changed`) + `SidecarClient` |
| `SendTurnAsync(prompt, ...)` | connection svc | `SidecarClient.PromptAsync(prompt)` → `POST /prompt` |
| `AssistantTextEvent(Text, IsFinal)` | `CodexEvents.cs` | `SidecarEvent{ type:"assistant_text", text }` |
| `ToolEvent(EventType, Name, Status, ...)` | `CodexEvents.cs` | `tool_call_started` / `tool_call_finished` |
| `TelemetryEvent(InputTokens, …, %window)` | `CodexEvents.cs` | `usage{ inputTokens, outputTokens }` (no %-of-window; drop that UI) |
| `CodexServerRequestEvent` / permission approve/deny | connection svc | `gate_request` + `SidecarClient.ResolveGateAsync(id, allow/deny)` |
| `StatusEvent` | `CodexEvents.cs` | `turn_started` / `turn_finished` / `error` |
| `IsServerStarted` / `IsTurnRunning` | snapshot | `SidecarEventStream.Connected` / `ActiveTurn` |
| Start/Stop server, StartThread, Interrupt | connection svc | sidecar is always-on; Start/Stop become connect-checks, Interrupt = abort (later) |

## What is Codex-specific and gets dropped / deferred

- **JSON-RPC plumbing** (`CodexAppServerClient.cs`, `JsonRpcMessage.cs`) — gone; the sidecar is the client.
- **Thread/turn lifecycle**, `thread/tokenUsage/updated` %-of-window, `approval_mode`, elicitation
  probe — Codex wire shapes; do not port. Usage shows plain token counts.
- **RawProtocolTab** (raw JSON-RPC) — drop, or repoint at the SSE event log.
- **Tasks board / ArchivedDiscussions / workflow coordinator** — Codex-server features; defer (port
  later only if wanted).
- **Bundled `Mcp/` workspace tools** — replaced by the engine's `claude-workbench` MCP surface the
  sidecar already registers.
- **`docs/policy/CS-*.txt` turn-start injection** — explicitly NOT ported (see Governance model:
  gate + on-demand skills, no turn-start policy).

## What is kept (the value)

Visual shell + styling (`App`, `Routes`, `MainLayout`, `wwwroot/app.css`, the header/command-bar,
the `RadzenTabs` control-grid), and these tabs rewired to the sidecar: **Assistant** (prompt + streamed
text), **Tools/Output** (tool-call feed), **Source** (browse watched tree — via engine MCP), and a
**Review/Approvals** surface (the gate: pending `gate_request`s with Allow/Deny → `ResolveGateAsync`).
Packages carried: Radzen.Blazor, DiffPlex, Markdig.

## Decompose Home — no 2,246-line code-behind

The Codex `Home.razor.cs` is a 2,246-line monolith orchestrating every tab, all state, the directory
browser, permissions, and workflow. Do **not** port that shape. Break it into small modules with
backing models:

- `Home.razor` — thin shell only: command bar + workspace toolbar + `RadzenTabs` host. No logic.
- `HomeState.cs` — backing model: repo root, busy flags, active tab, run-started-at.
- `Services/WorkbenchConnectionService.cs` — connection + turn state and a `Changed` event, fed by
  [[SidecarEventStream]]; the Codex-shaped snapshot the tabs bind to.
- One small component **per tab**, each with its own code-behind + view-model:
  `AssistantTab` (prompt + streamed text ← `assistant_text`), `ToolsTab`/`OutputTab`
  (tool feed ← `tool_call_*`), `ApprovalsTab` (gate list ← `gate_request` / `ResolveGateAsync`),
  `SourceTab` (watched tree ← engine MCP), `StatusTab` (usage/connection ← `usage`/`turn_*`).
- Dialogs (StagedReview / PreMergeValidation) and the directory browser stay as their own components
  with their own models, ported when their tab is.

Hosting: the UI folds into **`ClaudeWorkbench.Host`** (already serves MCP + owns `Services/Sidecar*`),
so it stays one MCP+UI process — no separate `.Web` project. `.Host`'s csproj gains Radzen.Blazor,
DiffPlex, Markdig.

## Revised plan (v2 — operator-agreed)

**Priority:** Assistant tab · permission+elicitation model · merge dialog + pre-merge overlay + governed
workflow · keep the task model.
**Defer:** Source/Tests tabs, file uploads, Discuss/Work chat modes (a VS-Code-era abstraction),
archive-conversation dialog (revisit via the sidecar later).
**Dead:** the File tab.

### Structure rules (operator directive — "structured properly")
Small, focused interfaces (interface segregation). Everything in its own file: **data structures**,
**interfaces**, **`.razor`**, **`.razor.cs`** (code-behind — no inline `@code` dumps), **`.razor.css`**.

### Layers
Abstraction (`Console/`): models + small interfaces. Adapters (`Services/`): one per backend.

| Interface | Exposes | Adapter (today) |
|---|---|---|
| `IOperatorConsole` | turn status, transcript (Markdig), send | `SidecarOperatorConsole` (sidecar) |
| `IApprovalQueue` | approvals + elicitations, resolve/answer | `SidecarApprovalQueue` (sidecar) |
| `IReviewWorkflow` | staged reviews, diff, pre-merge validation, accept/reject | `EngineReviewWorkflow` (**in-process engine**) |
| `ITaskBoard` | tasks: create/promote/list/status | `SqliteTaskBoard` (runtime SQLite) |

Review is host↔engine **in-process** (`stage_candidate_for_review` → `launch_staged_diff` →
`record_diff_decision`); the agent stages, the *operator* decides in the UI.

### Permissions & elicitation (Claude-native, no MCP-schema in UI)
- **Approval** = `canUseTool` (allow/deny, optional input edit). Neutral `ApprovalRequest`. No
  session/persistent/MCP-tool-variant cruft in the component.
- **Elicitation** = generic `Elicitation { Id, Question, Field[] }` now; the agent raises it via a
  `request_operator_input` engine tool surfaced through the same pause/resume plumbing as the gate.
  Kept generic; Claude-specific tuning + multi-agent mapping is a later problem.

### Port faithfully (do NOT reinvent)
- **Merge dialog + overlay**: port `Components/Pages/Home/Tasks/StagedReviewDialog.razor` (DiffPlex,
  in-app — "exactly as used") + `PreMergeValidationDialog.razor`, rebound to `IReviewWorkflow`.
- **Task SQLite**: port `AICodingServices/Data/WorkflowTaskBoardDatabase.cs` +
  `WorkflowTaskBoardRepository.cs` (+ `SystemDataPaths.cs`) into the task store, runtime-rooted DB.
- **Assistant layout**: port the real composer + resizable splitter (`sourceResize.js`) + chat-history
  transcript + agent-action modal; omit attachments / mode bar / task-promotion-inline / archive.

### Build order
1. `Console/` layer: model files + the four small interfaces (each its own file).
2. `Services/` adapters: refactor `SidecarOperatorConsole`; add `SidecarApprovalQueue`; keep green.
3. Assistant tab faithful (`.razor` + `.razor.cs` + `.razor.css`) on `IOperatorConsole`/`IApprovalQueue`,
   incl. the agent-action modal (approvals + generic elicitation form).
4. `SqliteTaskBoard` (ported) + Tasks tab (ported, rebound to `ITaskBoard`).
5. `EngineReviewWorkflow` + MergeReviewDialog (ported DiffPlex) + PreMergeValidationOverlay + wire the
   governed stage→review→decide loop.
6. Governance tightening: restrict the agent to `mcp__claude-workbench__*`.

## Staged plan (v1 — superseded by v2 above)

1. **Project + shell.** New `ClaudeWorkbench.Web` (Blazor Server + MCP + Radzen/DiffPlex/Markdig,
   references the engine + reuses `Services/Sidecar*`). Copy the UI shell (App/Routes/_Imports/
   MainLayout/wwwroot). Build green with an empty Home.
2. **Connection adapter.** `WorkbenchConnectionService` presenting a Codex-shaped snapshot/`Changed`
   over `SidecarEventStream` + `SidecarClient`, so ported components bind with minimal edits.
3. **Assistant tab.** Prompt box → `PromptAsync`; render streamed `assistant_text`; status from
   `Connected`/`ActiveTurn`.
4. **Tools/Output tab.** Bind the tool-call feed to `tool_call_started/finished`.
5. **Approvals (gate).** Port the permission UI to the gate: list `gate_request`s, Allow/Deny.
6. **Source tab.** Browse the watched tree via the engine MCP (`find_file`/`get_file`).
7. **Prune + polish.** Remove dead Codex-only components; retire the minimal `ClaudeWorkbench.Host`
   once `.Web` supersedes it (or fold Host's MCP wiring into `.Web`).

Each stage builds green before the next. The heavy Codex tabs (Tasks, ArchivedDiscussions, Raw
protocol) are not on the critical path.
