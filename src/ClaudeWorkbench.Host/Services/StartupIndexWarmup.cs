using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;

namespace ClaudeWorkbench.Host.Services;

// Warms the solution index ONCE per app run, triggered AFTER the Blazor circuit is up (see
// IndexWarmupCircuitHandler) rather than at startup. This deliberately keeps both the freshness
// CHECK and any rebuild off the first-paint path: a full MSBuild/Roslyn reindex — and even the
// staleness check, which re-hashes every indexed file — used to run at ApplicationStarted and
// starved Blazor's initial prerender + hydration, giving a long blank screen on every launch.
//
// The check is fully in-process (SolutionIndexQueryService over the host-owned SQLite index) — it
// does NOT need the sidecar or an MCP round-trip. We rebuild only when the index is missing or
// stale, so a clean reopen does no indexing at all and loads instantly.
public sealed class StartupIndexWarmup
{
    private readonly WorkspaceManager workspace;
    private readonly IndexRebuildStatus rebuildStatus;
    private readonly IMonitorLogger logger;
    private int started;

    public StartupIndexWarmup(WorkspaceManager workspace, IndexRebuildStatus rebuildStatus, IMonitorLogger logger)
    {
        this.workspace = workspace;
        this.rebuildStatus = rebuildStatus;
        this.logger = logger;
    }

    // Fire-and-forget, run-once. Safe to call on every circuit connection; only the first does work.
    public void EnsureWarmOnce()
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            return;
        }

        if (!workspace.HasWorkspace)
        {
            return;
        }

        _ = Task.Run(WarmAsync);
    }

    private async Task WarmAsync()
    {
        try
        {
            MonitorStatusResult status = SolutionIndexQueryService.Create(workspace.Settings).GetMonitorStatus();
            if (status.DatabaseExists && !status.RebuildRequired && status.StaleFileCount == 0)
            {
                logger.Write(MonitorLogLevel.Information, "Host", "startup-index-warm-skip",
                    "Index is current; skipping the startup rebuild.");
                return;
            }

            logger.Write(MonitorLogLevel.Information, "Host", "startup-index-warm-begin",
                status.DatabaseExists
                    ? $"Index needs a rebuild (stale files: {status.StaleFileCount}, rebuildRequired: {status.RebuildRequired})."
                    : "No index yet; building it.");

            using (rebuildStatus.Begin())
            {
                // Restore NuGet packages before the (re)index. The index loads via MSBuildWorkspace,
                // which reads restored assets and never restores itself — so a freshly-opened
                // package-bearing project would fail to resolve types without this. No-op when current.
                SolutionRestoreService.RestoreResult restore = new SolutionRestoreService().Restore(workspace.Settings);
                logger.Write(
                    restore.IsError ? MonitorLogLevel.Warning : MonitorLogLevel.Information,
                    "Host",
                    "startup-restore",
                    restore.IsError
                        ? restore.Message + " " + string.Join(" | ", restore.Diagnostics)
                        : restore.Message);

                await workspace.ProvisionAsync();
            }
        }
        catch (Exception exception)
        {
            logger.Write(MonitorLogLevel.Warning, "Host", "startup-index-warm-failed", exception.Message);
        }
    }
}
