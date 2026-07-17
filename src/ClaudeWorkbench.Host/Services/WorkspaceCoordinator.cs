using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Selecting a watched solution: point the manager at it, persist the choice to the
// monitor config so it sticks across restarts, then initialize its runtime (build
// the index). Persistence goes to <repo-root>/config/appsettings.json.
public sealed class WorkspaceCoordinator
{
    private readonly WorkspaceManager workspace;
    private readonly RuntimeProvisioner provisioner;

    public WorkspaceCoordinator(WorkspaceManager workspace, RuntimeProvisioner provisioner)
    {
        this.workspace = workspace;
        this.provisioner = provisioner;
    }

    public async Task SelectAsync(string watchedSolutionPath)
    {
        workspace.SwitchTo(watchedSolutionPath);
        MonitorSettingsLoader.SaveLocal(workspace.RepositoryRoot, watchedSolutionPath, workspace.RuntimeRoot);
        // Full runtime: skeleton dirs + task-board DB, then build the index.
        provisioner.EnsureRuntime(workspace.Settings);
        await workspace.ProvisionAsync();
    }
}
