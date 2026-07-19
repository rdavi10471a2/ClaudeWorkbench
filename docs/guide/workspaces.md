# Workspaces

A **workspace** is the watched .NET solution ClaudeWorkbench operates on. Exactly one is
active at a time.

## Selecting / switching

- On first run (or when none is configured) the **workspace picker** opens automatically.
- Anytime, use **Select Solution** (top-right) to switch.
- Point it at a `.sln` (or project). Switching **rewrites** `config/appsettings.json` and
  rebuilds the engine services against the new solution.

## What happens when you select one

1. The choice is saved to `config/appsettings.json` (git-ignored, per-machine).
2. The workspace **runtime** is provisioned: a skeleton under the `RuntimeRoot`, the task
   board database, and an `uploads` folder.
3. The **solution index** builds (Roslyn semantic extraction → SQLite) so the agent can
   query symbols. This can take a moment on a large solution.

## The runtime folder

Everything the workbench owns for a watched solution lives under **`RuntimeRoot`** — the
monitor-owned *Working* mirror (candidate edits), *staged* snapshots, the SQLite index,
task board, logs, and uploads. **None of this is your source.** Your real files are only
written on an operator Accept.

> `RuntimeRoot` resolves relative to the repo root when it isn't an absolute path. The
> Host project excludes its `runtime/` folder from compilation, so mirrored `.cs`
> candidates never get built into the app.

## The agent's working directory

The agent's CWD is the **watched solution's folder** (auto-derived from the host's
`/health`, so it tracks whatever solution you selected). The agent is **read-only** there
— it reads with `Read`/`Grep`/`Glob` and changes only through governed tools.

## Git-backing (optional but recommended)

If the watched solution is a git repo, the **Git** tab lets you commit and push accepted
changes so there's always a remote checkpoint. If it isn't a repo yet, the Git tab offers
to initialize one. See [the Git panel](git-panel.md).
