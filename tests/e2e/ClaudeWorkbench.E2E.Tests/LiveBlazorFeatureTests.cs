using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// A WATCHABLE live E2E: it drives the REAL Assistant UI to build a SIGNIFICANT, multi-file feature on the
// BlazorSample end to end, so you can watch the whole governed loop unfold — tool-call lines streaming in,
// the plan-complete build running the real Razor source generator across the project graph, Merge Review
// opening with several staged files, and (opt-in) the Accept that writes watched source.
//
// It asks for a genuine feature (a customer directory: a model change + a new Razor component + service
// wiring), not a one-liner — the kind of change that only a real cross-file build (with the Razor
// generator) can validate, and that stages multiple files into one review.
//
// PREREQ — start the app FIRST: the driver attaches to your already-running Host at AIMW_E2E_BASEURL. It
// does NOT launch the app, switch workspaces, or sign you in. So before running: start the Host, open the
// BlazorSample workspace, and be signed into Claude. If the Host isn't reachable the test SKIPS.
//
// Watch it:
//   $env:AIMW_E2E_LIVE="1"      # opt-in: real agent + tokens (skips otherwise)
//   $env:AIMW_E2E_HEADED="1"    # open a Chromium window you can watch
//   $env:AIMW_E2E_SLOWMO="500"  # 500ms between actions, so it's followable
//   $env:AIMW_E2E_ACCEPT="1"    # also accept in Merge Review (writes source); omit for watch-only
//   $env:AIMW_E2E_HOLD="45"     # keep the browser open 45s at the end to look around
//   $env:AIMW_E2E_VIDEO="1"     # optional: record a .webm of the whole run
//   $env:AIMW_E2E_BASEURL="http://localhost:6100"   # match the running Host's URL
//   dotnet test tests/e2e/ClaudeWorkbench.E2E.Tests --filter "FullyQualifiedName~LiveBlazorFeature"
//
// Same two caveats as LivePromptDriverTests: SINGLE-OPERATOR (don't touch the app yourself while it runs —
// you and the driver share one server-side session), and NOT fully deterministic (the real agent may raise
// an AskUserQuestion the driver doesn't script and pause — watch, and answer in the Chromium window if it
// stops).
public sealed class LiveBlazorFeatureTests : IClassFixture<PlaywrightFixture>
{
    // A real, multi-file feature request — deliberately phrased as a user would, letting the agent design
    // the files. It forces a model change + a NEW Razor component + service wiring, so the plan-complete
    // build must resolve new members through the generated Razor code across the project graph.
    // The feature to build. Defaults to the customer-directory feature; override with
    // AIMW_E2E_BLAZOR_PROMPT to drive a different feature (e.g. to re-run against a workspace that already
    // has the directory, so there is real work to stage).
    private static string FeaturePrompt =>
        Environment.GetEnvironmentVariable("AIMW_E2E_BLAZOR_PROMPT") is { Length: > 0 } custom
            ? custom
            : "Add a customer directory feature to this Blazor app. Give the Customer model an Email property, "
              + "add a way to list all customers from the customer repository/service, and create a NEW Razor "
              + "component that renders the customer list as a simple table of Name and Email. Wire the component "
              + "so it renders on a page. Make sure the whole solution still builds.";

    // Second turn, run AFTER the accept has rebuilt the index: make the agent query the FRESH index. If the
    // post-accept rebuild produced a usable index, find-references resolves against the just-accepted code.
    private const string FindReferencesPrompt =
        "Now, using the solution index, find all references to the CustomerService class across the solution "
        + "and list each call site (file and line). This is read-only — do not edit anything.";

