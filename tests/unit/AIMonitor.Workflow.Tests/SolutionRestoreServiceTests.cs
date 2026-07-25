using AIMonitor.Core;

namespace AIMonitor.Workflow.Tests;

public sealed class SolutionRestoreServiceTests
{
    // The fast, deterministic guard: a missing solution reports an error without shelling out to dotnet.
    // (The happy-path real `dotnet restore` is exercised in the integration suite against a real project.)
    [Fact]
    public void Restore_reports_missing_solution_without_running_dotnet()
    {
        string temp = Path.Combine(Path.GetTempPath(), "cwb-restore-" + Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(
            temp,
            Path.Combine(temp, "DoesNotExist.slnx"),
            Path.Combine(temp, "runtime"));

        SolutionRestoreService.RestoreResult result = new SolutionRestoreService().Restore(settings);

        Assert.True(result.IsError);
        Assert.Equal("missing-solution", result.Status);
        Assert.Empty(result.Diagnostics);
    }
}
