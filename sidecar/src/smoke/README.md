# Flow smoke — governed edit loop, end to end

`flow.smoke.test.ts` drives the one path that carries the safety guarantee, in
TypeScript, against the live stack:

```
POST /prompt (autoApprove) → agent stages a candidate → POST /review/accept
→ watched source written → GATE-2 build passes
```

That accept is `EngineReviewWorkflow.Accept` — where the H1/H2 fixes live. Normally
it is an operator action at the Merge Review dialog (the only path that writes
watched source). The smoke reaches the **same** method over HTTP via a test-only
host endpoint so it can run without a human at the UI.

This is an **integration smoke**, not a unit test: it needs the full stack running
against the `CalculatorSample` fixture and makes a **real Claude turn** (network +
tokens).

## Fixture

`samples/watched-solutions/CalculatorSample/` — a tiny buildable console solution
with `Calculator` and `AdvancedCalculations`. The smoke asks the agent to add a
`Modulo` method to `Calculator.cs`, accepts it, verifies the bytes landed and the
build passed, then **restores every fixture file it touched** (guaranteed in a
`finally`), so it is safe to re-run.

## Run

1. Start the host with the fixture config **and the review API opted in**. The host
   launches the sidecar itself.

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

## Safety note on `/review/accept`

`GET /review/pending` and `POST /review/accept` bypass the human merge window, so
they are **off by default** and only mapped when `CWB_ENABLE_REVIEW_API=1`.
Production never sets it, so the invariant "operator Accept is the only path that
writes watched source" holds in real use; the smoke opts in explicitly.

The smoke also refuses to run unless the host reports it is watching
`CalculatorSample.slnx`, so it can never mutate a real project.

## Environment overrides

| Var | Default | Purpose |
|---|---|---|
| `WORKBENCH_HOST` | `http://127.0.0.1:6100` | host base URL |
| `SIDECAR_BASE` | `http://127.0.0.1:6110` | sidecar base URL |
| `SMOKE_TURN_TIMEOUT_MS` | `300000` | max wait for the agent turn |
