using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Selecting a watched solution: point the manager at it, persist the choice to the
// monitor config so it sticks across restarts, then initialize its runtime (build
// the index). Persistence goes back to the SAME file the host was started with.
public sealed class WorkspaceCoordinator
{
    private readonly WorkspaceManager workspace;
    private readonly RuntimeProvisioner provisioner;
    private readonly MonitorConfigPath configPath;

    public WorkspaceCoordinator(WorkspaceManager workspace, RuntimeProvisioner provisioner, MonitorConfigPath configPath)
    {
        this.workspace = workspace;
        this.provisioner = provisioner;
        this.configPath = configPath;
    }

    public async Task SelectAsync(string watchedSolutionPath)
    {
        workspace.SwitchTo(watchedSolutionPath);
        // Explicitly the loaded path: defaulting would write <repo-root>\config\appsettings.json,
        // which is only the same file when --config happens to point there. Under the Launcher
        // (or any explicit --config) the choice would be saved somewhere nothing reads.
        MonitorSettingsLoader.SaveLocal(workspace.RepositoryRoot, watchedSolutionPath, workspace.RuntimeRoot, configPath.Value);
        // Full runtime: skeleton dirs + task-board DB, then build the index.
        provisioner.EnsureRuntime(workspace.Settings);
        await workspace.ProvisionAsync();
    }
}

// The monitor config file this host was started with (--config, or the default under the
// repo root). Registered so that whatever reads it and whatever writes it stay in step.
public sealed record MonitorConfigPath(string Value);
