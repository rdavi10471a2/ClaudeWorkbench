# The Governed Loop

What actually happens when you ask Claude to change something — and where **you** are in
control. This is the operator's view; the engineering view is in the
[architecture doc](../architecture/Architecture.md#3-the-governed-edit-loop).

## The one rule

> The agent proposes and stages. **You** accept. Only the accept writes real source.

Claude is **read-only** to your watched solution. By default it cannot run a shell, cannot
`Write` or `Edit` files, and cannot push to git on its own. Everything it wants to change
goes through governed tools that you approve. (The **Optional tools** in
[Settings](settings-and-usage.md) can hand it `Bash`/`Write`/`Edit` — that deliberately
steps outside this gate, which is why they're off by default.)

## Step by step

1. **You prompt.** Type a request in the **Workbench** tab.
2. **Claude works.** It reads your code (`Read`/`Grep`/`Glob` and index tools) and, to
   make a change, edits a **monitor-owned Working copy** — never your real file.
3. **The gate pauses.** Any tool that could reach your source or the review queue
   **pauses** and shows up as a **gate request**. You **Approve** or **Reject** each one.
   (You can turn on **Auto-approve** per thread to let candidate edits proceed without a
   per-call prompt — the merge-review Accept still gates the real write.)
4. **Claude stages.** When it's happy, it stages each changed file — an immutable,
   hashed snapshot. Then it **stops** and tells you it's staged. It never accepts.
5. **You review.** When the turn ends with staged edits, the **Merge Review** dialog
   opens with a side-by-side diff. You **Accept** or **Reject**.
6. **Accept validates, then writes source.** Accepting first re-checks the staged
   snapshot and (on the last file of the session) runs the authoritative build. Only if
   that passes are the reviewed bytes written to your real file and the decision
   recorded; the solution index then rebuilds so downstream truth is fresh. A failed
   build is a hard stop — nothing is written.

## Why the gate is trustworthy

It's **code, not a prompt.** The pause is enforced by the sidecar's `canUseTool` gate and
the engine's staging checks — not by asking the model nicely. And accepting is safe
because the staged snapshot is **immutable and hashed**: at accept time — **before** any
byte reaches your source — the engine re-hashes it and requires it to match what you
reviewed exactly. What you saw is what gets written, or nothing is.

## What you'll see in the UI

| Moment | Where |
|---|---|
| Gate request (Approve/Reject a tool) | Workbench tab — the gate/approval prompt |
| Agent asked you a question | The questions dialog (choice cards + free-text) |
| Staged edits to review | Merge Review dialog (opens automatically) — [details](merge-review.md) |
| The changed file afterward | Git tab (commit/push) — [details](git-panel.md) |
