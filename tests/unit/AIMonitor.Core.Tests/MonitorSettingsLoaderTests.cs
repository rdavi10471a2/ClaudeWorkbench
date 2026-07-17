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
        Assert.Equal(Path.Combine(config, "tools", "WinMergeU.exe"), Assert.Single(settings.WinMergeCandidatePaths));
    }

    [Fact]
    public async Task Load_uses_template_winmerge_paths_when_local_settings_are_legacy()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        string solutionPath = Path.Combine(root, "Fixture.slnx");
        string settingsPath = Path.Combine(config, "appsettings.json");
        string templatePath = Path.Combine(config, "appsettings.template.json");
        await File.WriteAllTextAsync(solutionPath, "<Solution />");
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "Monitor": {
                "WatchedSolutionPath": "{{solutionPath.Replace("\\", "\\\\")}}",
                "RuntimeRoot": "runtime"
              }
            }
            """);
        await File.WriteAllTextAsync(templatePath, """
            {
              "Monitor": {
                "WatchedSolutionPath": "C:\\Path\\To\\WatchedSolution.sln",
                "RuntimeRoot": "runtime",
                "WinMergeCandidatePaths": [
                  "tools/WinMergeU.exe"
                ]
              }
            }
            """);

        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(Path.Combine(config, "tools", "WinMergeU.exe"), Assert.Single(settings.WinMergeCandidatePaths));
    }

    [Fact]
    public void SaveLocal_writes_loadable_local_settings_file_and_preserves_diff_tool_paths()
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
                "RuntimeRoot": "runtime",
                "WinMergeCandidatePaths": [
                  "C:\\Tools\\WinMerge\\WinMergeU.exe"
                ]
              }
            }
            """);

        string settingsPath = MonitorSettingsLoader.SaveLocal(root, solutionPath);
        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(Path.Combine(root, "config", "appsettings.json"), settingsPath);
        Assert.Equal(Path.GetFullPath(solutionPath), settings.WatchedSolutionPath);
        Assert.Equal(Path.Combine(root, "runtime"), settings.RuntimeRoot);
        Assert.Equal(@"C:\Tools\WinMerge\WinMergeU.exe", Assert.Single(settings.WinMergeCandidatePaths));
    }

    [Fact]
    public void SaveLocal_seeds_template_winmerge_paths_for_legacy_local_settings()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string solutionPath = Path.Combine(root, "Watched", "Fixture.sln");
        string config = Path.Combine(root, "config");
        string settingsFile = Path.Combine(config, "appsettings.json");
        string templateFile = Path.Combine(config, "appsettings.template.json");
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
        File.WriteAllText(templateFile, """
            {
              "Monitor": {
                "WatchedSolutionPath": "C:\\Path\\To\\WatchedSolution.sln",
                "RuntimeRoot": "runtime",
                "WinMergeCandidatePaths": [
                  "C:\\Program Files\\WinMerge\\WinMergeU.exe",
                  "C:\\Program Files (x86)\\WinMerge\\WinMergeU.exe"
                ]
              }
            }
            """);

        string settingsPath = MonitorSettingsLoader.SaveLocal(root, solutionPath);
        MonitorSettings settings = MonitorSettingsLoader.Load(root, settingsPath);

        Assert.Equal(
            new[] { "C:\\Program Files\\WinMerge\\WinMergeU.exe", "C:\\Program Files (x86)\\WinMerge\\WinMergeU.exe" },
            settings.WinMergeCandidatePaths);
    }
}
