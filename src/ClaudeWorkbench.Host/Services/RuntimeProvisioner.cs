using AIMonitor.Core;

namespace ClaudeWorkbench.Host.Services;

// Provisions a watched workspace's per-solution runtime directory skeleton. Runs on
// workspace selection and on startup so a configured workspace is always fully set
// up. Idempotent.
public sealed class RuntimeProvisioner
{
    private static readonly string[] RuntimeSubdirectories =
    [
        "data",
        "workflow",
        "reviews",
        "logs",
        "planning",
        "uploads",
        // Ungoverned agent scratchpad: the agent writes freeform notes/plans here via the
        // write_note MCP tool. Physically under runtime, OUTSIDE watched source, so it can
        // never sneak an edit into governed source. See AIMonitorTools.Notes.
        "agent-notes",
    ];

    public void EnsureRuntime(MonitorSettings settings)
    {
        string root = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings);
        foreach (string subdirectory in RuntimeSubdirectories)
        {
            Directory.CreateDirectory(Path.Combine(root, subdirectory));
        }
    }
}
