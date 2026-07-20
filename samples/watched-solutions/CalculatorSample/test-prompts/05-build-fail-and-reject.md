# 05 — Reject voids the session (and how to force a build failure)

**Tests:** ADR-0005 — a single Reject voids the WHOLE edit session and writes nothing, even
files you already accepted. Also shows where the GATE-2 build failure surfaces.

## Prompt

Run **04** (or **03**) so a multi-file session is staged. Then in the Merge Review dialog:

1. **Accept** the first file (it's recorded as approved — nothing is written yet).
2. **Reject** a later file.

**Expect:** the review ends, the session is voided, and **nothing** reaches watched source —
not even the file you accepted in step 1. Confirm the source files are unchanged (Git tab
shows no changes) and the agent is told the session was rejected.

## Forcing a build failure (optional)

The accept-time GATE-2 build is the authoritative check. To see it fail on purpose, stage a
change and then, if a candidate compiles clean but you want to prove the gate, use
**Accept With Validation Override** only after the banner reports errors — the override path
exists exactly so a known-bad merge is a deliberate, logged choice, never an accident.
