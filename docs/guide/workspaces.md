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
task board, logs, and uploads. **None of this is your source.** Your real files are only
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

## Git-backing (optional but recommended)

If the watched solution is a git repo, the **Git** tab lets you commit and push accepted
changes so there's always a remote checkpoint. If it isn't a repo yet, the Git tab offers
to initialize one. See [the Git panel](git-panel.md).
