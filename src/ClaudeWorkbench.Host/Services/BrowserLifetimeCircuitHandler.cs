using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ClaudeWorkbench.Host.Services;

// Feeds Blazor circuit connect/disconnect into BrowserPresenceTracker so a
// launcher-owned instance stops when its last tab closes. Registered only when
// CWB_EXIT_WITH_BROWSER=1.
public sealed class BrowserLifetimeCircuitHandler : CircuitHandler
{
    private readonly BrowserPresenceTracker tracker;

    public BrowserLifetimeCircuitHandler(BrowserPresenceTracker tracker)
    {
        this.tracker = tracker;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.Increment();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.Decrement();
        return Task.CompletedTask;
    }
}
