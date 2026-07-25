using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Threads;

// The per-workspace facts the thread layer needs: where threads.sqlite lives and the cwd a
// resumed session must use. An interface so ThreadService is testable without the full
// WorkspaceManager, and so the paths retarget when the operator switches watched workspaces.
public interface IThreadWorkspace
{
    // runtime\<workspace>\data\threads.sqlite — its OWN db, beside (not inside) the solution index.
    string ThreadsDatabasePath { get; }

    // The watched-solution folder = the SDK cwd. REQUIRED for a correct resume (resuming from a
    // different cwd silently starts a fresh session).
    string Cwd { get; }
}

// Live adapter over the current watched workspace.
public sealed class WorkspaceThreadPaths : IThreadWorkspace
{
    private readonly WorkspaceManager workspace;

    public WorkspaceThreadPaths(WorkspaceManager workspace)
    {
        this.workspace = workspace;
    }

    public string ThreadsDatabasePath => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(workspace.Settings),
        "data",
        "threads.sqlite");

    public string Cwd => Path.GetFullPath(workspace.Settings.WatchedProjectFolder);
}
