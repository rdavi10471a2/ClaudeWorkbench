using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ClaudeWorkbench.Host.Services;

// Feeds Blazor circuit connect/disconnect into BrowserPresenceTracker so a
// launcher-owned instance stops when its last tab closes. Registered only when
// CWB_EXIT_WITH_BROWSER=1.
public sealed class BrowserLifetimeCircuitHandler : CircuitHandler
{
    private readonly BrowserPresenceTracker tracker;
    private readonly ILogger<BrowserLifetimeCircuitHandler> logger;

    public BrowserLifetimeCircuitHandler(BrowserPresenceTracker tracker, ILogger<BrowserLifetimeCircuitHandler> logger)
    {
        this.tracker = tracker;
        this.logger = logger;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Browser circuit connected ({CircuitId}).", circuit.Id);
        tracker.Increment();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Browser circuit disconnected ({CircuitId}).", circuit.Id);
        tracker.Decrement();
        return Task.CompletedTask;
    }
}
