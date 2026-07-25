# Conversation Threads

A **thread** is a named, resumable conversation. It replaced the old Tasks board: instead of a
kanban you maintain by hand, every conversation is saved automatically, and you name, reopen, and
delete threads from the **Threads** tab. A saved thread is a lightweight after-the-fact record —
the conversation, and (uniquely) the exact edits it produced.

## What a thread is

A thread is a pointer to a Claude Agent SDK **session** plus the human-facing metadata around it:

| Field | Meaning |
|---|---|
| `thread_id` | stable id (never changes on rename) |
| `name` | display name; defaults to `discussion-YYYY-MM-DD-N` (that day's Nth thread), editable |
| `description`, `user_note` | your free text |
| `session_id` | the SDK session |
| `cwd` | the watched-solution folder — required for a correct resume |
| `status` | lifecycle (below) |
| accepted-edit refs | provenance: the staged edits this thread landed in source |

## Lifecycle — a derived view, not a state machine

There is no discussion/task split — it's all just a thread. States are **derived** from the session,
not a board you drag cards around:

- **Active** — the thread whose session is the live one. **Computed**, never stored; single-operator
  (one Host per workspace) means there is at most one, and resuming another thread bumps the old one
  out of Active automatically.
- **Archived** — has a session, resumable, not live (the resting state).
- **Abandoned** — discarded, kept until you delete it.

The Threads tab is a **vertical board** on the left — Active pinned on top, then Archived and
Abandoned, each with its threads listed beneath — and a **details pane** on the right (resizable
splitter). Pick a thread to open it; from the details pane you can **Resume**, **rename**, edit
description/note, **Abandon/Restore**, and **hard-delete**.

## Autosave — no save button

Persistence is automatic. When the sidecar reports a session id (the new `session_started` event on
the agent's first message), the host creates the thread row and shows a toast — *"Saving thread as
…"* — no dialog. Later turns just touch it; nothing is lost if the app closes mid-conversation.

To name a conversation up front, type a name in the **"Name next thread"** field beside **New
Thread** before you start; it's applied to that conversation when it saves (blank → the default
`discussion-YYYY-MM-DD-N`). You can always rename later from the thread's details pane.

## Where it is stored

- **Thread index** — its own per-workspace SQLite database,
  `runtime\<workspace>\data\threads.sqlite`. Separate file and schema from the solution index DB and
  from the retired `board.sqlite`; thread metadata never touches the code index.
- **Transcript** — the SDK keeps the primary copy at `~/.claude/projects/<encoded-cwd>/<sessionId>.jsonl`
  (that folder also holds your Claude login, so it can't be relocated without moving auth). After each
  turn the host **mirrors** the transcript into an app-owned copy at `runtime\<workspace>\sessions\`.
- **Resume** restores that mirror back into `~/.claude` *first*, then resumes — so a thread continues
  from exactly what the app saved, even if Claude swept or compacted its own copy. (The mirror reflects
  whatever the transcript held at the last turn, so a compaction that predates the copy is reflected.)

## Provenance — what landed

A thread links to the accepted staged-edit records it produced. The sync points are the merge-review
outcomes: on the **terminal accept** (the write) the approved records are committed to the live
thread; a **reject** voids the *edit session* only (nothing written) and drops those refs — the
thread log itself is untouched.

## Hard delete reclaims disk

Delete is a hard delete: it removes the `threads.sqlite` row (and its provenance) **and** both
transcript copies — the `~/.claude` primary and the runtime mirror (transcripts run 10–28 MB). It
confirms first. Nothing here is watched source, so there is no governance gate.

## Related: the agent notes scratchpad

Distinct from the transcript (what was said) is the agent's **scratchpad** (what the agent writes for
itself). The `write_note` / `list_notes` / `read_note` / `delete_note` MCP tools are path-confined to
`runtime\<workspace>\agent-notes\` — outside watched source, ungoverned. See the governance card's
"two write destinations" note.
