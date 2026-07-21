# Flow smoke — governed edit loop, end to end

`flow.smoke.test.ts` drives the paths that carry the safety guarantee, in
TypeScript, against the live stack — three tests, each a real Claude turn:

```
1. accept        POST /prompt (autoApprove) → agent stages → POST /review/accept
                 → GATE-2 build passes → watched source written
2. reject        POST /prompt (autoApprove) → agent stages → POST /review/reject
                 → watched source UNCHANGED
3. multi-file    one session stages two files → accept both
                 → terminal build passes → both written
4. multi-reject  two files in one session → reject ONE → whole session voided
                 (ADR-0005), NOTHING written, both records leave the queue
5. build-error   a change that breaks a caller in an untouched file → accept
                 → terminal GATE-2 build FAILS → accept refused, source unchanged
```

Every case is one real Claude turn off the CalculatorSample fixture, covering the workflow
shapes: single-file, multi-file, reject, and build-gate failure. (2)/(4) also assert the
one-session-per-run invariant — every staged record shares one `sessionId`.

Accept/Reject are `EngineReviewWorkflow` — where the H1/H2 fixes live. Note the
ordering the accept test asserts: the authoritative GATE-2 build runs **before**
anything is written, so a failing build means nothing lands. Normally these are
operator actions at the Merge Review dialog (the only path that writes watched
source). The smoke reaches the **same** methods over HTTP via test-only host
endpoints so it can run without a human at the UI.

Turn completion is detected by **polling** (`GET /health` → `activeTurn`) and staged
work by `GET /review/pending` — not from the SSE `/events` stream, which several
subscribers share.

This is an **integration smoke**, not a unit test: it needs the full stack running
against the `CalculatorSample` fixture and makes **real Claude turns** (network +
tokens).

## Fixture

`samples/watched-solutions/CalculatorSample/` — a tiny buildable console solution
with `Calculator` and `AdvancedCalculations`. The smoke asks the agent for small
additions (`Modulo`, `Triple`, `Negate`/`Cube`), drives the decision, verifies what
did or didn't land on disk, then **restores every fixture file it touched**
(`Calculator.cs`, `AdvancedCalculations.cs`, `Program.cs` — guaranteed in a
`finally`) and starts a fresh thread, so it is safe to re-run.

The committed fixture config uses **relative** paths — `WatchedSolutionPath`
resolves against the config file's own folder, `RuntimeRoot` against the repo root —
so it works in any checkout with no editing.

## Run

Needs the **.NET 10 SDK** (indexing calls `MSBuildLocator.RegisterDefaults()`, which
needs MSBuild/Roslyn — the runtime alone is not enough), **Node.js**, and a Claude
login.

1. Start the host **from the repo root** with the fixture config **and the review API
   opted in**. The host launches the sidecar itself.

   ```bash
   # bash
   CWB_ENABLE_REVIEW_API=1 dotnet run --project src/ClaudeWorkbench.Host \
     -- --config src/ClaudeWorkbench.Host/config/appsettings.calculator-sample.json
   ```

   ```powershell
   # PowerShell
   $env:CWB_ENABLE_REVIEW_API = "1"
   dotnet run --project src/ClaudeWorkbench.Host `
     -- --config src/ClaudeWorkbench.Host/config/appsettings.calculator-sample.json
   ```

2. In `sidecar/`:

   ```bash
   npm run smoke
   ```

## Safety note on the review endpoints

`GET /review/pending`, `POST /review/accept`, `POST /review/reject` and
`POST /review/warmup` bypass the human merge window, so they are **off by default**
and only mapped when `CWB_ENABLE_REVIEW_API=1`. Production never sets it, so the
invariant "operator Accept is the only path that writes watched source" holds in real
use; the smoke opts in explicitly.

`/review/warmup` is why the flag is needed even before the first accept: a turn
against a **cold** index makes `start_monitor_session` flaky (it can't prove the
single owning project). The host does warm the index at startup, but in the
background — so the smoke calls `/review/warmup` once in `before()` and *waits* for
it, guaranteeing every turn runs warm.

The smoke also refuses to run unless the host reports it is watching
`CalculatorSample.slnx`, so it can never mutate a real project.

## Environment overrides

| Var | Default | Purpose |
|---|---|---|
| `WORKBENCH_HOST` | `http://127.0.0.1:6100` | host base URL |
| `SIDECAR_BASE` | `http://127.0.0.1:6110` | sidecar base URL |
| `SMOKE_TURN_TIMEOUT_MS` | `300000` | max wait for the agent turn |
