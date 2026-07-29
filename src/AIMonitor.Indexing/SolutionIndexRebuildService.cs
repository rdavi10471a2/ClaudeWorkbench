using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.MSBuild;
using AIMonitor.Workflow;

namespace AIMonitor.Indexing;

public sealed class SolutionIndexRebuildService
{
    public async Task<SolutionIndexSummary> RebuildAsync(
        MonitorSettings settings,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(store);
        SolutionIndexSummary summary = await builder.RebuildAsync(settings, cancellationToken, timingSink);
        // Red build → the index was preserved, not rewritten; do NOT mark the (now-stale) index fresh.
        if (summary.Built)
        {
            new WorkflowEditService(settings).MarkAllIndexesFresh();
        }

        return summary;
    }

    // ADR-0007 accept path: reindex by READING the build-after-accept's WHOLE-solution output (every project's
    // generated .g.cs + per-project refs), no compile. Marks every index fresh exactly like a full RebuildAsync,
    // because it reindexed the whole solution the build produced. One path for 1..N projects.
    public async Task<SolutionIndexSummary> RebuildFromBuildOutputAsync(
        MonitorSettings settings,
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(store);
        SolutionIndexSummary summary = await builder.RebuildFromBuildOutputAsync(
            settings, solutionPath, projectPaths, cancellationToken, timingSink);
        if (summary.Built)
        {
            new WorkflowEditService(settings).MarkAllIndexesFresh();
        }

        return summary;
    }

    public async Task<SolutionIndexSummary> RefreshProjectFilesAsync(
        MonitorSettings settings,
        string projectPath,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(store);
        return await builder.RefreshProjectFilesAsync(settings, projectPath, filePaths, cancellationToken, timingSink);
    }
}
