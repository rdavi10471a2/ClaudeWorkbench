using AIMonitor.Core;

namespace AIMonitor.Data.Tests;

public sealed class MonitorDataPathsTests
{
    [Fact]
    public void GetDefaultIndexDatabasePath_lives_under_watched_solution_workspace()
    {
        MonitorSettings settings = MonitorSettings.Create(
            "C:\\Monitor",
            "C:\\Watched\\Watched.sln",
            "C:\\Monitor\\runtime");

        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        string workspaceRoot = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings);

        Assert.Equal(
            Path.Combine(workspaceRoot, "data", "solution-index.sqlite"),
            databasePath);
    }
}
