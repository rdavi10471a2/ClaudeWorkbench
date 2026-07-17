namespace AIMonitor.Core;

public sealed record WatchedSolutionInfo(
    string SolutionPath,
    string ProjectFolder,
    bool SolutionExists)
{
    public static WatchedSolutionInfo FromSettings(MonitorSettings settings)
    {
        return new WatchedSolutionInfo(
            settings.WatchedSolutionPath,
            settings.WatchedProjectFolder,
            File.Exists(settings.WatchedSolutionPath));
    }
}
