using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Selecting a watched solution: point the manager at it, persist the choice to the
// monitor config so it sticks across restarts, then initialize its runtime (build
// the index). Persistence goes to <repo-root>/config/appsettings.json.
public sealed class WorkspaceCoordinator
{
    private readonly WorkspaceManager workspace;

    public WorkspaceCoordinator(WorkspaceManager workspace)
    {
        this.workspace = workspace;
    }

    public async Task SelectAsync(string watchedSolutionPath)
    {
        workspace.SwitchTo(watchedSolutionPath);
        MonitorSettingsLoader.SaveLocal(workspace.RepositoryRoot, watchedSolutionPath, workspace.RuntimeRoot);
        await workspace.ProvisionAsync();
    }
}
