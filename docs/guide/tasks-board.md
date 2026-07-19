# Tasks Board

A lightweight kanban board (the first tab) that gives the agent **durable task memory**
across turns and threads. Backed by a per-workspace SQLite board.

## The board

- Cards move between states; right-click (or drag) to change state.
- Exactly **one** task is **Active** at a time (single-Active invariant).
- Each task has a description, **user notes**, and **agent notes**.

## Why it matters — task memory

The agent can read and write task context through governed MCP tools:

| Tool | What it does |
|---|---|
| `get_current_task` | Returns the **Active** task + its description, user notes, and the agent's own prior notes. The agent calls this at the start of a work turn to load context. |
| `list_tasks` | Lists tasks (id, number, label, state) to find one to act on. |
| `update_agent_notes` | Replaces a task's **agent notes** — the agent's durable scratchpad, persisted across turns and threads, shown to you on the task's Agent Notes pane. |

Agent notes are written to the runtime **task-memory** store
(`planning/task-memory`), **never** to your watched source.

## How to use it

1. Create a task and set it **Active** for what you're working on.
2. Work in the **Workbench** tab. The agent loads the Active task at the start of a work
   turn and records progress in agent notes as it goes.
3. Next turn or thread, that context carries over — the agent picks up where it left off.

> Design note: binding conversation *threads* to tasks (save-as-task on New Thread,
> auto-named threads, task↔thread grouping) is a planned enhancement — see
> `docs/ThreadTaskWorkflow.md`. Today, "Active task" is the shared context; keep it
> pointed at what you're actually doing so ad-hoc requests aren't recorded against an
> unrelated task.
