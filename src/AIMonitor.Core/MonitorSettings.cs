namespace AIMonitor.Core;

public sealed record MonitorSettings(
    string RepositoryRoot,
    string RuntimeRoot,
    string WatchedSolutionPath)
{
    public string WatchedProjectFolder =>
        Path.GetDirectoryName(WatchedSolutionPath) ?? string.Empty;

    public static MonitorSettings Create(
        string repositoryRoot,
        string watchedSolutionPath,
        string? runtimeRoot = null)
    {
        string resolvedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string resolvedWatchedSolutionPath = Path.GetFullPath(watchedSolutionPath);
        string resolvedRuntimeRoot = Path.GetFullPath(
            runtimeRoot ?? Path.Combine(resolvedRepositoryRoot, "runtime"));

        return new MonitorSettings(
            resolvedRepositoryRoot,
            resolvedRuntimeRoot,
            resolvedWatchedSolutionPath);
    }
}
