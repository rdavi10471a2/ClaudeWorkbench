# Spec: Rehydrate the visible transcript on resume

Status: **DESIGN ONLY (2026-07-30). Not built.** Validated against real data (see §7).

## 1. Problem

Resuming a conversation restores the agent's *context* but leaves the chat pane **blank**. The
`Transcript` the UI renders is computed live from the sidecar's in-memory event ring
(`SidecarOperatorConsole.Transcript` → `stream.SnapshotEvents()`), which starts empty on a resumed
run. So the model "remembers" but the operator sees nothing — a real gap between "the agent knows"
and "the operator can see" (see [thread reload path], `ConversationService.PrepareResume` +
`ClaudeTranscriptStore.RestoreFromMirror`).

The agent cannot bridge this itself: its MCP surface is path-confined (watched source read-only +
the agent-notes dir), and `~/.claude` is outside both, so it cannot read its own transcript.

**Goal:** on resume, repaint the last **50 interactions** into the chat pane so the visible history
matches what the agent now holds in context. "Interaction" = one **human input** OR one **agent
reply**. Tool calls (and their results/images) are **rendered** but **do not count** toward the 50.

## 2. Scope

In scope: parse the app-owned mirror JSONL into `TranscriptEntry` items, trim to the last-50-counted
window (tool calls included), and make the chat pane show them on resume.

Out of scope: cross-machine resume; editing history; "load earlier than 50"; changing how live turns
stream. The mirror/restore plumbing already exists and is unchanged.

## 3. Data sources & shapes (verified against a real 32,300-line transcript)

**Source of truth = the RUNTIME mirror, never `~/.claude`.** Rehydration reads the app-owned mirror
resolved via `IConversationWorkspace.SessionsDirectory` (e.g.
`runtime\<ws>\watched-solutions\<slug>\sessions\YYYY-MM-DD-N.jsonl`), the exact file
`ConversationService.MirrorTranscript` writes and `PrepareResume` restores from. Do **not** read the
SDK's `~/.claude` copy for rehydration: it lives outside the app, may be swept/compacted, and the
runtime mirror is authoritative by design. The reader takes an absolute mirror path — it never
computes a `~/.claude` location.

The mirror is a byte copy of the **Claude Agent SDK / claude CLI JSONL** format (one JSON object per
line), NOT the sidecar event shape. Per line, the fields that
matter: `type`, `message` (`role` + `content`), `timestamp` (ISO-8601), `uuid`, `parentUuid`,
`isSidechain`, `isMeta`, `sessionId`, `cwd`.

Top-level `type` values observed: `assistant`, `user`, plus non-message noise
(`queue-operation`, `ai-title`, `last-prompt`, `attachment`, `file-history-snapshot`,
`file-history-delta`). Only `assistant`/`user` carry transcript content.

`message.content` is either a **string** or an **array of blocks**:
- assistant → array of `text` and `tool_use` (`name`, `input`, `id`) blocks.
- user (human) → a **string** (plain text the operator typed).
- user (tool output) → array of `tool_result` blocks (`tool_use_id`, `content`, `is_error`) — this is
  the SDK re-injecting tool results under the `user` role. **These are not human input.**
- user (pasted media) → array with `image` blocks.

## 4. Reconstruction → `TranscriptEntry`

Map each JSONL line to zero-or-more `TranscriptEntry` (`Console.TranscriptKind`:
User / Assistant / ToolCall / Image / Error), mirroring the live mapping in
`SidecarOperatorConsole.Transcript` so restored and live rendering are identical:

| JSONL source | → TranscriptEntry | Counts toward 50? |
|---|---|---|
| assistant `text` block (non-empty) | `Assistant` | **yes** |
| assistant `tool_use` block | `ToolCall` (label via `ApprovalFormatter.ShortLabel(name, input)`) | no |
| user role, **string**, not synthetic | `User` | **yes** |
| user role, `tool_result` block | render alongside its call (or drop; see §6) | no |
| user role, `image` block, or a Read-of-image tool_use | `Image` | no |
| synthetic user string (see below) | drop | no |

Exclusions (all confirmed present in real data):
- `isSidechain: true` → skip (subagent/Task internals; not the main thread).
- `isMeta: true` → skip.
- **Synthetic user strings** — dropped as non-human: leading `<task-notification>`,
  `<system-reminder>`, `<local-command-stdout>`, `<local-command-caveat>`, `<command-name>`,
  `<command-message>`, `<command-args>`, and `[Request interrupted…]`. (51 such strings existed in the
  sample; 4 fell inside the last-50 window.) **Open question §8:** a `/slash-command` is
  human-*initiated* — decide whether to keep it as `User` or drop with the rest.
- **Dedupe by `uuid`** (keep first) — auto-compaction rewrites history and can repeat uuids; dedupe
  keeps the linear view stable.

