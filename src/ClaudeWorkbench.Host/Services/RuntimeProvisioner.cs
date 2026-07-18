using AIMonitor.Core;
using ClaudeWorkbench.Host.Tasks;

namespace ClaudeWorkbench.Host.Services;

// Provisions a watched workspace's per-solution runtime: the directory skeleton
// and the task-board database. Runs on workspace selection and on startup so a
// configured workspace is always fully set up — even before the Tasks UI exists,
// so the task capability is never lost. Idempotent.
public sealed class RuntimeProvisioner
{
    private static readonly string[] RuntimeSubdirectories =
    [
        "data",
        "workflow",
        "reviews",
        "logs",
        "planning",
        Path.Combine("planning", "task-memory"),
        "uploads",
    ];

    public void EnsureRuntime(MonitorSettings settings)
    {
        string root = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings);
        foreach (string subdirectory in RuntimeSubdirectories)
        {
            Directory.CreateDirectory(Path.Combine(root, subdirectory));
        }

        // Create/upgrade the task-board schema (matches the Codex board.sqlite layout
        // so the full task repository ports in cleanly later).
        new WorkflowTaskBoardDatabase(Path.Combine(root, "planning", "board.sqlite")).EnsureCreated();
    }
}
