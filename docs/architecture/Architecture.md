# ClaudeWorkbench — Architecture

> A Blazor operator console for **governed, human-gated AI edits** to a watched .NET
> solution, driven by **Claude** through the Claude Agent SDK. The agent proposes
> changes; every change is composed against a local *Working* candidate, staged, and
> held at a human **accept/reject** gate before it ever touches real source.

This document is the top of the architecture tree. It uses the **C4 model** — zooming
from system **Context** → **Container** → per-module **Component** docs (in
[`../components/`](../components/)). For the operator's view, see the
[user guide](../guide/). For the "why", see the [decisions](../decisions/).

---

## 1. Context (C4 Level 1) — the system in its world

```mermaid
flowchart TB
    operator([Operator<br/>the human developer])
    subgraph CWB[ClaudeWorkbench]
        direction TB
        note[Governed AI-edit console]
    end
    claude([Claude<br/>via Agent SDK + claude CLI])
    watched[(Watched .NET solution<br/>on disk)]
    github([GitHub<br/>remote])

    operator -->|runs turns, reviews & accepts edits, commits/pushes| CWB
    CWB -->|drives, deny-by-default tools| claude
    claude -->|proposes edits through governed MCP tools| CWB
    CWB -->|reads always; writes ONLY on the terminal operator Accept| watched
    CWB -->|operator-driven commit/push| github
```

- **Reason in the cloud, edit locally.** Claude reasons from compact context; edits are
  composed against explicit local *Working* candidates and promoted only through review.
