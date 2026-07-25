using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// Browser coverage for the Threads tab (the conversation-thread list that replaced the Tasks board).
// Deterministic and agent-free: it only proves the tab is reachable and renders, exercising the
// data-testid hooks added to ThreadsTab.razor (threads-tab, threads-refresh, and — when any thread
// exists in the target workspace — thread-row). The autosave->UI path (a driven conversation showing
// up as a row) is asserted by the live driver; see LivePromptDriverTests.
public sealed class ThreadsTabSmokeTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture fixture;

    public ThreadsTabSmokeTests(PlaywrightFixture fixture)
    {
        this.fixture = fixture;
    }

    [SkippableFact]
    public async Task Threads_tab_opens_and_renders()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        IPage page = fixture.Page;
        await page.GotoAsync(E2EEnvironment.BaseUrl);

        // App loaded (composer present) before we switch tabs.
        await Assertions.Expect(page.GetByTestId("composer-input"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // The Radzen tab header carries the text "Threads" (no testid on the tab itself).
        await page.GetByText("Threads", new PageGetByTextOptions { Exact = true }).First.ClickAsync();

        // The tab body and its refresh control render (empty-state or rows — either is valid).
        await Assertions.Expect(page.GetByTestId("threads-tab"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await Assertions.Expect(page.GetByTestId("threads-refresh")).ToBeVisibleAsync();
    }
}
