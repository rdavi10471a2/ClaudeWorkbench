using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// First browser-visible coverage of the Assistant page. These target the data-testid hooks added to
// AssistantTab.razor (composer-input, transcript, message-*, tool-call, auto-approve, turn-activity),
// falling back to role+name for the Radzen buttons (Submit Turn / Stop / New Thread), which do not
// carry stable testids.
//
// This is a SCAFFOLD: it proves the harness, the hooks, and the round-trip through a real browser. The
// next step (a scripted "agent in the middle" that emits canned turns through the UI so a full loop is
// deterministic AND watchable) builds on this fixture.
public sealed class AssistantPageSmokeTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture fixture;

    public AssistantPageSmokeTests(PlaywrightFixture fixture)
    {
        this.fixture = fixture;
    }

    [SkippableFact]
    public async Task Assistant_page_loads_and_shows_the_composer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await fixture.Page.GotoAsync(E2EEnvironment.BaseUrl);

        ILocator composer = fixture.Page.GetByTestId("composer-input");
        await Assertions.Expect(composer).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000,
        });
    }

    [SkippableFact]
    public async Task Composer_accepts_typed_text()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await fixture.Page.GotoAsync(E2EEnvironment.BaseUrl);

        ILocator composer = fixture.Page.GetByTestId("composer-input");
        await composer.FillAsync("List the projects in the watched solution.");

        // Proves the hook resolves and the two-way bound textarea round-trips through a real browser.
        await Assertions.Expect(composer).ToHaveValueAsync("List the projects in the watched solution.");

        // The submit control is a RadzenButton (no testid) - located by role+accessible name.
        ILocator submit = fixture.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit Turn" });
        await Assertions.Expect(submit).ToBeVisibleAsync();
    }
}
