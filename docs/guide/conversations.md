# Conversations

A **conversation** is a named, resumable chat with Claude. It replaced the old Tasks board: instead of
a list you maintain by hand, every conversation is saved automatically, and you name, reopen, and
delete them from the **Conversations** modal — opened with **History** on the composer's conversation
bar. A saved conversation is a lightweight after-the-fact record: the chat, and (uniquely) the exact
edits it produced. The list is simply newest-first with the current one pinned on top — not a board or
kanban; there are no columns and nothing to drag.

It is entirely **host-side plumbing**: the agent has no tools for it and no awareness of it.

## What a conversation is

A pointer to a Claude Agent SDK **session** plus the human-facing metadata around it:

| Field | Meaning |
|---|---|
| `conversation_id` | stable id (never changes on rename); the DB primary key |
| `name` | display name; defaults to `conversation-YYYY-MM-DD-N` (that day's Nth), editable |
| `description`, `user_note` | your free text |
| `session_id` | the SDK session (null until the first turn) |
| `cwd` | the watched-solution folder — required for a correct resume, and to warn if you reopen one from a different folder |
| `status` | `archived` (see below) |
| `transcript_file` | the mirror's readable filename (dated) — the DB maps it back to the conversation |
| accepted-edit refs | provenance: the staged edits this conversation landed in source |

## Two states, not a state machine

Disposal is a hard **Delete**, so there is no soft/"abandoned" middle state. There are just two buckets:

- **Current** — the conversation whose session is live (or the just-started one before its first turn).
  **Computed**, never stored; single-operator (one Host per workspace) means there is at most one, and
  starting or resuming another makes it Current automatically.
- **Archived** — every other saved conversation (the resting state).

The Conversations modal is master/detail: a newest-first list on the left with **Current** pinned on
top, and a **details pane** on the right (resizable splitter). The current conversation is selected by
default. From the details you can **rename**, edit description/note, **Resume** (closes the modal so you
continue on the Workbench), and **Delete**. The **current** conversation can't be deleted (a toast says
so — leave it with New or switch first). Resuming a conversation whose `cwd` differs from the current
workspace warns first, so a copied/relocated conversation can't quietly continue against the wrong code.

## Starting one — the New popup

**New** (on the conversation bar) opens a popup that, in one place:

- offers to **keep or discard the conversation you're leaving** *if it's still on its default name*
  (keep, optionally renaming it; or discard, which reclaims its runtime row + mirror so the list
  doesn't fill with junk defaults), and
- lets you **name the new one** up front.

Both fields are prefilled with default names, so nothing is ever blank. New is disabled while a turn —
or a previous New's reset — is in flight.

## Autosave — no save button

Persistence is automatic and immediate. A conversation is a real, named row the moment **New** is
confirmed (so it's nameable *before* its first turn); its first turn simply adopts that row. Later
turns touch it; nothing is lost if the app closes mid-chat.

## Where it is stored

- **Conversation index** — its own per-workspace SQLite database,
  `runtime\<workspace>\data\conversations.sqlite`. Separate file and schema from the solution index DB
  and from the retired `board.sqlite`; conversation metadata never touches the code index.
- **Transcript** — the SDK keeps the primary copy at `~/.claude/projects/<encoded-cwd>/<sessionId>.jsonl`
  (that folder also holds your Claude login, so it can't be relocated without moving auth). After each
  turn the host **mirrors** the transcript into an app-owned copy at `runtime\<workspace>\sessions\`,
  under a plain dated filename (`YYYY-MM-DD-N.jsonl`, no GUID — the DB maps it back).
- **Resume** restores that mirror back into `~/.claude` *first*, then resumes — so a conversation
  continues from exactly what the app saved, even if Claude swept or compacted its own copy.

None of these live in the agent's scratchpad (`agent-notes\`), which is path-confined and separate; the
agent's tools can't reach the conversation store.

## Provenance — what landed

A conversation links to the accepted staged-edit records it produced. The sync point is the terminal
**Accept** in merge review (the write); a **reject** voids the *edit session* only (nothing written) and
drops those refs — the conversation log itself is untouched.

## Hard delete reclaims disk

Delete is a hard delete behind a confirm dialog: it removes the `conversations.sqlite` row (and its
provenance) **and** both transcript copies — the runtime mirror (reclaimed first, always) and the
`~/.claude` primary (best-effort; it's outside our runtime). Transcripts run 10–28 MB. Nothing here is
watched source, so there is no governance gate.

## Related: the agent notes scratchpad

Distinct from the transcript (what was said) is the agent's **scratchpad** (what the agent writes for
itself). The `write_note` / `list_notes` / `read_note` / `delete_note` MCP tools are path-confined to
`runtime\<workspace>\agent-notes\` — outside watched source, ungoverned. See the governance card's
"two write destinations" note.

This scratchpad — not Claude Code's built-in `~/.claude` file-memory — is the agent's memory here. The
built-in memory needs the native `Write` tool, which is denied by default (see the deny-by-default
workspace in [../architecture/Architecture.md](../architecture/Architecture.md)); so the agent cannot
write to `~/.claude`, and every agent write funnels to `agent-notes\` or the staged-source workflow.
That's the governance model working as intended, not a gap.
