using Microsoft.Playwright;

namespace ClaudeWorkbench.E2E.Tests;

// Owns the browser for a test class. Deliberately does NOT throw when prerequisites are missing: it
// records a SkipReason instead, and the tests call Skip.If(SkipReason) so the suite stays green on a
// machine without a running Host or installed browsers. The Host-reachability check runs first, so the
// no-Host skip path never needs browser binaries at all.
public sealed class PlaywrightFixture : IAsyncLifetime
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;

    public IPage Page { get; private set; } = null!;

    public string? SkipReason { get; private set; }

    // Set when video recording is on; the .webm is finalized on context dispose.
    public string? VideoDir { get; private set; }

    public async Task InitializeAsync()
    {
        if (!E2EEnvironment.IsHostReachable())
        {
            SkipReason =
                $"Host UI not reachable at {E2EEnvironment.BaseUrl}. Start the Host (or the launcher), " +
                "or set AIMW_E2E_BASEURL, then re-run. See tests/e2e/.../README.md.";
            return;
        }

        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = !E2EEnvironment.Headed,
                SlowMo = E2EEnvironment.SlowMoMs,
            });

            BrowserNewContextOptions contextOptions = new()
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            };
            if (E2EEnvironment.RecordVideo)
            {
                VideoDir = Path.Combine(E2EEnvironment.ArtifactsDir, "video");
                Directory.CreateDirectory(VideoDir);
                contextOptions.RecordVideoDir = VideoDir;
                contextOptions.RecordVideoSize = new RecordVideoSize { Width = 1440, Height = 900 };
            }

            context = await browser.NewContextAsync(contextOptions);

            if (E2EEnvironment.RecordTrace)
            {
                await context.Tracing.StartAsync(new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                    Sources = true,
                });
            }

            Page = await context.NewPageAsync();
        }
        catch (Exception ex)
        {
            // Almost always "browsers are not installed": Playwright needs a one-time
            //   pwsh tests/e2e/ClaudeWorkbench.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install
            // Skip rather than fail so the rest of the suite is unaffected.
            SkipReason =
                "Playwright could not launch a browser (are the browsers installed? run playwright.ps1 " +
                $"install - see README.md). Underlying error: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (context is not null)
        {
            if (E2EEnvironment.RecordTrace)
            {
                Directory.CreateDirectory(E2EEnvironment.ArtifactsDir);
                await context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(E2EEnvironment.ArtifactsDir, "trace.zip"),
                });
            }

            // Disposing the context flushes the .webm to VideoDir.
            await context.DisposeAsync();
        }

        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
    }
}
