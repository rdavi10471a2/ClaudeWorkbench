# Thread ↔ Task Workflow (design note)

Deferred feature: bind conversation threads to board tasks. **Not yet built** —
it needs design decisions (board columns, an Inbox lane for loose/unbound
threads, and the save-on-close UX).

## Intent

- On **New Thread**, prompt: **save as task / keep as discussion / discard**
  (skip the prompt when the thread is already task-linked).
- Auto-name threads `discussion-<datetime>` at creation (SDK `Options.title`),
  and offer the first-prompt summary as a one-click rename.
- Add a nullable `task_id` FK on `workflow_archived_discussions` so a task
  groups its threads.
- Record thread provenance on agent notes.
- Export the transcript to `planning/task-memory/<name>.md` to satisfy the
  discussions table's `markdown_path`.

## First-run finding (2026-07-18) — the correction this must make

On the first real run with the task board, the operator asked the agent to do
something **unrelated** to the Active task. The agent **conflated** the two: it
loaded the Active task via `get_current_task`, folded the ad-hoc request into
it, and wrote `update_agent_notes` against that task.

**Cause.** The injected governed role card couples *every* work turn to the
Active task ("at the start of a work turn call `get_current_task` … record
progress with `update_agent_notes`"). It implicitly assumes the turn's subject
*is* the Active task.

**Correction.**

1. **Explicit thread↔task binding.** A thread should know whether it is working
   a task and *which one*. Ad-hoc threads are **unbound**; `get_current_task`
   and `update_agent_notes` fire only when the work belongs to the bound task.
   "Whatever is Active" must stop being an implicit proxy for "what I'm doing."
2. **Hedge the role-card wording** in the meantime: *load task context only if
   the request relates to the current task; do not record ad-hoc work against an
   unrelated task.*

The machinery (`get_current_task` / `update_agent_notes` / the board) worked
correctly — the gap is *when* the current task is the subject, which the binding
resolves.

## Status (2026-07-19)

- **Correction 2 has landed.** The injected role card no longer couples a turn to the
  Active task: the board is now stated as *optional context, not a per-turn step* —
  free-flowing by default, `get_current_task` / `update_agent_notes` only when the
  operator's request clearly concerns a board task, and never folding an ad-hoc request
  into the Active task (`sidecar/src/index.ts`, `buildGovernanceCard`).
- **Correction 1 is still unbuilt.** There is no thread↔task binding, so nothing above
  the wording enforces it. Meanwhile the **Tasks tab is disabled** in the UI and the
  `TaskMcpTools` surface stays available to the agent, so the board is reachable by tool
  but not by operator. This note stays the design of record for the binding.
