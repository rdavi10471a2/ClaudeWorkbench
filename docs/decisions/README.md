# Decisions (ADRs)

Short records of the architectural decisions that shaped ClaudeWorkbench — the "why"
behind the structure. Numbered; append new ones, don't rewrite history.

| # | Decision |
|---|---|
| [0001](0001-the-gate-is-code.md) | The gate is code, not policy prose (deny-by-default; enforcement in `canUseTool` + staging, not the system prompt) |
| [0002](0002-two-process-node-sidecar.md) | Two processes: a Node sidecar for the Node-only Claude Agent SDK; in-proc HTTP MCP |
| [0003](0003-in-app-review-retire-winmerge.md) | In-app DiffPlex merge review; retire WinMerge |
| [0004](0004-argv-git-no-shell.md) | Git via `argv` (`ArgumentList`), never a shell — injection is structurally impossible |
| [0005](0005-edit-session-is-atomic.md) | The edit session is atomic: per-file Accept is an approval, the write happens once at the terminal accept, and a single Reject voids the whole session |
| [0006](0006-some-tools-are-never-auto-approvable.md) | Delete and rename are never auto-approvable — auto-approve must never be the reason something irreversible happened |
