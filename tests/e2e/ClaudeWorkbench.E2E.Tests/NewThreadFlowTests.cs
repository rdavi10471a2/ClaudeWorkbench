using System.Linq;
using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// Browser coverage for the conversation-thread flow, driven through the REAL UI exactly as an operator
// would: click New Thread, fill the popup, hit Start, edit details in the Conversations board, and check
// the top-bar chip. Deterministic and AGENT-FREE — a conversation is persisted the moment New Thread is
// confirmed (StartNamedConversation), so none of this needs a live Claude turn or tokens. It only needs a
// running Host (skips otherwise), exercising the data-testids on NewThreadDialog, the chip, and ThreadsDialog.
//
// Covers what the operator kept hitting:
//   1. a named new conversation shows immediately in the chip AND the Conversations list;
//   2. leaving an unnamed conversation and KEEPING it (with a better name) renames it;
//   3. leaving an unnamed conversation and NOT keeping it discards it from the list;
//   4. the CURRENT (Active) conversation's details are editable, and the rename reflects in the chip.
//
// SELF-CLEANING: every thread this suite creates is named with the E2EPrefix, and each test deletes ALL
// such threads (its own and any left by a prior run) in a finally. It drives the REAL Host, so without
// this it would litter the operator's live Conversations list. It never touches non-E2E threads.
public sealed class NewThreadFlowTests : IClassFixture<PlaywrightFixture>
{
    private const string E2EPrefix = "e2etest-";

    private readonly PlaywrightFixture fixture;

    public NewThreadFlowTests(PlaywrightFixture fixture)
    {
        this.fixture = fixture;
    }

    private static string Unique(string label) => $"{E2EPrefix}{label}-{Guid.NewGuid():N}"[..26];

