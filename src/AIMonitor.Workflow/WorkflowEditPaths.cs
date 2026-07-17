using AIMonitor.Core;

namespace AIMonitor.Workflow;

public sealed class WorkflowEditPaths
{
    public WorkflowEditPaths(MonitorSettings settings)
    {
        Settings = settings;
    }

    public MonitorSettings Settings { get; }

    public string WorkingRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(Settings),
        "working");

    public string MetadataRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(Settings),
        "workflow",
        "edits");

    public string HistoryRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(Settings),
        "workflow",
        "history");

    public string StagedRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(Settings),
        "workflow",
        "staged");

    public string RetrievalBackupsRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(Settings),
        "retrieval-backups");

    public string StagedRecordsRoot => Path.Combine(StagedRoot, "records");

    public string GetRelativeWatchedPath(string watchedFilePath)
    {
        string fullFilePath = Path.GetFullPath(watchedFilePath);
        string watchedRoot = Path.GetFullPath(Settings.WatchedProjectFolder);
        if (!fullFilePath.StartsWith(watchedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullFilePath.Equals(watchedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File is not under the watched solution folder: {watchedFilePath}");
        }

        return Path.GetRelativePath(watchedRoot, fullFilePath);
    }

    public string GetWorkingFilePath(string watchedFilePath)
    {
        return Path.Combine(WorkingRoot, GetRelativeWatchedPath(watchedFilePath));
    }

    public string GetRetrievalBackupDirectory(string watchedFilePath)
    {
        return Path.Combine(RetrievalBackupsRoot, GetRelativeWatchedPath(watchedFilePath));
    }

    public string GetMetadataPath(string watchedFilePath)
    {
        string relativePath = GetRelativeWatchedPath(watchedFilePath);
        return Path.Combine(MetadataRoot, $"{relativePath}.json");
    }

    public string GetStagedFilePath(string stagedRecordId, string relativePath)
    {
        return Path.Combine(StagedRoot, "files", stagedRecordId, relativePath);
    }

    public string GetStagedRecordPath(string stagedRecordId)
    {
        return Path.Combine(StagedRecordsRoot, $"{stagedRecordId}.json");
    }
}
