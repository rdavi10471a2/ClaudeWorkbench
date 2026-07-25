# ClaudeWorkbench.E2E.Tests (Playwright, human-visible)

Black-box browser tests that drive the **real Blazor Assistant UI** through Playwright, so a human can
watch the governed loop render. They reference no product code — they exercise what the user sees.

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

## Environment knobs

| Variable | Default | Meaning |
|---|---|---|
| `AIMW_E2E_BASEURL` | `http://localhost:6100` | The running Host UI |
| `AIMW_E2E_HEADED` | (headless) | `1`/`true` to show the browser |
| `AIMW_E2E_SLOWMO` | `0` | Milliseconds of slow-motion between actions |

## Stable selectors

The Assistant page (`AssistantTab.razor`) carries `data-testid` hooks: `composer-input`, `transcript`,
`message-user`, `message-assistant`, `tool-call`, `auto-approve`, `turn-activity`. The Radzen buttons
(Submit Turn / Stop / New Thread / Copy / Pop Out) have no testid — target them by role + accessible
name (`GetByRole(AriaRole.Button, name: "Submit Turn")`).

## Next step

These smoke tests prove the harness + hooks + browser round-trip. The planned follow-up is a scripted
**"agent in the middle"** that emits canned transcript entries and tool calls through the UI, so a full
author→submit→review loop is deterministic *and* watchable — the browser-visible counterpart to the
engine-level `AgentLoopSampleWorkflowTests`.
