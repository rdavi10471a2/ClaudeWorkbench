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

## Staged plan

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
