using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// The "automated headed driver": opens the REAL Assistant UI and runs each test-prompt end to end so you
// can WATCH the governed loop unfold - tool-call lines streaming in, the plan-complete build, Merge
// Review, and (opt-in) the Accept that writes watched source. It drives the real Claude agent, so it is:
//
//   * OPT-IN  - skipped unless AIMW_E2E_LIVE=1 (spends tokens, needs Claude signed in).
//   * headed  - AIMW_E2E_HEADED=1 (+ AIMW_E2E_SLOWMO=500 to follow it).
//   * batched - runs every prompt from PromptFiles() as its own theory case, in order, sharing one
//               browser so a single video/trace captures the whole session. New Thread resets the
//               conversation between prompts. See E2EEnvironment.PromptFiles for selection.
//
// PREREQS: the Host must already be running on the sample the prompts target (e.g. CalculatorSample).
// The driver types/submits/accepts; it does not switch workspaces or sign you in. Don't touch the app
// yourself while it runs (single-operator - see README).
//
// NOT FULLY DETERMINISTIC: this drives the REAL agent, so it can raise a prompt the driver does not
// script - most often an AskUserQuestion elicitation (the questions dialog), or a tool the auto-Allow
// doesn't cover - and then pause waiting on a human (eventually timing out). Monitor a live run and
// answer/approve in the browser if it stops.
//
//   $env:AIMW_E2E_LIVE="1"; $env:AIMW_E2E_HEADED="1"; $env:AIMW_E2E_SLOWMO="500"
//   $env:AIMW_E2E_ACCEPT="1"; $env:AIMW_E2E_VIDEO="1"
//   dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests --filter "FullyQualifiedName~LivePromptDriver"
public sealed class LivePromptDriverTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture fixture;
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public LivePromptDriverTests(PlaywrightFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    public static IEnumerable<object[]> Prompts()
    {
        IReadOnlyList<string> files = E2EEnvironment.PromptFiles();
        if (files.Count == 0)
        {
            // One placeholder case so the theory isn't empty; it will Skip.
            yield return ["(none)", string.Empty];
            yield break;
        }

        foreach (string file in files)
        {
            yield return [Path.GetFileNameWithoutExtension(file), file];
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Prompts))]
    public async Task Drive_test_prompt(string name, string promptFile)
    {
        Skip.IfNot(E2EEnvironment.LiveEnabled,
            "Live driver is opt-in (real Claude agent + tokens). Set AIMW_E2E_LIVE=1 to run it.");
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Skip.IfNot(File.Exists(promptFile), $"Prompt file not found: {promptFile}");

        string prompt = E2EEnvironment.ReadPromptSection(promptFile);
        Skip.If(string.IsNullOrWhiteSpace(prompt), $"No '## Prompt' section in {promptFile}");

        output.WriteLine($"Driving prompt '{name}' ({promptFile})");

        IPage page = fixture.Page;
        await page.GotoAsync(E2EEnvironment.BaseUrl);

        ILocator composer = page.GetByTestId("composer-input");
        await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Reset the conversation so each prompt is an independent turn (best-effort: disabled while a
        // turn is running, absent on a fresh session).
        ILocator newThread = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New Thread" });
        if (await newThread.IsVisibleAsync() && await newThread.IsEnabledAsync())
        {
            await newThread.ClickAsync();
            await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        }

        // Auto-approve claude-workbench edits for this thread so the loop flows without a per-tool
        // dialog (New Thread resets it, so re-tick every prompt). The write is still gated by Accept.
        await page.GetByTestId("auto-approve").CheckAsync();

        await composer.FillAsync(prompt);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit Turn" }).ClickAsync();

        // Turn started -> a tool-call streams in (the live loop) -> turn finished.
        ILocator activity = page.GetByTestId("turn-activity");
        await Assertions.Expect(activity).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("tool-call").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });
        await Assertions.Expect(activity).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 600_000 });
        await Assertions.Expect(page.GetByTestId("message-assistant").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        if (E2EEnvironment.AcceptChanges)
        {
            await ResolveReviewAsync(page);
        }

        // Autosave -> UI: the driven conversation must have surfaced as a saved thread. Open the
        // Conversations modal and assert at least one thread row is present (one created per prompt).
        await page.GetByTestId("open-conversations").ClickAsync();
        await Assertions.Expect(page.GetByTestId("thread-row").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        if (E2EEnvironment.HoldSeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(E2EEnvironment.HoldSeconds));
        }
    }

    // Merge Review auto-opens after the turn when a candidate is staged. Accept each file when the
    // normal Accept is available; if pre-merge validation failed (Accept disabled), Reject instead -
    // that is the correct outcome for the build-fail scenarios and keeps the batch from hanging.
    private static async Task ResolveReviewAsync(IPage page)
    {
        ILocator accept = page.GetByTestId("accept-proposed");
        ILocator reject = page.GetByTestId("reject-proposed");
        ILocator busy = page.GetByTestId("review-busy");

        try
        {
            await accept.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            return; // nothing staged (e.g. a read-only prompt) or the dialog already closed
        }

        for (int i = 0; i < 25 && await accept.IsVisibleAsync(); i++)
        {
            try
            {
                if (await accept.IsEnabledAsync())
                {
                    await accept.ClickAsync(new LocatorClickOptions { Timeout = 120_000 });
                }
                else
                {
                    await reject.ClickAsync(new LocatorClickOptions { Timeout = 120_000 });
                }
            }
            catch (Exception)
            {
                break; // the dialog raced closed under the click, or nothing left to decide - done
            }

            // The decision runs the terminal build + write (+ optional reindex): wait it out.
            try { await busy.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 }); }
            catch (TimeoutException) { }
            try { await busy.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 180_000 }); }
            catch (TimeoutException) { }
            await page.WaitForTimeoutAsync(1_000);
        }
    }
}
