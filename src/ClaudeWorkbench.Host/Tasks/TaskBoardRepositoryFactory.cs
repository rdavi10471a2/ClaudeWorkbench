using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Tasks;

// Single source of truth for the task board's on-disk location. Both the Blazor
// view service and the MCP task tools build their repository from here, so the
// board paths (planning/board.sqlite + planning/task-memory under the watched-
// solution workspace root) are defined once.
public sealed class TaskBoardRepositoryFactory
{
    private readonly WorkspaceManager workspace;

    public TaskBoardRepositoryFactory(WorkspaceManager workspace)
    {
        this.workspace = workspace;
    }

    public bool HasWorkspace => workspace.HasWorkspace;

    public string? WatchedSolutionPath => workspace.WatchedSolutionPath;

    public string WorkspaceRoot => MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(workspace.Settings);

    public WorkflowTaskBoardRepository Create()
    {
        string root = WorkspaceRoot;
        return new WorkflowTaskBoardRepository(
            Path.Combine(root, "planning", "board.sqlite"),
            Path.Combine(root, "planning", "task-memory"));
    }
}
