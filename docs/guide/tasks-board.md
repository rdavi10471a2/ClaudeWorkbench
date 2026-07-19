# Tasks Board

> **Status: currently disabled (unpublished WIP).** The Tasks tab is turned **off** in the
> UI while the thread↔task workflow is designed (see `docs/ThreadTaskWorkflow.md`). The
> board and its MCP tools still exist, but the sidecar role card is **free-flowing** —
> nothing ties a turn to a task. This page describes the *intended* feature.

A lightweight kanban board that gives the agent **durable task memory** across turns and
threads, backed by a per-workspace SQLite board.

## The board

- Cards move between states; right-click (or drag) to change state.
- Exactly **one** task is **Active** at a time (single-Active invariant).
- Each task has a description, **user notes**, and **agent notes**.

## Why it matters — task memory

The agent can read and write task context through governed MCP tools:

| Tool | What it does |
|---|---|
| `get_current_task` | Returns the **Active** task + its description, user notes, and the agent's own prior notes. |
| `list_tasks` | Lists tasks (id, number, label, state) to find one to act on. |
| `update_agent_notes` | Replaces a task's **agent notes** — the agent's durable scratchpad, persisted across turns and threads, shown to you on the task's Agent Notes pane. |

Agent notes are written to the runtime **task-memory** store
(`planning/task-memory`), **never** to your watched source.

## Intended use (when re-enabled)

1. Create a task and set it **Active** for what you're working on.
2. Work in the **Workbench** tab.
3. **When a request concerns that task** (not automatically every turn — the role card is
   opt-in), the agent loads it via `get_current_task` and records progress with
   `update_agent_notes`, so context carries to the next turn or thread.

## Why it's parked

On the first real run, the injected role card coupled *every* work turn to the Active
task, and the agent **folded an unrelated ad-hoc request into it**. Two fixes followed:
the role card was made **free-flowing/opt-in** (interim), and the Tasks tab was
**disabled** until the real fix — explicit **thread↔task binding** (save-as-task on New
Thread, task groups its threads) — is built. See `docs/ThreadTaskWorkflow.md`.
Re-enable: uncomment the Tasks `RadzenTabsItem` in `Home.razor`.
