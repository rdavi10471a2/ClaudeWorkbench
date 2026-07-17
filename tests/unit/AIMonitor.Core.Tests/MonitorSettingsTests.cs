using AIMonitor.Core;

namespace AIMonitor.Core.Tests;

public sealed class MonitorSettingsTests
{
    [Fact]
    public void Create_resolves_absolute_paths()
    {
        MonitorSettings settings = MonitorSettings.Create(
            ".",
            Path.Combine(".", "Sample.sln"));

        Assert.True(Path.IsPathFullyQualified(settings.RepositoryRoot));
        Assert.True(Path.IsPathFullyQualified(settings.RuntimeRoot));
        Assert.True(Path.IsPathFullyQualified(settings.WatchedSolutionPath));
    }
}