    [SkippableFact]
    public async Task Naming_a_new_conversation_shows_it_in_the_chip_and_the_list()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = fixture.Page;
        await OpenAppAsync(page);
        try
        {
            string name = Unique("named");
            await OpenNewThreadAsync(page);
            await page.GetByTestId("new-thread-name").FillAsync(name);
            await page.GetByTestId("new-thread-start").ClickAsync();

            // The chip reflects the current conversation immediately — no turn required.
            await Assertions.Expect(page.GetByTestId("thread-chip-label"))
                .ToContainTextAsync(name, new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            // And it's a real, selectable thread in the Conversations board.
            await OpenConversationsAsync(page);
            await Assertions.Expect(RowsWith(page, name).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
            await CloseModalAsync(page);
        }
        finally
        {
            await CleanupAsync(page);
        }
    }

    [SkippableFact]
    public async Task Keeping_the_left_conversation_renames_it()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = fixture.Page;
        await OpenAppAsync(page);
        try
        {
            // Start a conversation on its default name (leave the prefilled name as-is).
            await OpenNewThreadAsync(page);
            string defaultName = (await page.GetByTestId("new-thread-name").InputValueAsync()).Trim();
            await page.GetByTestId("new-thread-start").ClickAsync();
            await Assertions.Expect(page.GetByTestId("thread-chip-label"))
                .ToContainTextAsync(defaultName, new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            // Leave it: the leaving section appears (it's default-named). Keep is on by default — rename it.
            string kept = Unique("kept");
            await OpenNewThreadAsync(page);
            await Assertions.Expect(page.GetByTestId("new-thread-keep-leaving")).ToBeVisibleAsync();
            await page.GetByTestId("new-thread-leaving-name").FillAsync(kept);
            await page.GetByTestId("new-thread-start").ClickAsync();

            await OpenConversationsAsync(page);
            await Assertions.Expect(RowsWith(page, kept).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
            // Renamed, so the old default name is gone (exact match, to dodge -3 vs -30 substring traps).
            string[] names = await ThreadNamesAsync(page);
            Assert.Contains(kept, names);
            Assert.DoesNotContain(defaultName, names);
            await CloseModalAsync(page);
        }
        finally
        {
            await CleanupAsync(page);
        }
    }

    [SkippableFact]
    public async Task Not_keeping_the_left_conversation_discards_it()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = fixture.Page;
        await OpenAppAsync(page);
        try
        {
            // Start a conversation on its default name.
            await OpenNewThreadAsync(page);
            string defaultName = (await page.GetByTestId("new-thread-name").InputValueAsync()).Trim();
            await page.GetByTestId("new-thread-start").ClickAsync();
            await Assertions.Expect(page.GetByTestId("thread-chip-label"))
                .ToContainTextAsync(defaultName, new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            // Leave it and UNCHECK keep -> it's discarded when the new thread starts.
            string replacement = Unique("replacement");
            await OpenNewThreadAsync(page);
            await page.GetByTestId("new-thread-keep-leaving").UncheckAsync();
            await page.GetByTestId("new-thread-name").FillAsync(replacement);
            await page.GetByTestId("new-thread-start").ClickAsync();

            await OpenConversationsAsync(page);
            await Assertions.Expect(RowsWith(page, replacement).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
            // The discarded default-named conversation is gone from the list.
            string[] names = await ThreadNamesAsync(page);
            Assert.Contains(replacement, names);
            Assert.DoesNotContain(defaultName, names);
            await CloseModalAsync(page);
        }
        finally
        {
            await CleanupAsync(page);
        }
    }

    [SkippableFact]
    public async Task Editing_the_active_conversation_details_persists_and_updates_the_chip()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = fixture.Page;
        await OpenAppAsync(page);
        try
        {
            // Start a named conversation — it's the Active thread.
            string original = Unique("editable");
            await OpenNewThreadAsync(page);
            await page.GetByTestId("new-thread-name").FillAsync(original);
            await page.GetByTestId("new-thread-start").ClickAsync();
            await Assertions.Expect(page.GetByTestId("thread-chip-label"))
                .ToContainTextAsync(original, new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            // Select the Active thread in the board and rename it via the details pane.
            string edited = Unique("edited");
            await OpenConversationsAsync(page);
            await RowsWith(page, original).First.ClickAsync();
            ILocator nameField = page.GetByTestId("thread-detail-name");
            await Assertions.Expect(nameField).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
            await nameField.FillAsync(edited);
            await page.GetByTestId("thread-save").ClickAsync();

            // The edit persisted: the row shows the new name, the old is gone.
            await Assertions.Expect(RowsWith(page, edited).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
            string[] names = await ThreadNamesAsync(page);
            Assert.Contains(edited, names);
            Assert.DoesNotContain(original, names);
            await CloseModalAsync(page);

            // And the top-bar chip reflects the rename of the current conversation.
            await Assertions.Expect(page.GetByTestId("thread-chip-label"))
                .ToContainTextAsync(edited, new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        }
        finally
        {
            await CleanupAsync(page);
        }
    }

    private static async Task OpenAppAsync(IPage page)
    {
        await page.GotoAsync(E2EEnvironment.BaseUrl);
        await Assertions.Expect(page.GetByTestId("composer-input"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }

    private static async Task OpenNewThreadAsync(IPage page)
    {
        // New Thread is disabled while a turn/reset is in flight (Working); wait for Ready before
        // clicking, or the handler's `if (Working) return` swallows the click and no dialog opens.
        // New Thread is disabled while a turn OR a prior New Thread's reset is in flight; wait for
        // Ready before clicking, or the handler's guard swallows the click and no dialog opens.
        ILocator button = page.GetByTestId("new-thread");
        await Assertions.Expect(button).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });
        await button.ClickAsync();
        await Assertions.Expect(page.GetByTestId("new-thread-start"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
    }

    private static async Task OpenConversationsAsync(IPage page)
    {
        await page.GetByTestId("open-conversations").ClickAsync();
        await Assertions.Expect(page.GetByTestId("threads-tab"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
    }

    private static async Task CloseModalAsync(IPage page)
    {
        // The Radzen dialog's title-bar close button (Escape isn't wired for this modal).
        await page.Locator(".rz-dialog-titlebar-close").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("threads-tab"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }

    // Delete every thread this suite created (name starts with E2EPrefix). Best-effort: reloads to a
    // known state first, accepts the delete confirm() dialog, and loops until none remain. Never touches
    // non-E2E threads, so it's safe to run against a live workspace.
    private static async Task CleanupAsync(IPage page)
    {
        void AcceptConfirm(object? _, IDialog dialog) => _ = dialog.AcceptAsync();
        page.Dialog += AcceptConfirm;
        try
        {
            await OpenAppAsync(page);
            await OpenConversationsAsync(page);
            for (int i = 0; i < 100; i++)
            {
                ILocator rows = page.GetByTestId("thread-row").Filter(new LocatorFilterOptions { HasText = E2EPrefix });
                if (await rows.CountAsync() == 0)
                {
                    break;
                }

                await rows.First.ClickAsync();
                await page.GetByTestId("thread-delete").ClickAsync();
                await page.WaitForTimeoutAsync(400); // board reloads after the delete
            }

            await CloseModalAsync(page);
        }
        catch
        {
            // Best-effort cleanup — never fail a test on teardown.
        }
        finally
        {
            page.Dialog -= AcceptConfirm;
        }
    }

    // Rows in the Conversations board whose name contains the given text (fine for unique GUID names).
    private static ILocator RowsWith(IPage page, string text) =>
        page.GetByTestId("thread-row").Filter(new LocatorFilterOptions { HasText = text });

    // Exact thread names currently listed in the board — used for absence checks.
    private static async Task<string[]> ThreadNamesAsync(IPage page) =>
        (await page.GetByTestId("thread-name").AllInnerTextsAsync())
            .Select(name => name.Trim())
            .ToArray();
}
