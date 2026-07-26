using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// Browser coverage for the Conversations modal (the conversation-thread board that replaced the Tasks
// board), opened from the composer toolbar's `open-conversations` button. Deterministic and agent-free:
// it only proves the modal opens and renders, exercising the data-testid hooks on ThreadsDialog.razor
// (threads-tab, threads-refresh, and — when any thread exists in the target workspace — thread-row).
// The autosave->UI path (a driven conversation showing up as a row) is asserted by the live driver.
public sealed class ConversationsDialogSmokeTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture fixture;

    public ConversationsDialogSmokeTests(PlaywrightFixture fixture)
    {
        this.fixture = fixture;
    }

    [SkippableFact]
    public async Task Threads_tab_opens_and_renders()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        IPage page = fixture.Page;
        await page.GotoAsync(E2EEnvironment.BaseUrl);

        // App loaded (composer present) before we open the Conversations modal.
        await Assertions.Expect(page.GetByTestId("composer-input"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Conversations is a modal launched from the composer toolbar button (not a tab).
        await page.GetByTestId("open-conversations").ClickAsync();

        // The board and its refresh control render (empty-state or rows — either is valid).
        await Assertions.Expect(page.GetByTestId("threads-tab"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await Assertions.Expect(page.GetByTestId("threads-refresh")).ToBeVisibleAsync();
    }
}