- **The gate is code, not a prompt.** Mutations are intercepted and applied only on
  operator approval — see [§5 Governance](#5-governance--the-gate-is-code).
- **Auth is a subscription login.** The Agent SDK spawns the local `claude` CLI, which
  authenticates from the cached `~/.claude` subscription — no `ANTHROPIC_API_KEY` needed
  for personal use.

---

## 2. Containers (C4 Level 2) — the two processes

ClaudeWorkbench is a **two-process** app. A .NET Blazor **Host** and a Node **sidecar**.
An optional third container, the **Launcher**, runs several of those pairs side by side.

```mermaid
flowchart TB
    operator([Operator])
    launcher["ClaudeWorkbench.Launcher (optional)<br/>WinForms control panel · Job Object"]

    subgraph host["Blazor Host — .NET 10 process, :6100"]
        direction TB
        ui["Blazor operator console<br/>Tasks (disabled) · Workbench · Source · Git · Activity"]
        mcp[claude-workbench MCP server<br/>Streamable HTTP /mcp · ~71 tools]
        engine[AIMonitor.* engine<br/>Core · Logging · MSBuild · Data · Indexing · Workflow · McpServer]
        rev[EngineReviewWorkflow<br/>the ONLY watched-source writer]
        git[GitService<br/>argv git CLI, no shell]
    end

    subgraph side["Node Sidecar — :6110, loopback-only"]
        direction TB
        sdk[Claude Agent SDK]
        gate[canUseTool operator gate<br/>deny-by-default]
        bus[neutral event bus → SSE]
    end

    idx[(SQLite solution index)]
    board[(SQLite task board)]
    watched[(Watched solution files)]
    claudecli([claude CLI → Claude])
    remote([GitHub])

    operator --> ui
    ui -->|POST /prompt, /gates, /new-thread| side
    side -->|SSE /events → gate/turn/usage| ui
    sdk --> gate
    sdk -->|MCP client over HTTP| mcp
    sdk --> claudecli
    engine --> idx
    ui --> board
    mcp --> engine
    rev -->|"on Accept: build passes, then atomic write"| watched
    engine -->|reads| watched
    git --> watched
    git --> remote
    host -.->|launches + supervises single-start| side
    operator -.->|optional: pick a workspace| launcher
    launcher -.->|one host+sidecar+browser per workspace,<br/>all in one Job Object| host
```

**Why two processes.** The Claude Agent SDK is **Node-only** (no .NET SDK). So a thin Node
sidecar runs the SDK and is the MCP *client*; the .NET host owns the engine, the MCP
*server*, and the UI. Everything the human sees and every governed tool the agent calls
lives in the **one** .NET process, so logging and state are in-process — no cross-process
pipe. Details: [`../components/Sidecar.md`](../components/Sidecar.md) and
[`../components/ClaudeWorkbench.Host.md`](../components/ClaudeWorkbench.Host.md).

| Container | Port | Runtime | Responsibility |
|---|---|---|---|
| **Host** | 6100 | .NET 10 (ASP.NET/Blazor Server) | Engine, MCP HTTP surface, operator UI, sidecar supervisor, the sole watched-source writer |
| **Sidecar** | 6110 | Node (Claude Agent SDK, Express) | Drives Claude, MCP client, the `canUseTool` operator gate, neutral SSE events |
| **Launcher** *(optional)* | n/a — assigns a free pair per instance | .NET 10 WinForms (Windows) | Multi-instance control panel: one Host+sidecar+browser per workspace, held in a Job Object |

The sidecar binds **loopback only** (`127.0.0.1`) and rejects any request whose `Host` header
isn't localhost or that carries a non-local `Origin` — a DNS-rebinding defense on the control
surface. See [`../components/Sidecar.md`](../components/Sidecar.md).

**The optional third container.** [`ClaudeWorkbench.Launcher`](../components/ClaudeWorkbench.Launcher.md)
runs *above* the pair, not inside the governed path: the Host is single-workspace (one
`WatchedSolutionPath`, one port pair, one runtime), so the Launcher allocates a free host+sidecar
port pair per instance, writes each instance its own config, and starts Host + browser window
inside a Windows **Job Object** so they die together. It sets `CWB_EXIT_WITH_BROWSER=1`, which
makes the Host shut itself down (taking its sidecar with it) once the last browser circuit drops
— `BrowserPresenceTracker` + `BrowserLifetimeCircuitHandler`. The Launcher never reads a watched
solution and never touches the gate; it only points a Host at one. Ports `6100`/`6110` are the
plain-`dotnet run` defaults; under the Launcher each instance gets its own free pair.

---

## 3. The governed edit loop

The heart of the product. An edit never touches real source until the operator accepts it.

```
choose workspace → discover (index) → refresh_file / new_file → governed edit
   → stage session → operator review → accept / reject → post-accept reindex
```

```mermaid
sequenceDiagram
    autonumber
    actor Op as Operator
    participant UI as Blazor UI
    participant Side as Sidecar (gate)
    participant Claude
    participant MCP as MCP tools (engine)
    participant Work as Working mirror
    participant Src as Watched source

    Op->>UI: type a prompt ("add a method to X")
    UI->>Side: POST /prompt
    Side->>Claude: run turn (deny-by-default tools)
    Claude->>MCP: refresh_file / get_source_map / add_method ...
    MCP->>Work: write monitor-owned Working candidate
    Claude->>MCP: stage_candidate_for_review
    MCP->>Work: seal immutable staged snapshot + hash
    Note over Side: mutation tools pause at the operator gate
    Side-->>UI: gate_request (SSE)
    Op->>UI: Approve (or Reject)
    UI->>Side: POST /gates/:id allow
    Claude-->>Side: turn finishes (files staged)
    UI->>Op: open Merge Review (DiffPlex side-by-side)
    Op->>UI: Accept
    Note over UI: EngineReviewWorkflow: still-pending? re-hash staged?<br/>GATE-2 overlay build — any failure = hard stop, nothing written
    UI->>Src: writes staged bytes (temp file + atomic rename)
    UI->>MCP: RecordDecision + post-accept index refresh
```

- The agent **stops at the staging line** — it never calls the accept. The operator's
  **Accept** is the only thing that writes real source.
- **The session is the atomic unit** ([ADR 0005](../decisions/0005-edit-session-is-atomic.md)).
  A per-file Accept before the last file is an *approval* that writes nothing; the **terminal**
  accept writes every approved file together once the combined-overlay build passes. A single
  **Reject voids the session**, including files approved earlier — nothing was written, so
  nothing needs undoing. Half a refactor is not a smaller refactor.
- **Validate, then write.** Every accept check runs *before* watched source is touched; a
  failed build leaves the files exactly as they were.
- **Freshness at accept.** The solution index rebuilds after an accepted decision — the
  normal point where downstream truth is refreshed.

Deep dive: [`../components/AIMonitor.Workflow.md`](../components/AIMonitor.Workflow.md).

---

## 4. The two gates

Two independent checks protect watched source. Both must be satisfied to accept.

| Gate | When | What it checks | Where |
|---|---|---|---|
| **GATE 1 — pre-merge validation** | at stage/review-open | a fast staged-overlay **readiness check**, no `dotnet build` (on the MCP/CLI path, a full overlay build) | `PreMergeValidationService`, `StagedDiffLaunchWorkflow` |
| **GATE 2 — the authoritative build + decision** | at the **terminal** accept, **before anything is written** | the record is still pending, the staged file **re-hashes unchanged**, and the overlay of the whole write set **compiles** (a real build) — then, and only then, every approved-but-unwritten file in the session is written and `expectedStagedHash` / `dirty-unexpected` are re-checked as each decision is recorded | `EngineReviewWorkflow.Accept` → `PreMergeValidationService`, then `WorkflowEditService.RecordDecision` |

**Ordering matters, and it is validate-then-write.** In the in-app path the GATE-2 build runs
*before* watched source is touched: a failed build (or a superseded record, or a staged file that
changed since staging) is a **hard stop** — nothing is written and the operator is told why.
Only a validation override (*Accept With Validation Override*) proceeds past a failing build.
The write itself is a temp file plus an atomic rename, so a crash mid-write cannot truncate
watched source. Because the build already ran, the decision is recorded *without*
`terminalValidationRecords` — it would otherwise build a second time.

The build runs once per **edit session**, on the *terminal* accept (the last pending file),
over the combined overlay of every file accepted in that session — so a multi-file edit is
compiled as a whole, not file by file. The post-accept index rebuild is deferred to that same
terminal accept.

The core safety invariant: **an accepted edit equals exactly what was reviewed** — the
staged snapshot is copy-once/immutable, accept re-hashes it, and accept independently
requires `watched == staged`. See
[`../components/AIMonitor.Workflow.md`](../components/AIMonitor.Workflow.md#the-safety-invariants-the-crux).

---

## 5. Governance — the gate is code

Governance is **not** policy prose in the model's context. Two mechanisms carry it:

- **Enforcement is code.** Every mutation routes through the sidecar `canUseTool` gate
  (operator accept/reject) and the server-side planned-session gate
  (`EnsurePlannedMutationAllowed`). A denied gate cannot be talked around.
- **The agent is deny-by-default.** Only read-only native tools (`Read`/`Grep`/`Glob`,
  plus `ToolSearch`/`TodoWrite`) and the `claude-workbench` MCP tools are permitted;
  `Write`/`Edit`/`Bash`/`PowerShell`/`Agent`/`WebFetch`/anything unknown are **denied**.
  So the watched workspace is read-only to the agent, and every change must go through the
  governed MCP surface.
- **The allow-set cannot be widened from the wire.** A turn's `enabledTools` is intersected
  with a fixed blockable set (`Write`, `Edit`, `MultiEdit`, `NotebookEdit`, `Bash`,
  `PowerShell`) — the writers/shells an operator may deliberately opt back in. Any other name
  in a `/prompt` body is dropped, so a request body can't splice `Agent` or `WebFetch` into the
  permitted surface.

```mermaid
flowchart TD
    tool{tool the agent wants to call}
    tool -->|AskUserQuestion| elicit[route to operator questions dialog]
    tool -->|native read: Read/Grep/Glob/ToolSearch/TodoWrite| allow1[allow]
    tool -->|native other: Write/Edit/Bash/...| deny[deny — not permitted in the governed workbench]
    tool -->|claude-workbench read tool| allow2[allow]
    tool -->|claude-workbench mutation| gatecheck{autoApprove?}
    gatecheck -->|no| pause[pause at operator gate → Approve/Reject]
    gatecheck -->|yes| allow3[allow candidate; watched source still gated by merge-review Accept]
```

Git is handled differently: the agent has **read-only** git via MCP
(`git_status` / `git_diff` / `git_log` / `git_list_branches`, all auto-allowed) and **no
git-write tools at all**. Every git write — commit, push, branch, merge — is an
operator-only action in the Git page, executed by launching `git` directly (never a
shell, never MCP). With no git-write tool to call, auto-approve has nothing to bypass.
Read: [`../guide/git-panel.md`](../guide/git-panel.md).

---

## 6. The engine layering

The engine was extracted from **AIMonitor** (the WinForms-based origin), one testable
layer at a time, keeping the `AIMonitor.*` namespaces so the port stayed mechanical.
Left behind: the WinForms app, the MCP proxy hub, the stdio bridge, the cross-process log
pipe.

```mermaid
flowchart TD
    Core[AIMonitor.Core<br/>settings · paths · identifiers]
    Logging[AIMonitor.Logging<br/>JSON-lines sink + in-proc event source]
    MSBuild[AIMonitor.MSBuild<br/>MSBuild/Roslyn load → snapshots]
    Data[AIMonitor.Data<br/>SQLite index store + queries]
    Indexing[AIMonitor.Indexing<br/>rebuild/refresh orchestration]
    Workflow[AIMonitor.Workflow<br/>edit sessions · staging · gates]
    Mcp[AIMonitor.McpServer<br/>the governed MCP tool surface]
    Host[ClaudeWorkbench.Host<br/>Blazor + MCP HTTP + sidecar]

    MSBuild --> Data
    MSBuild --> Indexing
    Data --> Indexing
    Core --> Workflow
    Data --> Workflow
    Indexing --> Workflow
    Workflow --> Mcp
    Indexing --> Mcp
    Data --> Mcp
    Mcp --> Host
    Workflow --> Host
    Indexing --> Host
    Logging -.-> Host
    Logging -.-> Mcp
```

Each box has its own [component doc](../components/). `Core` is the leaf; `Host` is the
composition root.

---

## 7. MCP binding

The sidecar is the MCP **client**; the Agent SDK connects to the engine's MCP **server**
via its `mcpServers` option. Recommended (and implemented) transport: the Host serves MCP
in-proc over **Streamable HTTP** at `http://localhost:6100/mcp`, advertising
`serverInfo.name` = `claude-workbench` (deliberately distinct from the real monitor's
`ai-monitor`). Tools appear to the agent as `mcp__claude-workbench__*`. `strictMcpConfig:
true` exposes only `claude-workbench` — the machine's other MCP connectors don't leak in.

The surface is ~**71 tools**: ~60 `AIMonitorTools` (Editing, Index, Status, RoslynEdits,
Sessions, Review) + 3 `TaskMcpTools` + 8 `GitMcpTools`.

---

## 8. Logging

Engine narration (index rebuilds, staging, gate results, errors) logs via `IMonitorLogger`
to a **JSON-lines file**; in-process, `MonitorLogService` raises `EntryWritten` for a live
UI view. MCP-call telemetry is re-emitted from the sidecar's `tool_use`/`tool_result`
events and hooks — **not** sniffed off a pipe (the old man-in-the-middle proxy is gone).
See [`../components/AIMonitor.Logging.md`](../components/AIMonitor.Logging.md).

---

## 9. Where to read next

- **New to the codebase?** Start at [`../README.md`](../README.md) — the guided path.
- **Operating the app?** [`../guide/`](../guide/).
- **A specific module?** [`../components/`](../components/).
- **Why a decision was made?** [`../decisions/`](../decisions/).
