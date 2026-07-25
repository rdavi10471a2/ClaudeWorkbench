# ClaudeWorkbench.E2E.Tests (Playwright, human-visible)

Black-box browser tests that drive the **real Blazor Assistant UI** through Playwright, so a human can
watch the governed loop render. They reference no product code — they exercise what the user sees.

## One operator at a time (important)

The driver drives your **running Host** at `AIMW_E2E_BASEURL`. That Host is single-operator: its agent
session, sidecar, and approval gate are one shared server-side state. If you also interact with the same
app in another browser tab/window while a driver run is in flight, the two circuits fight over that one
session and the app can hang. **While a driver run is going, don't touch the app yourself** — just watch
the Chromium window Playwright opened. (A future option is a dedicated, isolated Host instance on its own
port + workspace so the two never share state.)

## Self-gating

Every test **skips** (never fails) when its prerequisites are missing, so `dotnet test ClaudeWorkbench.slnx`
stays green on a machine without a running Host or installed browsers:

- Host not reachable at the base URL → skipped (the check runs before any browser launch).
- Playwright browsers not installed → skipped with the install command.

## Run it (watch it)

1. **One-time: install the browsers** (downloads Chromium/Firefox/WebKit):
   ```powershell
   dotnet build tests/e2e/ClaudeWorkbench.E2E.Tests
   pwsh tests/e2e/ClaudeWorkbench.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install
   ```
2. **Start the Host** (the launcher, or run `ClaudeWorkbench.Host`) — the UI serves at
   `http://localhost:6100` by default.
3. **Run headed, so you can watch:**
   ```powershell
   $env:AIMW_E2E_HEADED = "1"
   $env:AIMW_E2E_SLOWMO = "400"   # optional: 400ms between actions
   dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests
   ```

## Watch a real scenario run live (the driver)

`LivePromptDriverTests` opens the Assistant, types one of the `test-prompts/` prompts, clicks **Submit
Turn**, and lets you **watch the real governed loop** — tool-call lines streaming in, the plan-complete
build, then Merge Review. It drives the **real Claude agent**, so it is **opt-in** (spends tokens, needs
Claude signed in) and never runs during a normal `dotnet test`.

Prereq: start the Host **on the sample the prompt targets** (e.g. CalculatorSample), signed in.

```powershell
$env:AIMW_E2E_LIVE   = "1"      # opt in to the real-agent driver
$env:AIMW_E2E_HEADED = "1"      # show the browser
$env:AIMW_E2E_SLOWMO = "400"    # follow the clicks
$env:AIMW_E2E_HOLD   = "120"    # keep the browser open 120s after the turn (to Accept in Merge Review)
$env:AIMW_E2E_PROMPT = "samples/watched-solutions/CalculatorSample/test-prompts/01-add-method.md"
dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests --filter "FullyQualifiedName~LivePromptDriver"
```

Point `AIMW_E2E_PROMPT` at any Calculator (`01`–`06`) or Blazor (`01`–`05`) prompt. Omit it to default
to Calculator `01-add-method`.

## Environment knobs

| Variable | Default | Meaning |
|---|---|---|
| `AIMW_E2E_BASEURL` | `http://localhost:6100` | The running Host UI |
| `AIMW_E2E_HEADED` | (headless) | `1`/`true` to show the browser |
| `AIMW_E2E_SLOWMO` | `0` | Milliseconds of slow-motion between actions |
| `AIMW_E2E_LIVE` | (off) | `1` to enable the real-agent live driver (opt-in; spends tokens) |
| `AIMW_E2E_HOLD` | `0` | Seconds to keep the browser open after the turn (to Accept in Merge Review) |
| `AIMW_E2E_PROMPT` | Calculator `01` | Path to the test-prompt `.md` to drive |
| `AIMW_E2E_VIDEO` | (off) | `1` to record a `.webm` of the run |
| `AIMW_E2E_TRACE` | (off) | `1` to record a Playwright trace (`playwright show-trace trace.zip`) |
| `AIMW_E2E_ARTIFACTS` | `%TEMP%\ClaudeWorkbenchE2E` | Where video/trace land |

The live driver ticks **Auto-approve** before submitting and auto-clicks **Allow** on any approval
dialog, so the loop runs hands-free to Merge Review (the write to watched source is still gated by the
human Accept). Recordings finalize when the run ends: video at `<artifacts>\video\*.webm`, trace at
`<artifacts>\trace.zip`.

## Stable selectors

The Assistant page (`AssistantTab.razor`) carries `data-testid` hooks: `composer-input`, `transcript`,
`message-user`, `message-assistant`, `tool-call`, `auto-approve`, `turn-activity`. The Radzen buttons
(Submit Turn / Stop / New Thread / Copy / Pop Out) have no testid — target them by role + accessible
name (`GetByRole(AriaRole.Button, name: "Submit Turn")`).

## Two test kinds here

- **Smoke** (`AssistantPageSmokeTests`) — no agent; proves the page/hooks/browser round-trip. Safe in the
  normal suite (self-gates on Host reachability + browser install).
- **Live driver** (`LivePromptDriverTests`) — drives the **real** agent so you can watch a scenario run.
  Opt-in via `AIMW_E2E_LIVE=1`; never runs by accident.

Possible follow-up: a **deterministic replay** (inject canned transcript entries + tool calls via a
test-only session seam) so the exact Calculator/Blazor scenarios animate in the browser with no tokens —
the browser-visible counterpart to the engine-level `AgentLoopSampleWorkflowTests`.
