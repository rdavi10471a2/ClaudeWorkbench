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
did or didn't land on disk, then **resets the watched fixture to a completely fresh
copy** of a pristine golden and re-warms the index (guaranteed in a `finally`), and
starts a fresh thread — so every case begins from pristine source no matter what the
last case wrote or created (a new folder/namespace, an accepted whole-file overwrite).
Because an accept WRITES watched source, this reset is what keeps the cases independent;
a per-file restore is not enough.

## Fixture: a throwaway %temp% copy + a golden

Point the harness at the pristine source with `SMOKE_GOLDEN_DIR`; the reset wipes the
watched tree and re-copies it. The recommended setup gives each case a fresh solution
without ever mutating the repo sample:

```bash
# From the repo root. $TEMP is your temp dir.
golden="$TEMP/cwb-smoke-golden/CalculatorSample"; work="$TEMP/cwb-smoke-work/CalculatorSample"
rm -rf "$golden" "$work"; mkdir -p "$(dirname "$golden")" "$(dirname "$work")"
cp -r samples/watched-solutions/CalculatorSample "$golden"; rm -rf "$golden/bin" "$golden/obj"
cp -r "$golden" "$work"
```

Then a config pointing `WatchedSolutionPath` at `$work/CalculatorSample.slnx` and
`RuntimeRoot` at a temp dir. When `SMOKE_GOLDEN_DIR` is unset the reset is a no-op and
multi-accept cases bleed together — set it.

## Run

Needs the **.NET 10 SDK** (indexing calls `MSBuildLocator.RegisterDefaults()`, which
needs MSBuild/Roslyn — the runtime alone is not enough), **Node.js**, and a Claude
login.

1. Stage the fixture (above), then start the host on the working copy with the review
   API opted in (the host launches the sidecar itself):

   ```bash
   CWB_ENABLE_REVIEW_API=1 dotnet run --project src/ClaudeWorkbench.Host \
     -- --config /abs/path/to/smoke-config.json --repo-root "$PWD"
   ```

2. In `sidecar/`, run the smoke pointed at the golden (and an evidence dir):

   ```bash
   SMOKE_GOLDEN_DIR="$golden" SMOKE_EVIDENCE_DIR="$TEMP/cwb-smoke-evidence" npm run smoke
   ```

## Evidence

Each case writes, under `SMOKE_EVIDENCE_DIR` (default `sidecar/smoke-evidence/`,
gitignored), the whole turn for offline analysis:

- `<case>.chat.txt` — the readable transcript: the prompt, every assistant message, and
  every tool call in order.
- `<case>.events.jsonl` — the raw sidecar event stream for the turn.

The chat is captured off the sidecar's `/events` bus, best-effort: a dropped stream
loses the capture, not the test (completion is polled off `/health`).

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
| `SMOKE_GOLDEN_DIR` | (unset) | pristine solution the fixture is reset FROM between cases; unset = no reset |
| `SMOKE_EVIDENCE_DIR` | `sidecar/smoke-evidence` | where per-case `<case>.chat.txt` + `.events.jsonl` are written |