    private readonly PlaywrightFixture fixture;
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public LiveBlazorFeatureTests(PlaywrightFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [SkippableFact]
    public async Task Build_a_customer_directory_feature_on_the_Blazor_sample()
    {
        Skip.IfNot(E2EEnvironment.LiveEnabled,
            "Live driver is opt-in (real Claude agent + tokens). Set AIMW_E2E_LIVE=1 to run it.");
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        output.WriteLine("Driving a SIGNIFICANT multi-file Blazor feature. The Host must be watching the BlazorSample.");
        output.WriteLine($"Prompt: {FeaturePrompt}");

        IPage page = fixture.Page;
        await page.GotoAsync(E2EEnvironment.BaseUrl);

        ILocator composer = page.GetByTestId("composer-input");
        await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Fresh thread so this feature turn is independent (best-effort: disabled mid-turn, absent on a
        // fresh session).
        ILocator newThread = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New Thread" });
        if (await newThread.IsVisibleAsync() && await newThread.IsEnabledAsync())
        {
            await newThread.ClickAsync();
            await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        }

        // Auto-approve workbench edits for this thread so the loop flows without a per-tool dialog (New
        // Thread resets it). The write is still gated by Accept in Merge Review.
        await page.GetByTestId("auto-approve").CheckAsync();

        await composer.FillAsync(FeaturePrompt);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit Turn" }).ClickAsync();

        // Watch the loop: turn starts -> tool calls stream in -> the plan-complete real build (Razor
        // generator + cross-project) -> turn finishes with an assistant summary. A feature this size can
        // take several minutes with the real agent, so the "turn finished" wait is generous.
        ILocator activity = page.GetByTestId("turn-activity");
        await Assertions.Expect(activity).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("tool-call").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });
        await Assertions.Expect(activity).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 900_000 });
        await Assertions.Expect(page.GetByTestId("message-assistant").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // It's a multi-file feature, so more than one tool call should have streamed in.
        int toolCalls = await page.GetByTestId("tool-call").CountAsync();
        output.WriteLine($"Tool calls observed this turn: {toolCalls}");

        if (E2EEnvironment.AcceptChanges)
        {
            output.WriteLine("AIMW_E2E_ACCEPT=1 — resolving Merge Review (accepting each staged file)...");
            await ResolveReviewAsync(page);

            // The accept rebuilt the index. Second turn: make the agent query that FRESH index — find
            // references via the index the accept just produced. You watch it resolve against the code it
            // only just wrote. (find_indexed_references is read-only, so no approval gate.)
            output.WriteLine("Accept + index rebuild done — second turn: find references via the fresh index...");
            await Assertions.Expect(composer).ToBeEditableAsync(new LocatorAssertionsToBeEditableOptions { Timeout = 30_000 });
            await composer.FillAsync(FindReferencesPrompt);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit Turn" }).ClickAsync();

            await Assertions.Expect(activity).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await Assertions.Expect(activity).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 300_000 });
            await Assertions.Expect(composer).ToBeEditableAsync(new LocatorAssertionsToBeEditableOptions { Timeout = 15_000 });
            output.WriteLine("Find-references turn complete (queried the post-accept index).");
        }
        else
        {
            output.WriteLine("Watch-only (AIMW_E2E_ACCEPT unset): the staged files are left in Merge Review for you to inspect.");
        }

        // The driven conversation should have surfaced as a saved thread.
        await page.GetByTestId("open-conversations").ClickAsync();
        await Assertions.Expect(page.GetByTestId("thread-row").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });

        if (E2EEnvironment.HoldSeconds > 0)
        {
            output.WriteLine($"Holding the browser open {E2EEnvironment.HoldSeconds}s so you can look around...");
            await Task.Delay(TimeSpan.FromSeconds(E2EEnvironment.HoldSeconds));
        }
    }

    // Merge Review auto-opens after the turn when candidates are staged. Accept each file when the normal
    // Accept is enabled; if the pre-merge build failed (Accept disabled), Reject instead — that keeps the
    // review from hanging. Mirrors LivePromptDriverTests.ResolveReviewAsync.
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
            return; // nothing staged, or the dialog already closed
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
                break; // the dialog raced closed under the click, or nothing left to decide
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
