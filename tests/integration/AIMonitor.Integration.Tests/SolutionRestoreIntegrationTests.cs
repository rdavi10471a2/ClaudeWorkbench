using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Integration.Tests;

// Proves the host-run `dotnet restore` actually restores a real project (the NuGet-support gap: the
// design-time index load never restores, so a package-bearing/fresh project needs this). Uses the
// hermetic single-project fixture; a package-free SDK project restores offline in a second or two.
public sealed class SolutionRestoreIntegrationTests
{
    [Fact]
    public void Restore_succeeds_on_a_real_project()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        MonitorSettings settings = MonitorSettings.Create(
            fixture.RepositoryRoot,
            fixture.WatchedProjectPath,
            fixture.RuntimeRoot);

        SolutionRestoreService.RestoreResult result = new SolutionRestoreService().Restore(settings);

        Assert.False(result.IsError, string.Join(" | ", result.Diagnostics));
        Assert.Equal("restored", result.Status);
        Assert.Equal(fixture.WatchedProjectPath, result.SolutionPath);
    }
}
