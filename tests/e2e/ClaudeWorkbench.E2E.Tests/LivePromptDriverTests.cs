using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// The "automated headed driver": opens the REAL Assistant UI, types a test-prompt, clicks Submit, and
// then lets you WATCH the real governed loop unfold in the browser - tool-call lines streaming into the
// transcript, the plan-complete build, and Merge Review. It drives the real Claude agent, so it is:
//
//   * OPT-IN  - skipped unless AIMW_E2E_LIVE=1 (it spends tokens and needs Claude signed in).
//   * headed  - run with AIMW_E2E_HEADED=1 (+ AIMW_E2E_SLOWMO=400 to follow the clicks).
//   * held    - AIMW_E2E_HOLD=120 keeps the browser open 120s after the turn so you can Accept.
//
// PREREQS: the Host must already be running on the sample the prompt targets (e.g. launch it on
// CalculatorSample). The driver types and submits; it does not switch workspaces or sign you in.
//
//   $env:AIMW_E2E_LIVE="1"; $env:AIMW_E2E_HEADED="1"; $env:AIMW_E2E_SLOWMO="400"; $env:AIMW_E2E_HOLD="120"
//   $env:AIMW_E2E_PROMPT="samples/watched-solutions/CalculatorSample/test-prompts/01-add-method.md"
//   dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests --filter "FullyQualifiedName~LivePromptDriver"
public sealed class LivePromptDriverTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture fixture;

    public LivePromptDriverTests(PlaywrightFixture fixture)
    {
        this.fixture = fixture;
    }

    [SkippableFact]
    public async Task Drive_a_test_prompt_and_watch_the_loop()
    {
        Skip.IfNot(E2EEnvironment.LiveEnabled,
            "Live driver is opt-in (it uses the real Claude agent + tokens). Set AIMW_E2E_LIVE=1 to run it.");
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Skip.IfNot(File.Exists(E2EEnvironment.PromptFile), $"Prompt file not found: {E2EEnvironment.PromptFile}");

        string prompt = E2EEnvironment.ReadPromptSection(E2EEnvironment.PromptFile);
        Skip.If(string.IsNullOrWhiteSpace(prompt), $"No '## Prompt' section in {E2EEnvironment.PromptFile}");

        IPage page = fixture.Page;
        await page.GotoAsync(E2EEnvironment.BaseUrl);

        // Fallback for the operator gate (AgentActionModal): if an approval dialog appears, click
        // "Allow". Registered before the turn so it fires the moment a dialog interrupts. With
        // auto-approve ticked below this should rarely trigger, but a never-auto-approvable tool
        // (ADR-0006) would still surface a dialog, and this keeps the loop watchable end to end.
        ILocator allowButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Allow", Exact = true });
        await page.AddLocatorHandlerAsync(allowButton, async handled => await handled.ClickAsync());

        ILocator composer = page.GetByTestId("composer-input");
        await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Auto-approve claude-workbench edits for this thread so the loop flows to Merge Review
        // without a per-tool dialog. The write to watched source is still gated by the human Accept.
        await page.GetByTestId("auto-approve").CheckAsync();

        // Type the prompt a human would paste, then submit the turn.
        await composer.FillAsync(prompt);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit Turn" }).ClickAsync();

        // The turn started: "Thinking..." appears.
        ILocator activity = page.GetByTestId("turn-activity");
        await Assertions.Expect(activity).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // The agent is acting: at least one tool-call line streams into the transcript. THIS is the
        // live loop you wanted to watch.
        ILocator toolCalls = page.GetByTestId("tool-call");
        await Assertions.Expect(toolCalls.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });

        // The turn finished: "Thinking..." goes away (real turns can be slow - allow minutes).
        await Assertions.Expect(activity).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 600_000 });

        // An assistant reply rendered.
        await Assertions.Expect(page.GetByTestId("message-assistant").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        // Optionally script the whole loop through to the write: Merge Review auto-opens after the turn
        // when a candidate is staged. Accept each file (single-file closes after one; session flow
        // advances per accept and auto-closes on the last). The Accept is the human gate to watched
        // source - scripting it here writes the change, so it is opt-in via AIMW_E2E_ACCEPT.
        if (E2EEnvironment.AcceptChanges)
        {
            ILocator accept = page.GetByTestId("accept-proposed");
            ILocator busy = page.GetByTestId("review-busy");
            try
            {
                await accept.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 30_000,
                });

                for (int i = 0; i < 25 && await accept.IsVisibleAsync(); i++)
                {
                    await accept.ClickAsync(new LocatorClickOptions { Timeout = 120_000 });
                    // The accept runs the terminal build + write (+ optional reindex): wait out the
                    // busy overlay, then loop - the dialog either advances to the next file or closes.
                    try { await busy.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 }); }
                    catch (TimeoutException) { }
                    try { await busy.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 180_000 }); }
                    catch (TimeoutException) { }
                    await page.WaitForTimeoutAsync(1_000);
                }
            }
            catch (TimeoutException)
            {
                // Nothing staged to accept (e.g. a read-only prompt), or the dialog already closed.
            }
        }

        // Hold the browser open so you can see the result (and Merge Review if not auto-accepted).
        if (E2EEnvironment.HoldSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(E2EEnvironment.HoldSeconds));
        }
    }
}
