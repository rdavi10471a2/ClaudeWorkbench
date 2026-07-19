# The Git Panel

Git-back your watched solution so accepted edits become real, pushable checkpoints — and
optionally let Claude do git **by prompt**, still under your gate.

Everything here is **host-side and operator-authorized**. The agent never runs a shell;
git is executed by launching the `git` binary directly with an argument array (no shell,
so command injection is structurally impossible).

## The panel (manual, operator clicks)

Open the **Git** tab. Left panel + a side-by-side diff on the right.

- **Branch bar** — the current branch, a dropdown to **switch**, **＋ New** to create one,
  `→ origin/main`, and `↑`/`↓` ahead/behind badges.
- **Fetch / Pull / Push** — Push is deliberate and operator-driven.
- **Staged Changes** / **Changes** groups — hover a file for **stage (＋) / unstage (−) /
  discard (✕)**.
- **Click a file** → the diff opens on the right (same `DiffView` as merge review; before
  is the repository/HEAD side, after is the working tree/staged side).
- **Commit** — commits the staged set; if nothing is staged it stages everything and
  commits (there's a **Stage All** button too). The message box is pre-filled with a draft
  from the changed filenames — edit it.
- **History** (collapsible) — recent commits.
- **Hide Panel** — collapse the left panel to give the diff the full width; drag the
  splitter to resize.

If the watched folder **isn't a git repo**, the panel offers **Initialize Git
repository** instead. If `git` isn't on PATH, you get a clear "Git is not available"
warning.

## Prompt-driven git (the agent, gated)

You can also just *ask*: **"commit the changes and push."** Claude calls the git MCP
tools, and the mutating ones **pause at your operator gate**:

| Tool | Gated? |
|---|---|
| `git_status`, `git_diff`, `git_log`, `git_list_branches` | No (read-only, auto-allow) |
| `git_commit`, `git_push`, `git_create_branch`, `git_switch_branch` | **Yes — pause for your approval** |

So *nothing reaches GitHub without your click*, whether you drive git from the panel or by
prompt.

## Typical flow

Accept an edit in [merge review](merge-review.md) → the file shows under **Changes** →
**Commit** (edit the message) → **Push**. Watch `↑1` clear as the push lands.
