# The Git Page

A **read-only review** of what has been done to your watched solution — the uncommitted
working-tree changes and the full commit history, each click-through to a side-by-side
diff — plus a small set of **operator-only** write actions (branch, commit, push, merge).

Everything here is **host-side and operator-authorized**. The agent never runs a shell;
git is executed by launching the `git` binary directly with an argument array (no shell,
so command injection is structurally impossible).

Bringing the agent's proposed edits *into* your source is the job of
[merge review](merge-review.md), not this page. This page is the rear-view mirror over
the result.

## Review (the focus — read-only)

Open the **Git** tab. Left review panel + a side-by-side diff on the right.

- **Uncommitted changes** — every changed file in the working tree. **Click a file** → its
  diff opens on the right (**HEAD** on the left, **Working tree** on the right). No
  stage/discard controls — this is review, not source-control mechanics.
- **History** — recent commits, newest first. **Click a commit** to expand its changed
  files; **click a file** → that commit's diff (**`<hash>~1`** on the left, **`<hash>`** on
  the right). This is how you review exactly what a commit did.
- The diff uses the same `DiffView` renderer as merge review (conventional
  old-left/new-right orientation; merge review flips it to put the proposal on the left).
- **Hide Panel** collapses the review list to give the diff full width; drag the splitter
  to resize.

If the watched folder **isn't a git repo**, the page offers **Initialize Git repository**
instead. If `git` isn't on PATH, you get a clear "Git is not available" warning.

## Write actions (operator-only, in the toolbar)

These are the operator's alone. They call the git service **directly** — never through the
agent, never through MCP — and the outward or irreversible ones **confirm first**:

| Action | Confirms? | Notes |
|---|---|---|
| **Commit** | no | Opens a message box; commits the whole working tree (message pre-drafted from the changed filenames). |
| **＋ New** branch | no | Create and switch to a new branch. |
| Branch **switch** (dropdown) | yes | Changes the working-tree files. |
| **Fetch** | no | Updates remote-tracking refs so ahead/behind is accurate; touches nothing of yours. |
| **Pull** | yes | Fast-forward only. |
| **Push** | yes | Nothing reaches GitHub without your click. |
| **Merge to main** | yes | Merges the current branch into `main` (`--no-ff`). Requires a clean tree; on conflict it **aborts and returns you to the feature branch**. Disabled while already on `main`. |

## The agent's git access (read-only)

You can ask Claude about the repo — **"what changed in the last commit?"**, **"show me the
diff of `Foo.cs`"** — and it answers using read-only git MCP tools:

| Tool | Access |
|---|---|
| `git_status`, `git_diff`, `git_log`, `git_list_branches` | Read-only (auto-allow) |

There are **no git write MCP tools**. The agent can *see* the repository to reason about
it, but it cannot commit, push, branch, or merge — those happen only when *you* click in
this page. Because there is no git-write tool to call, auto-approve ("approve all") has
nothing to bypass: git writes are never automated.

## Typical flow

Accept an edit in [merge review](merge-review.md) → the file shows under **Uncommitted
changes** (review its diff) → **Commit** (edit the message) → **Push**, or **Merge to
main**. Watch `↑1` clear as the push lands.
