# Code Review — Git surface, Launcher, and the accept/lifetime changes

- **Date:** 2026-07-20
- **Reviewer:** Claude (Opus 4.8), read-only review — no code was edited as part of this review.
- **Scope:** the delta `7c0b762..1ad66a9` on `main` (the ~40-commit two-day push: ADR-0004 argv-git, ADR-0005 session-atomic accept/reject, ADR-0006 never-auto-approve, the Git panel + `GitService`/`GitMcpTools`, the WinForms Launcher with Job Objects, browser-lifetime shutdown, single-source `AgentGuidance`, DiffView consolidation, and the AIMonitor.Cli / smoke-runner retirement).
- **Method:** six independent read-only reviewer passes (line-by-line, removed-behavior, cross-file/DI, safety/governance, process-lifetime, cleanup/efficiency), deduped and verdict-tagged. 6 CONFIRMED / 9 PLAUSIBLE.
- **Status:** findings only. Nothing here has been fixed or committed beyond this document.

## Headline

The git surface is built the right way structurally — **argv, `UseShellExecute=false`, no shell string (ADR-0004)** — so classic shell injection is structurally impossible, and the read tools are genuinely read-only. But the feature went past read-only into **mutating** git, and the auto-approve exemption set (ADR-0006) was **not** extended to match. That is the one regression that matters (finding #1): with auto-approve ON, `git_switch_branch` rewrites watched-tree files and `git_push` publishes to the remote with no gate anywhere — re-opening watched-source mutation outside the operator Accept.

## Findings (most severe first)

### 1. Mutating git tools escape auto-approve — `sidecar/src/gate.ts:53` — CONFIRMED
`git_push` / `git_switch_branch` / `git_commit` / `git_create_branch` are gated but absent from `NEVER_AUTO_APPROVED_TOOLS`. With auto-approve ON, `index.ts` auto-allows them: `git_switch_branch` overwrites watched-tree files (a source write that skips refresh/stage/GATE-2/Accept), and `git_push` publishes to the remote. ADR-0006's "safe because Accept gates the bytes" argument does not cover push (remote publication) or switch (direct tree rewrite). **Fix:** add these tools to `NEVER_AUTO_APPROVED_TOOLS` (or a parallel "not Accept-gated" set checked before the auto-approve branch).

### 2. Partial terminal-write breaks session atomicity — `EngineReviewWorkflow.cs:231` — CONFIRMED
If file 1's write succeeds (stamped `WrittenAtUtc`) but file 2's throws, file 1 keeps `Decision='approved'`. On retry it is excluded from both the write set and decision-recording, so its bytes sit on disk unrecorded and unreindexed. On Reject it is not rolled back, yet the agent is told "none written to watched source" — false. Undermines ADR-0005.

### 3. Staged-record store read-modify-write race — `WorkflowEditService.cs:1153` — CONFIRMED
`Get/LoadStagedRecord` returns a clone under the lock then releases it; callers mutate outside the lock and `SaveStagedRecord` overwrites the whole object. Concurrent stage (MCP thread) + accept (Blazor circuit thread) on the same record → last-writer-wins drops either the supersede marker (two active candidates for one file) or the operator's approval. The field comment wrongly claims the single lock makes this safe.

### 4. GATE-2 build-failure + force-approve path is untested — `tests/integration/AIMonitor.Integration.Tests/EngineReviewSessionAtomicityTests.cs:24` — CONFIRMED
Every atomicity test uses compiling fixtures; the one compile-error test stops at `ValidateStaged` and never calls `Accept`. The branch that blocks a build-failing terminal accept (and its force-approve override) — in the sole writer of watched source — has no coverage. A refactor that inverts/short-circuits it, or reorders the write above the build check, keeps the whole suite green while writing build-breaking source. **Suggested:** an integration test driving `Accept` through a genuinely build-failing session, asserting both the `overrideAvailable` block and that `forceApproveValidation:true` then writes.

### 5. Browser-lifetime shutdown fires mid-turn — `BrowserPresenceTracker.cs:28` — CONFIRMED
The ~3s shutdown grace is far under Blazor's ~3-min reconnect window and keys only on circuit count. With `CWB_EXIT_WITH_BROWSER=1`, a >3s blip / refresh / sleep on the last tab arms the timer, `StopApplication()` runs, and `SidecarProcessHost` kills the sidecar tree — aborting an in-flight turn and losing staged-but-unrecorded work. Debounce is under-sized and ignores in-flight agent work.

### 6. Concurrent index rebuilds race the SQLite index — `WorkspaceManager.cs:91` — PLAUSIBLE
`ProvisionAsync` rebuilds with no mutual exclusion. Startup fires a background rebuild; `/review/warmup` (or a Source-tab rebuild click) calls it again while in flight. Two `RebuildAsync` writers on one index DB → "database is locked" / torn index. (`IndexRebuildStatus` is a counter precisely because overlap is expected.)

### 7. Fire-and-forget startup rebuild abandoned on fast shutdown — `Program.cs:133` — PLAUSIBLE
The startup rebuild is an untracked `Task.Run`; the launcher sets a 4s shutdown timeout. Closing the last tab mid-rebuild lets the process exit while `RebuildAsync` is still writing SQLite → partial/corrupt or cold index, making the first `start_monitor_session` flaky.

### 8. Git porcelain paths not octal-unescaped — `GitService.cs:312` — CONFIRMED
`ParseStatus` only does `Trim('"')`, never unescaping git's C-quoting. With default `core.quotepath=true`, a non-ASCII filename (e.g. `Ålesund.cs`) parses as the literal `\303\205lesund.cs` → `git show`/`restore`/`add` target a path that doesn't exist → empty diff, silent no-op stage/discard. The wrong path also reaches the agent via `GitMcpTools`.

### 9. Launcher kills a healthy host on a browser hiccup — `InstanceController.cs:90` — PLAUSIBLE
After the host is healthy, `job.Assign(browser.Handle)` throws if the browser process exits promptly (URL forwarded to an existing Chromium window, locked profile). The outer catch calls `Stop()`, tearing down the working host + sidecar over a cosmetic browser exit.

### 10. Job Object assigned after process start (race) — `InstanceController.cs:73` — PLAUSIBLE
Assignment happens after `process.Start()`, so a grandchild spawned in the start→assign window escapes `KILL_ON_JOB_CLOSE`. Correct pattern is `CREATE_SUSPENDED` → assign → resume. Usually hidden by boot latency, not guaranteed.

### 11. Sidecar port check is TOCTOU + adopts a foreign listener — `SidecarProcessHost.cs:30` — PLAUSIBLE
Between "probe port free" and the sidecar's bind, another process can grab the port; the "already listening" branch then wires the host to a stranger with no `/health` identity check — governance events/gates route to a process that isn't our sidecar.

### 12. Agent cwd has no retry/fallback — `sidecar/src/index.ts:416` — PLAUSIBLE
`workspaceCwd` defaults to undefined with no env fallback and (unlike the staging-guide fetch, which got a 60s retry) no retry. If `/health` has not answered by the first prompt, the query is created with `cwd: undefined` → Read/Grep/Glob run over the SDK default dir (the sidecar's own), not the watched solution; the role card shows the project as "(resolving)".

### 13. git_commit with nothing staged commits the whole tree — `GitWorkspaceService.cs:109` — CONFIRMED
When nothing is staged, `CommitAsync` sets `stageAll=true` → `git add -A` + commit, sweeping unrelated working-tree edits / untracked files into the commit; auto-approves under auto-approve ON. Local/reversible (lower severity) but commits state the operator never chose.

### 14. Branch/ref args not `--`-guarded — `GitService.cs:217` — PLAUSIBLE
`git switch` / `switch -c` receive agent-supplied names without a `--` separator (ADR-0004's `--` discipline only guards paths on add/restore/clean). A leading-dash name is parsed as an option: `git switch '-'` switches to the previous branch; `--orphan` misparses. argv blocks RCE (confusion/DoS-grade), but the discipline should extend to refs or reject leading-dash names.

### 15. Operator DiffView uncapped + un-virtualized + re-diffs each render — `GitWorkspaceService.cs:60` / `DiffView.razor.cs` — PLAUSIBLE
The operator diff path has no size cap (agent `git_diff` caps at 8000 chars) and emits one DOM row per line with no `Virtualize`; `DiffView` rebuilds the DiffPlex model on every parent render while the merge dialog re-renders every 1s. A large file freezes the tab / bloats the SignalR batch and re-diffs every second. (Also: repo root re-resolved via a `git rev-parse` spawn on every op — ~4 redundant spawns per reload.)

## Suggested triage order

1. **Governance regression:** #1 (one-line — extend the never-auto-approve set). Then the accept-path cluster #2, #3, #4 (the sole writer of source).
2. **Lifetime:** #5 (can kill a live turn), then #6 / #7 (index rebuild concurrency/abandonment).
3. **Correctness polish:** #8, #12, #14; **launcher edges:** #9, #10, #11.
4. **Perf:** #15.

## What checked out clean

The change set is well-managed and this list is the sharp edges, not a verdict against it:

- Git is argv-only, no shell (ADR-0004); read tools are genuinely read-only.
- ADR-0005 accept/reject is carefully implemented (non-terminal accept writes nothing; terminal accept builds-then-writes the set; reject voids pending + approved-unwritten) — the gap is only the partial-write edge (#2).
- ADR-0006's `isNeverAutoApproved` check is correctly placed before the auto-approve branch (the gap is only which tools are in the set — #1).
- Deletions were gated behind a written retire plan; the CLI's SEV-1-only queries were re-exposed as MCP tools with tests; accept guards were preserved verbatim (`EnsureAcceptanceGuardsPass`); the language corpus now asserts in `dotnet test`.
- Single-source `AgentGuidance` is genuinely consumed by both `get_staging_guide` and the sidecar role card (no stale duplicate). Shared `DiffView` backs both merge review and the git panel (no forked diff renderer). DI lifetimes are sane; no dangling refs to the deleted `AIMonitor.Cli` / `AIMonitor.Runtime`.

## Latent (no active defect today, worth a note)

- The only source-code log-redaction implementation was deleted with `AIMonitor.Cli`; there is no MCP equivalent. No active leak (the MCP path logs only session-touch events), but the "source never reaches logs" invariant is now unenforced — the moment tool-argument telemetry is added on the MCP path, raw edited source could land in logs unredacted with nothing to catch it.
