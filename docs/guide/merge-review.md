# Merge Review

The **Merge Review** is where you decide whether a staged edit becomes real source. It's
the human gate that draws blood — nothing the agent does reaches your files until you
**Accept** here.

## When it opens

Automatically, when an agent turn **finishes** with one or more staged edits. It waits for
the turn to finish so the whole edit session is staged and it can advance through every
file in order.

## What you see

A **side-by-side diff**: **Current Source** vs. the **Proposed Candidate**, rendered by the
shared `DiffView` (green = added, red = removed, amber = modified). The same renderer is
used by the Git panel, so diffs look identical everywhere.

## The three actions

| Button | When it's available | What it does |
|---|---|---|
| **Accept Proposed** | validation isn't a hard error (or was force-approved) | Writes the reviewed staged bytes to your real file, records the decision, and (on the last file of a session) rebuilds the index |
| **Accept With Validation Override** | pre-merge validation reported errors | Accepts despite the validation failure — use only when you understand the errors |
| **Reject** | always (until decided) | Declines. Rejecting stops the whole edit session; remaining files stay pending |

## Session flow

If the agent staged several files in one edit session, the dialog **advances through them
in order**. Close the dialog anytime to stop reviewing — unresolved files stay pending and
you can come back to them.

## Why Accept is safe

The staged candidate is an **immutable, hashed snapshot**. At Accept the engine re-hashes
it and requires your real file to match the reviewed candidate — so **what you saw is
exactly what gets written**. The agent cannot alter a staged candidate after you've
reviewed it.

## After Accept

The changed file shows up as a working-tree change in the **[Git panel](git-panel.md)**,
where you can commit and push it. The solution index refreshes so later agent queries see
the new code.
