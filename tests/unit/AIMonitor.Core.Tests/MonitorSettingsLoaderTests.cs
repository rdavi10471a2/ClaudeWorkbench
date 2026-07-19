using AIMonitor.Core;

namespace AIMonitor.Core.Tests;

public sealed class MonitorSettingsLoaderTests
{
    [Fact]
    public async Task Load_reads_single_watched_solution_path()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        string solutionPath = Path.Combine(root, "Fixture.slnx");
        string settingsPath = Path.Combine(config, "appsettings.json");
        await File.WriteAllTextAsync(solutionPath, "<Solution />");
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "Monitor": {
                "WatchedSolutionPath": "{{solutionPath.Replace("\\", "\\\\")}}",
                "RuntimeRoot": "runtime"
              }
            }
            """);

        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(Path.GetFullPath(solutionPath), settings.WatchedSolutionPath);
        Assert.Equal(Path.Combine(root, "runtime"), settings.RuntimeRoot);
    }

    [Fact]
    public async Task Load_ignores_retired_settings_keys()
    {
        // Settings files in the wild still carry the retired external-diff-tool key. The loader
        // reads only the keys it knows, so an unknown leftover must not fail the load.
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        string solutionPath = Path.Combine(root, "Fixture.slnx");
        string settingsPath = Path.Combine(config, "appsettings.json");
        await File.WriteAllTextAsync(solutionPath, "<Solution />");
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "Monitor": {
                "WatchedSolutionPath": "{{solutionPath.Replace("\\", "\\\\")}}",
                "RuntimeRoot": "runtime",
                "WinMergeCandidatePaths": [
                  "tools/WinMergeU.exe"
                ]
              }
            }
            """);

        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(Path.GetFullPath(solutionPath), settings.WatchedSolutionPath);
        Assert.Equal(Path.Combine(root, "runtime"), settings.RuntimeRoot);
    }

    [Fact]
    public void SaveLocal_writes_loadable_local_settings_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string solutionPath = Path.Combine(root, "Watched", "Fixture.sln");
        string config = Path.Combine(root, "config");
        string settingsFile = Path.Combine(config, "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(solutionPath)!);
        Directory.CreateDirectory(config);
        File.WriteAllText(solutionPath, string.Empty);
        File.WriteAllText(settingsFile, """
            {
              "Monitor": {
                "WatchedSolutionPath": "old.sln",
                "RuntimeRoot": "runtime"
              }
            }
            """);

        string settingsPath = MonitorSettingsLoader.SaveLocal(root, solutionPath);
        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(Path.Combine(root, "config", "appsettings.json"), settingsPath);
        Assert.Equal(Path.GetFullPath(solutionPath), settings.WatchedSolutionPath);
        Assert.Equal(Path.Combine(root, "runtime"), settings.RuntimeRoot);
    }
}
