namespace ClaudeWorkbench.Host.Services;

// Tracks live browser (Blazor circuit) connections so a launcher-owned instance can
// shut itself down when its last tab closes — the tab is the instance's lifecycle
// owner. On graceful shutdown the host's SidecarProcessHost kills the sidecar too, so
// closing the window tears down the whole backend for that workspace.
//
// A short grace period tolerates refresh/navigation (the old circuit drops before the
// new one connects). Only armed after the FIRST connection, so the app doesn't stop
// before a tab has ever opened. Opt-in (CWB_EXIT_WITH_BROWSER=1) — a plain dev run is
// unaffected.
public sealed class BrowserPresenceTracker : IDisposable
{
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<BrowserPresenceTracker> logger;
    private readonly TimeSpan grace;
    private readonly object gate = new();
    private int liveConnections;
    private bool everConnected;
    private Timer? shutdownTimer;

    public BrowserPresenceTracker(IHostApplicationLifetime lifetime, ILogger<BrowserPresenceTracker> logger)
    {
        this.lifetime = lifetime;
        this.logger = logger;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("CWB_EXIT_GRACE_SECONDS"), out int parsed) && parsed > 0
            ? parsed
            : 3;
        grace = TimeSpan.FromSeconds(seconds);
    }

    public void Increment()
    {
        lock (gate)
        {
            liveConnections++;
            everConnected = true;
            CancelTimer();
        }
    }

    public void Decrement()
    {
        lock (gate)
        {
            liveConnections = Math.Max(0, liveConnections - 1);
            if (liveConnections == 0 && everConnected)
            {
                ArmShutdown();
            }
        }
    }

    private void ArmShutdown()
    {
        CancelTimer();
        logger.LogInformation(
            "Last browser tab closed; stopping this workbench instance in {Seconds}s unless a tab reconnects.",
            grace.TotalSeconds);
        shutdownTimer = new Timer(
            _ =>
            {
                bool stop;
                lock (gate)
                {
                    stop = liveConnections == 0;
                }

                if (stop)
                {
                    logger.LogInformation("No browser reconnected; shutting down this workbench instance.");
                    lifetime.StopApplication();
                }
            },
            null,
            grace,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        shutdownTimer?.Dispose();
        shutdownTimer = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            CancelTimer();
        }
    }
}
