# 0004 — Git via argv, never a shell

**Status:** Accepted · **Date:** 2026-07

## Context

The workbench git-backs the watched solution and — critically — exposes **gated git MCP
tools** so the agent can commit/push/branch by prompt. An agent-supplied commit message or
branch name is untrusted input. If git were invoked through a shell string, a message like
`"; git push --force #` or a branch named `$(rm -rf ~)` would be a command-injection hole.

## Decision

`GitService` launches the **`git` executable directly** via
`System.Diagnostics.ProcessStartInfo` with:

- `UseShellExecute = false` — **no shell** (`cmd.exe` / `/bin/sh`) in the path.
- **`ArgumentList`** (the per-argument collection passed straight through as `argv`), **not**
  `ProcessStartInfo.Arguments` (the single string that gets re-parsed).
- `--` before file paths on `add`/`restore`/`clean` so a path can't be read as a flag.

Every call passes a discrete array, e.g. `["commit", "-m", message]`, `["push", "-u",
remote, branch]`. The message/branch is one `argv` slot — a literal string git receives as
**data**.

## Consequences

- Command injection is **structurally impossible**, not filtered-against. There is no shell
  to interpret metacharacters, and the `Arguments`-string re-parse is bypassed.
- This is what makes the gated `git_*` MCP tools safe to hand an agent (each still pauses at
  the operator gate — see [0001](0001-the-gate-is-code.md)).
- `GitService` is covered by `GitServiceTests` (hermetic; push tested against a local bare
  repo, no network).

See: [ClaudeWorkbench.Host](../components/ClaudeWorkbench.Host.md#git-integration),
[git panel guide](../guide/git-panel.md).
