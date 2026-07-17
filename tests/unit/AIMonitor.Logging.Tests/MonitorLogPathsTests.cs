using AIMonitor.Core;

namespace AIMonitor.Logging.Tests;

public sealed class MonitorLogPathsTests
{
    [Fact]
    public void GetDefaultLogPath_uses_runtime_root()
    {
        MonitorSettings settings = MonitorSettings.Create(
            "C:\\Monitor",
            "C:\\Watched\\Watched.sln",
            "C:\\Monitor\\runtime");

        string logPath = MonitorLogPaths.GetDefaultLogPath(settings);

        Assert.Equal(
            Path.GetFullPath("C:\\Monitor\\runtime\\logs\\aimonitor.ndjson"),
            logPath);
    }
}
