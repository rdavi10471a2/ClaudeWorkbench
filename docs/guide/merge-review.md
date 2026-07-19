# Merge Review

The **Merge Review** is where you decide whether a staged edit becomes real source. It's
the human gate that draws blood — nothing the agent does reaches your files until you
**Accept** here.

## When it opens

Automatically, when an agent turn **finishes** with one or more staged edits. It waits for
the turn to finish so the whole edit session is staged and it can advance through every
file in order.

## What you see

A **side-by-side diff** of the **Proposed Candidate** (left) against your **Current
Source** (right), rendered by the shared `DiffView` (green = added, red = removed,
amber = modified). The same renderer is used by the Git panel, so diffs look identical
everywhere. Review is entirely in-app — there is no external diff tool to install or
launch.

## The three actions

| Button | When it's available | What it does |
|---|---|---|
| **Accept Proposed** | pre-merge validation isn't a hard error (or was force-approved) | Re-checks and re-hashes the staged candidate, runs the authoritative build on the **last** file of a session, and only then writes the reviewed bytes to your real file, records the decision, and rebuilds the index |
| **Accept With Validation Override** | pre-merge validation reported errors for this record | Accepts despite the validation failure — use only when you understand the errors |
| **Reject** | always (until decided) | Declines. Rejecting stops the whole edit session; remaining files stay pending |

## Nothing is written until the build passes

Accept validates **before** it writes, never after. On the last pending file of an edit
session the workbench compiles the whole session's accepted set (a real `dotnet build`,
not the fast readiness check). If that build fails, the accept **stops there**: you get
the error count and the first diagnostics, and **not a single byte reaches your source** —
there is no half-applied change to clean up. Fix the code (ask Claude to re-stage) and
accept again.

The same is true of the other guards: if the staged record was superseded, already
decided, or its bytes changed since staging, Accept refuses and writes nothing. The write
itself goes through a temp file and an atomic rename, so an interrupted accept can't leave
your file truncated.

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
