# Workspaces

A **workspace** is the watched .NET solution ClaudeWorkbench operates on. Exactly one is
active at a time.

## Selecting / switching

- On first run (or when none is configured) the **workspace picker** opens automatically,
  starting at your **user profile** folder — not wherever the app was launched from.
- Anytime, use **Select Solution** (on the workspace toolbar) to switch.
- Browse to a folder and click a `.sln` / `.slnx` under **Solutions in this folder**.
  Switching **rewrites the config the host was started with** (see below) and rebuilds the
  engine services against the new solution.

> **It must be a solution file.** The picker lists **only** `.sln` and `.slnx` — you cannot
> select a bare `.csproj` through the UI, and that is deliberate: the solution is what defines
> the workspace, so the index covers every project in it rather than one project's view of the
> world.
>
> The engine itself will accept a `.csproj` if you set `Monitor:WatchedSolutionPath` to one by
> hand (`SolutionIndexBuilder` branches on the extension and calls `OpenProjectAsync`), and it
> does index — but it is an unsupported back door, not a feature. A single project cannot see
> the projects that depend on *it*, so anything that reasons across the solution — find-all-
> references, "what breaks if I change this signature" — is answering from a partial graph. That
> is exactly the question the agent asks before a refactor, and a confidently wrong answer there
> is worse than no index.

## What happens when you select one

1. The choice is saved back to the config the host was started with — `config/appsettings.json`
   (git-ignored, per-machine) by default, or whatever `--config` pointed at. Under the
   [Launcher](deploying.md) that is the instance's own config, so instances never fight over one file.
2. The workspace **runtime** is provisioned: a skeleton under the `RuntimeRoot`, the task
   board database, and an `uploads` folder.
3. The **solution index** builds (Roslyn semantic extraction → SQLite) so the agent can
   query symbols. This can take a moment on a large solution — watch the **Indexing**
   spinner on the workspace toolbar. Reopening the app with a solution already attached
   warms the index the same way, in the background, once the host is up.

## The runtime folder

Everything the workbench owns for a watched solution lives under **`RuntimeRoot`** — the
monitor-owned *Working* mirror (candidate edits), *staged* snapshots, the SQLite index,
thread index, logs, and uploads. **None of this is your source.** Your real files are only
written on an operator Accept.

> `RuntimeRoot` resolves relative to the repo root when it isn't an absolute path. The
> Host project excludes its `runtime/` folder from compilation, so mirrored `.cs`
> candidates never get built into the app.
>
> Started from the [Launcher](deploying.md), each instance gets its own `RuntimeRoot` at
> `<workbench>\runtime\<workspace>` — same contents, one folder per watched solution.

## The agent's working directory

The agent's CWD is the **watched solution's folder** (auto-derived from the host's
`/health`, so it tracks whatever solution you selected). The agent is **read-only** there
— it reads with `Read`/`Grep`/`Glob` and changes only through governed tools.

## Per-solution config (`.claudeworkbench.json`)

Each watched solution can carry a small operator config file, **`.claudeworkbench.json`**, at the
solution root **beside the `.slnx`**. It is **committed with the repo** and travels with it — the
same root-level, shared convention as `.editorconfig` or `.vscode/settings.json`. **A missing file
means all defaults.**

| Key | What it does |
|---|---|
| `version` | Config format version (currently `1`) — lets the shape migrate later. |
| `filesTree.excludeDirectories` | Extra directory names hidden from the Source **Files** tree's *filesystem fallback*, merged with the built-in `bin`/`obj`/`.git`/`node_modules` set. Only consulted when git isn't the source of truth. |
| `git.defaultBranch` | Branch name written into a freshly-**initialized** repo. Blank/absent = the machine's git default. |

It is edited **in-app** through the **Git** tab's **Configure** button (see
[the Git panel](git-panel.md)) — host-side and operator-driven, written straight to disk, **never
through the agent** or merge review. Because it is committed, a save shows up as an ordinary
change on the Git page.

## Git-backing (optional but recommended)

If the watched solution is a git repo, the **Git** tab lets you commit and push accepted
changes so there's always a remote checkpoint. If it isn't a repo yet, the Git tab offers
to initialize one. See [the Git panel](git-panel.md).
