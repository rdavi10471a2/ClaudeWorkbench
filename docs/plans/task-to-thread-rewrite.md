# Plan — Retire the Tasks board, rewrite as Conversations

> **SHIPPED (2026-07-26).** This plan is complete and live: named, resumable **Conversations** replaced
> the Tasks board (own `conversations.sqlite`, transcript mirror + resume, Current/Archived, a
> Conversations modal from the composer's conversation bar, persist-immediately naming, hard delete).
> The final naming is **Conversation** (not "thread"); see [../guide/conversations.md](../guide/conversations.md).
> Kept as a historical design record — the wording below predates the rename and says "thread".

**Status:** proposed (2026-07-25). No code written yet — this is for review.
**Supersedes:** `docs/ThreadTaskWorkflow.md` (the deferred thread↔task design).
**Design record:** the decisions below were agreed in conversation; this is the implementation plan.

## Why

Conversation-driven development currently leaves no durable trail — a New Thread discards context, and
re-running a line of work means re-typing it. The Tasks board (a Codex-era kanban, currently UI-disabled)
is heavier than the actual need. We replace both with **named, resumable conversation threads**: name a
conversation, reopen/resume it, and see what it changed. This closes the gap with spec-driven dev — a saved
thread is a lightweight, *after-the-fact* spec, and (uniquely) it links to the exact edits it produced.

## The model (agreed)

- **A thread is the atom.** `{thread_id, name, description, user_note, sessionId, cwd, status, kind, timestamps, acceptedEditRefs[]}`.
- **The board collapses into a thread list; states are a DERIVED view of lifecycle**, not a maintained
  state machine:
  - **Planned** = a named *stub*, no `sessionId` yet (pre-conversation intent — kept, it's the one useful board affordance).
  - **Active** = the thread whose session is the current live one. Single-operator ⇒ single-Active for free (computed, not stored).
  - **Archived** = has a `sessionId`, resumable, not active. **Abandoned** = discarded.
- **The old "discussion mode vs work mode" split is gone.** One thread type; `kind: discussion | task` is a
  *status/promotion* (keep-as-discussion vs promote-to-task), not a mode chosen up front.
- **Moving a thread between statuses (first-class UI action):**
  - Explicit user moves from the thread list: **Archive**, **Abandon**, **Restore** (back to a normal
    non-terminal state), and **promote/demote `kind`** (discussion ⇄ task). These just update the `status`/
    `kind` column — the kanban-like "move between columns" you wanted, minus the machinery.
  - **Active is not a place you drag into** — it's *computed* (= the currently live session). A thread
    becomes Active by **Open/Resume**; it leaves Active when another thread opens or on New Thread.
  - **Delete** (hard) is always available: removes the DB row + the transcript JSONL to reclaim disk.
- **Local-only** (no cross-machine resume). No `SessionStore` adapter needed.
- **Provenance kept:** a thread links to the accepted-edit records it produced.

## Storage & data contracts

- **Thread index = its OWN dedicated little SQLite DB (per workspace, app-owned):** `runtime\<workspace>\threads.sqlite`.
  **Separate file, separate schema — NOT the solution index DB** (`AIMonitor.Data`'s `SolutionIndexDatabase` /
  index db) and NOT the old kanban `board.sqlite` (Phase 0 deletes that). Reuse only the `Microsoft.Data.Sqlite`
  *plumbing/patterns*, not the solution DB itself — thread metadata never touches the code index. Structured/
  queryable fields (description, user note, status) are what a DB is for.

  Table `threads`:
  | column | notes |
  |---|---|
  | `thread_id` TEXT PK | **stable** key + resume pointer; never changes on rename/promote |
  | `name` TEXT | display name; DEFAULT `discussion-YYYY-MM-DD-N` where **N iterates by thread** (that day's Nth thread, per workspace = `count(threads created today)+1`); human-editable to anything |
  | `description` TEXT | human "what this thread is about" — the semantic label the auto-name can't give |
  | `user_note` TEXT | freeform human notes (the old task user-notes, kept) |
  | `session_id` TEXT NULL | null while **Planned** (stub) |
  | `cwd` TEXT | exact cwd — REQUIRED for correct resume |
  | `status` TEXT | `planned|archived|abandoned` (**Active is computed** = the live session, not stored) |
  | `kind` TEXT | `discussion|task` promotion flag; default `discussion` |
  | `created_at_utc`, `updated_at_utc` TEXT | |

  Provenance: table `thread_edits(thread_id, staged_record_id)` (or a JSON column) linking a thread to the
  accepted edits it produced.

- **Transcript** stays in `~/.claude/projects/<encoded-cwd>/<session-id>.jsonl` (pointer-only; local).
- **Agent notes/scratchpad** stay as FILES in `runtime\<workspace>\agent-notes\` (Phase 1) — files suit
  freeform scratch; the DB suits structured thread metadata.
- All under `runtime\` (preserved by `publish-live.ps1`). Hard-delete removes the DB row + the `~/.claude`
  transcript file (and, for notes, the files).

---

## Phase 0 — Demolish (self-contained; keeps build + tests green)

Delete (task subsystem — no tests reference it, sidecar has zero task refs, tab already disabled):
- `src/ClaudeWorkbench.Host/Tasks/`: `TaskMcpTools.cs`, `WorkflowTaskBoardDatabase.cs`,
  `WorkflowTaskBoardRepository.cs`, `TaskBoardRepositoryFactory.cs`, `WorkflowTaskBoardModels.cs`,
  `TaskBoardViewModels.cs`, `IWorkflowTaskBoardViewService.cs`, `WorkflowTaskBoardViewService.cs`.
- `src/ClaudeWorkbench.Host/Components/Pages/Tabs/Tasks/`: `TasksTab.*`, `TaskNavigator.*`,
  `TaskWorkspace.*`, `TaskUiRequests.cs`.
- **Keep** `ArchivedDiscussionViewerDialog.*` and `ArchivedDiscussionRow.cs` (repurpose as the thread
  viewer in Phase 3); its storage moves off `board.sqlite`.

Surgical edits (do NOT over-delete — these files/dirs are shared):
- `Program.cs`: remove `.WithTools<Tasks.TaskMcpTools>()` (~:56), `AddSingleton<TaskBoardRepositoryFactory>` (~:89), `AddScoped<IWorkflowTaskBoardViewService,...>` (~:90).
- `RuntimeProvisioner.cs`: remove the `planning/task-memory` subdir line (~:19) and the `board.sqlite` `EnsureCreated()` line (~:33). **KEEP** the `planning/` dir and everything else.
- `Home.razor` (~:95-100): remove the already-commented `Tasks` `RadzenTabsItem` block.
- `wwwroot/js/sourceResize.js`: remove only `attachTaskSplitter` (file is SHARED — Source/Assistant use it).
- `AgentGuidance.cs` (~:48): remove the task-board bullet in `ComposeGovernanceCard` + the "task board"
  mentions in the class comment. **KEEP** the card (load-bearing; fetched at `/guidance/card`).

Verify: `dotnet build` clean; unit + integration green; MCP tool count drops by 3 (71 → 68 before Phase 1).
Commit.

## Phase 1 — `write_note` MCP tool (standalone)

- New MCP method(s) on a small tool type (or folded into `AIMonitorTools`): `write_note(relativePath, content, append?)`, and likely `list_notes()`, `read_note(relativePath)`, `delete_note(relativePath)`.
- **Path containment (in the tool):** resolve `relativePath` under `runtime\<workspace>\agent-notes\`; reject
  rooted paths and `..`; the resolved full path must start with the notes root (case-insensitive on Windows).
  Mirror `WorkflowEditService`'s watched-path containment. The agent can write ONLY inside that folder.
- **Auto-approved:** under `runtime`, not watched source → no operator gate.
- **Startup card:** add the "two write destinations" paragraph to `ComposeGovernanceCard` — (1) watched
  source = read-only, changes only via governed MCP → staging → operator accept; (2) the notes dir = write
  freely, ungoverned, never touches source.
- Supersedes the deleted `update_agent_notes`.
- **Tests:** containment (escape attempts rejected), round-trip write/read, auto-approve (no gate), and an
  MCP smoke assertion that the tool is registered. Update the tool count in docs.
- Commit.

## Phase 2 — Thread persistence (engine + sidecar)

- **Persist on New Thread + autosave per turn** (crash-safe): upsert the thread row from the current
  session (`currentSessionId`, `cwd`, `acceptedEditRefs`); assign the default `name` on first save.
- **Reopen/resume:** a host action that calls the sidecar to start a turn with `resume: <sessionId>` and the
  **stored `cwd`**. NOTE: the sidecar currently derives `resume` once from `currentSessionId`; Phase 2 adds a
  way to resume a *specified* session id on reopen (small sidecar change — the one non-trivial sidecar edit).
- **Naming:** default `discussion-YYYY-MM-DD-N` (N = that day's sequence, per workspace) — deterministic, no
  agent call. Human-editable to a meaningful name at any time. Display `name` is editable; the `threadId`
  (file key + resume pointer) is stable and never changes on rename/promote.
- **Hard delete:** delete the `threads` row (+ `thread_edits`) AND the `~/.claude` transcript JSONL (path
  from `cwd`+`sessionId`) to reclaim disk. Confirm dialog. No governance gate (not watched source).
- **Provenance:** capture `acceptedEditRefs` from the session's accepted staged records.
- **Tests:** save→reopen→resume round-trip; stub (Planned, no session) → open → gains session → Active;
  hard-delete removes both files; containment/path safety on the threads dir.

## Phase 3 — Thread UI + docs

- Replace the Tasks tab with a **thread list** (or a "New Thread / Open Thread…" switcher in the composer
  header). States are the derived view (Planned/Active/Archived/Abandoned). Reuse `ArchivedDiscussionViewerDialog`
  as the thread/transcript viewer. Optional badge: Archived-but-transcript-swept (un-resumable).
- The **New-Thread save/promote prompt** (built fresh — it never existed): on New Thread with a non-empty
  live thread, show name (prefilled w/ suggestion) + [Save as discussion] / [Promote to task] / [Discard].
- **Docs:** supersede `docs/ThreadTaskWorkflow.md`; rewrite `docs/guide/tasks-board.md` → threads; update
  `docs/components/ClaudeWorkbench.Host.md`, `docs/architecture/Architecture.md`, `docs/guide/testing.md`
  (tool count), `README.md` (feature blurb + roadmap).

---

## Load-bearing couplings — do NOT break
- `RuntimeProvisioner` is shared (provisions the whole runtime skeleton) — remove only the 2 task lines; keep `planning/`.
- `sourceResize.js` is shared — remove only `attachTaskSplitter`.
- `AgentGuidance.ComposeGovernanceCard` is fetched by the sidecar at startup — edit the task bullet only.
- Thread/session lifecycle is NOT task-coupled today — the rewrite hooks in cleanly at `NewThreadAsync`.
- `TaskBoardRepositoryFactory` depended on `WorkspaceManager`/`MonitorWorkspacePaths` (AIMonitor.Core) — keep those.

## Open decisions
- **Pre-conversation stubs (Planned):** IN (agreed).
- **Naming:** RESOLVED — default `discussion-YYYY-MM-DD-N`, human-editable; no agent-generated names.
  Stable `threadId` separate from editable display `name`.
- **Thread scope of agent-notes:** workspace-wide `agent-notes\` (default) vs. per-thread subfolder — default workspace-wide; revisit if notes should archive/delete with a thread.
- **UI shape:** full tab vs. composer-header switcher — decide in Phase 3.
- **Git ↔ thread link (DEFERRED — user-flagged 2026-07-25):** beyond `acceptedEditRefs`, connect a thread to
  git state (the branch it ran on and/or the commit(s) its accepted edits landed in). Would make a thread a
  fuller "what happened" record — conversation + edits + the commit that shipped them. Not in scope for
  Phases 0–3; revisit after threads persist.