Ordering: file append-order is chronological and is what the live view effectively shows; use it
directly rather than walking the `parentUuid` DAG.

## 5. Window selection (the "last 50" rule)

```
COUNTED = { User, Assistant }
walk entries from the end backward, counting only COUNTED kinds;
stop when the count reaches 50 (or at index 0 if fewer exist);
the window = entries[thatIndex ..end]  — rendered in full, tool calls included.
```

This yields "the last 50 human/agent turns, with every tool call, result and image that occurred
within that span." Trim at parse time so we hold ~the window (≈134 entries in the sample), not all
9,474.

## 6. Integration (where the code changes go — no UI markup change needed)

The chat pane already renders `IOperatorConsole.Transcript`; the cleanest seam is to prepend a
restored prefix to that computed list.

1. **New parser** `Conversations/ClaudeTranscriptReader.cs`: `IReadOnlyList<TranscriptEntry>
   ReadWindow(string mirrorPath, int counted = 50)`. Pure/offline, no app state; unit-testable
   against fixture JSONL. Owns §4–§5.
2. **`SidecarOperatorConsole`** gains a `restoredPrefix` field + `SetRestoredPrefix(entries)` /
   `ClearRestoredPrefix()`. Change the `Transcript` getter to return
   `restoredPrefix.Concat(<current live projection>)`. Live turns after resume append cleanly (the
   prefix is strictly pre-resume history; `SnapshotEvents()` holds only this run's new events, so no
   duplication).
3. **`ConversationsDialog.ResumeAsync`**: after `PrepareResume` returns the sessionId and before/after
   `Sidecar.ResumeAsync`, call `reader.ReadWindow(mirrorPath)` and `console.SetRestoredPrefix(...)`.
   The mirror path is `Path.Combine(Workspace.SessionsDirectory, thread.TranscriptFile)` (already
   resolved in `PrepareResume`; consider returning it or exposing it).
4. **Clear on divergence**: `NewThreadAsync`, and any fresh (non-resumed) `session_started`, must
   `ClearRestoredPrefix()` so a new conversation doesn't inherit the old one's history.
5. `tool_result` handling: the live view does **not** emit a distinct entry for tool *results* (it
   shows the `ToolCall` line only). Match that — drop `tool_result` lines in reconstruction so
   restored and live look identical. (The table's "render alongside" is the alternative if we ever
   show results inline.)

## 7. Feasibility result (prototype, offline, no app changes)

Ran the exact §4–§5 algorithm over two real files, including a **runtime mirror** (per §3):

Large `~/.claude` transcript `3bdc…7e14a1a8cb83.jsonl` (used to stress the last-50 trim):
- 32,300 lines → 9,474 reconstructed entries → **3,165 counted interactions** available
  (595 User, 2,570 Assistant; plus 3,114 ToolCall, 3,113 ToolResult, 31 Image, 51 Injected).
- Last-50 window = **exactly 50 counted** (34 Assistant + 16 User) across **134 rendered** entries
  (40 ToolCall, 39 ToolResult, 1 Image, 4 Injected-if-kept). Tail render faithful.

Real runtime mirror `CalculatorSample/.../sessions/2026-07-30-8.jsonl` (the actual read path):
- 115 lines → 84 entries → **22 counted** (3 User, 19 Assistant; 31 ToolCall, 31 ToolResult).
- Fewer than 50 counted → window correctly = all 84 entries (start-at-0 fallback). Governed edit-loop
  MCP calls (`replace_text_in_file`, `stage_candidate_for_review`, `complete_edit_plan`) render
  in-place, uncounted, as designed.

**Conclusion: reliable reproduction confirmed on the real runtime-mirror path.**

## 8. Open questions / edge cases

- **Slash commands**: keep `/command` user strings (human-initiated) or drop with synthetics? Leaning
  keep-as-`User`, since the operator did type them.
- **Timestamps**: JSONL `timestamp` is ISO; the live `TranscriptEntry.Time` is
  `yyyy-MM-dd HH:mm:ss` local. Parse+reformat for visual parity.
- **Perf**: a full parse of a 32k-line / up-to-28 MB file is one-time on the Resume click (busy
  spinner already shows). Acceptable now; a reverse tail-scan (stop after 50 counted found) is the
  optimization if it ever bites.
- **Images**: pasted-image and Read-of-image render via `/local-file`; a swept image file just shows
  its path (same as the live view's fallback). No new failure mode.
- **"Load earlier"**: capped at 50 by design; a later "show earlier" affordance is additive and needs
  no schema change.
- **Compaction gaps**: dedupe-by-uuid handles repeats; a compaction *summary* line (if the SDK writes
  one) would render as a normal assistant/user entry — acceptable, it's real context.

[thread reload path]: ../../src/ClaudeWorkbench.Host/Conversations/ConversationService.cs
