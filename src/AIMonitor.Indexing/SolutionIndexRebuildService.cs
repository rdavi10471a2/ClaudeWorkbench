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
        new WorkflowEditService(settings).MarkAllIndexesFresh();
        return summary;
    }

    // ADR-0007 accept path: reindex by READING the build-after-accept's output (generated .g.cs + harvested
    // refs), no compile. Marks every index fresh exactly like a full RebuildAsync, because it reindexed the
    // whole (single) project the build produced.
    public async Task<SolutionIndexSummary> RebuildFromBuildOutputAsync(
        MonitorSettings settings,
        string projectPath,
        string generatedRoot,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(store);
        SolutionIndexSummary summary = await builder.RebuildFromBuildOutputAsync(
            settings, projectPath, generatedRoot, references, cancellationToken, timingSink);
        new WorkflowEditService(settings).MarkAllIndexesFresh();
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
