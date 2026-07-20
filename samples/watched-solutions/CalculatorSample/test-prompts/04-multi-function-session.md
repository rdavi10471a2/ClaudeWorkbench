# 04 — Multi-function session (the big one)

**Tests:** the full atomic session — a top-level rewrite, edits to an existing class, AND a
brand-new folder/namespace/file, all declared in ONE edit session and merged to source as a
single unit on the final Accept. This is the multi-file, new-file-plus-edits workout.

Run this on a **pristine** tree (Reset samples first). Expect ONE Merge Review that advances
through every file, then one write ("N files written to source together"), one build, one
index refresh.

## Prompt

Rewrite main to not use top level statements.

Update Calculator to support a running total and a single memory location via a mode switch.

Add a folder and a namespace named calculus and an implementation of Newton's method with
any support that it needs.
