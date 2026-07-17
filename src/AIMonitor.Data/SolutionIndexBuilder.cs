using AIMonitor.Core;
using AIMonitor.MSBuild;

namespace AIMonitor.Data;

public sealed class SolutionIndexBuilder
{
    private readonly MSBuildWorkspaceLoader workspaceLoader;
    private readonly SolutionIndexStore store;

    public SolutionIndexBuilder(MSBuildWorkspaceLoader workspaceLoader, SolutionIndexStore store)
    {
        this.workspaceLoader = workspaceLoader;
        this.store = store;
    }

    public SolutionIndexBuilder(SolutionIndexStore store)
        : this(new MSBuildWorkspaceLoader(), store)
    {
    }

    public async Task<SolutionIndexSummary> RebuildAsync(
        MonitorSettings settings,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        System.Diagnostics.Stopwatch snapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string extension = Path.GetExtension(settings.WatchedSolutionPath);
        MSBuildSolutionSnapshot snapshot = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? await workspaceLoader.OpenProjectAsync(settings.WatchedSolutionPath, cancellationToken, timingSink)
            : await workspaceLoader.OpenSolutionAsync(settings.WatchedSolutionPath, cancellationToken, timingSink);
        snapshotStopwatch.Stop();
        timingSink?.Invoke(
            "index.full.snapshot",
            snapshotStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["inputPath"] = Path.GetFullPath(settings.WatchedSolutionPath)
            });

        System.Diagnostics.Stopwatch saveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        SolutionIndexSummary summary = store.SaveSnapshot(snapshot, timingSink);
        saveStopwatch.Stop();
        timingSink?.Invoke(
            "index.full.sqlite-save",
            saveStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["inputPath"] = Path.GetFullPath(settings.WatchedSolutionPath)
            });
        return summary;
    }

    public async Task<SolutionIndexSummary> RefreshProjectFilesAsync(
        MonitorSettings settings,
        string projectPath,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        string normalizedProjectPath = Path.GetFullPath(projectPath);
        string[] normalizedFilePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFilePaths.Length == 0)
        {
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));
        }

        System.Diagnostics.Stopwatch existingSymbolsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string, MSBuildSymbolSnapshot> existingSymbolsByIdentity = store.ListSymbols()
            .Where(symbol => !normalizedFilePaths.Contains(Path.GetFullPath(symbol.FilePath), StringComparer.OrdinalIgnoreCase))
            .GroupBy(CreateSymbolIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => ToSnapshot(group.First()), StringComparer.Ordinal);
        existingSymbolsStopwatch.Stop();
        timingSink?.Invoke(
            "index.project.existing-symbols",
            existingSymbolsStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["projectPath"] = normalizedProjectPath,
                ["fileCount"] = normalizedFilePaths.Length.ToString(),
                ["retainedSymbolIdentityCount"] = existingSymbolsByIdentity.Count.ToString()
            });

        System.Diagnostics.Stopwatch msbuildSnapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        MSBuildProjectFileSnapshot fileSnapshot = await workspaceLoader.OpenProjectFilesAsync(
            normalizedProjectPath,
            normalizedFilePaths,
            existingSymbolsByIdentity,
            cancellationToken,
            timingSink);
        msbuildSnapshotStopwatch.Stop();
        timingSink?.Invoke(
            "index.project.msbuild-snapshot",
            msbuildSnapshotStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["projectPath"] = normalizedProjectPath,
                ["fileCount"] = normalizedFilePaths.Length.ToString(),
                ["documentCount"] = fileSnapshot.Documents.Count.ToString(),
                ["symbolCount"] = fileSnapshot.Symbols.Count.ToString(),
                ["referenceCount"] = fileSnapshot.References.Count.ToString()
            });

        System.Diagnostics.Stopwatch sqliteReplaceStopwatch = System.Diagnostics.Stopwatch.StartNew();
        SolutionIndexSummary summary = store.ReplaceProjectFiles(
            Path.GetFullPath(settings.WatchedSolutionPath),
            normalizedProjectPath,
            normalizedFilePaths,
            fileSnapshot.Documents,
            fileSnapshot.Symbols,
            fileSnapshot.References,
            timingSink);
        sqliteReplaceStopwatch.Stop();
        timingSink?.Invoke(
            "index.project.sqlite-replace",
            sqliteReplaceStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["projectPath"] = normalizedProjectPath,
                ["fileCount"] = normalizedFilePaths.Length.ToString()
            });
        return summary;
    }

    private static string CreateSymbolIdentity(IndexedSymbolRow symbol)
    {
        return StableIdentifier.FromParts(
            "symbol-identity",
            symbol.FilePath,
            symbol.Kind,
            symbol.Signature,
            symbol.StartLine.ToString());
    }

    private static MSBuildSymbolSnapshot ToSnapshot(IndexedSymbolRow symbol)
    {
        return new MSBuildSymbolSnapshot(
            symbol.StableKey,
            symbol.Name,
            symbol.Kind,
            symbol.Namespace,
            symbol.ContainingType,
            symbol.FilePath,
            symbol.StartLine,
            symbol.EndLine,
            symbol.Signature,
            symbol.Accessibility,
            symbol.IsStatic,
            symbol.IsAbstract,
            symbol.IsSealed,
            symbol.IsVirtual,
            symbol.IsOverride,
            symbol.MethodKind);
    }
}
