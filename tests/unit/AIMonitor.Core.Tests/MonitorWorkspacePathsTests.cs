using AIMonitor.Core;

namespace AIMonitor.Core.Tests;

public sealed class MonitorWorkspacePathsTests
{
    [Fact]
    public void GetWatchedSolutionWorkspaceRoot_uses_runtime_owned_solution_folder()
    {
        MonitorSettings settings = MonitorSettings.Create(
            "C:\\Monitor",
            "C:\\Watched\\Sample App.sln",
            "C:\\Monitor\\runtime");

        string workspaceRoot = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings);

        Assert.StartsWith(
            Path.GetFullPath("C:\\Monitor\\runtime\\watched-solutions\\Sample App-"),
            workspaceRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetWatchedSolutionWorkspaceRoot_keeps_same_solution_names_distinct()
    {
        MonitorSettings first = MonitorSettings.Create(
            "C:\\Monitor",
            "C:\\One\\App.sln",
            "C:\\Monitor\\runtime");
        MonitorSettings second = MonitorSettings.Create(
            "C:\\Monitor",
            "C:\\Two\\App.sln",
            "C:\\Monitor\\runtime");

        string firstRoot = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(first);
        string secondRoot = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(second);

        Assert.NotEqual(firstRoot, secondRoot);
    }
}
