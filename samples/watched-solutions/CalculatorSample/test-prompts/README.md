# Test prompts — CalculatorSample

Copy-paste prompts for exercising the ClaudeWorkbench governed edit loop against this
sample, so you don't have to retype them. They live inside the sample, so a `publish-live`
carries them into the install (both `samples\` and the `samples-golden\` backup), and the
Launcher's **Reset samples** button restores the whole thing — prompts included — between
runs.

## How to use

1. In the Launcher, **Reset samples** (restores this solution to pristine), then reindex and
   start a **New Thread** in the app.
2. Open a prompt file below, copy the text under **## Prompt**, paste it into the Workbench
   composer, and Submit.
3. Review each staged file in the Merge Review dialog and Accept (the whole session writes
   as a unit on the final Accept).

Run them **sequentially** (01 → 05) for a graded workout, or just fire **04** on a pristine
tree for the big multi-file, new-file-plus-edits test in one shot.

## What each one exercises

| File | Shape | Exercises |
|---|---|---|
| `01-add-method.md` | 1 file, surgical | typed symbol edit (`add_method`), single-file session, one Accept |
| `02-new-file.md` | 1 new file | `new_file` → `submit_file`, brand-new file into the namespace |
| `03-cross-file-move.md` | 3 files | move code OUT of one file into a new one + fix the caller — declare-all-files up front |
| `04-multi-function-session.md` | 4 files | the big one: rewrite + edits + new folder/namespace, all one session, unit merge |
| `05-build-fail-and-reject.md` | 2 files | GATE-2 build failure at Accept, and reject-voids-the-session |
| `06-convert-tls-and-document.md` | 1 file + a doc | convert top-level statements to `Program.Main` (makes the entry point a real symbol → caller edges appear), then the doc-gen currency guardrail: won't document from a stale index |

## Adding your own

Drop another `NN-title.md` here following the same shape (a `## Prompt` section with the
copy-paste text). It ships on the next publish. Keep prompts phrased as an operator request
("Add…", "Move…", "Rewrite…"), not as instructions to specific tools — the agent chooses the
tools.
