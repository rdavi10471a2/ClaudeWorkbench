namespace AIMonitor.Core;

public sealed record MonitorSettings(
    string RepositoryRoot,
    string RuntimeRoot,
    string WatchedSolutionPath,
    IReadOnlyList<string> WinMergeCandidatePaths)
{
    public string WatchedProjectFolder =>
        Path.GetDirectoryName(WatchedSolutionPath) ?? string.Empty;

    public static MonitorSettings Create(
        string repositoryRoot,
        string watchedSolutionPath,
        string? runtimeRoot = null,
        IReadOnlyList<string>? winMergeCandidatePaths = null)
    {
        string resolvedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string resolvedWatchedSolutionPath = Path.GetFullPath(watchedSolutionPath);
        string resolvedRuntimeRoot = Path.GetFullPath(
            runtimeRoot ?? Path.Combine(resolvedRepositoryRoot, "runtime"));

        return new MonitorSettings(
            resolvedRepositoryRoot,
            resolvedRuntimeRoot,
            resolvedWatchedSolutionPath,
            winMergeCandidatePaths ?? []);
    }
}
