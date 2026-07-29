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
        string indexInputPath = Path.GetFullPath(settings.WatchedSolutionPath);
        System.Diagnostics.Stopwatch snapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        MSBuildSolutionSnapshot snapshot;

        // ADR-0007, opt-in (CWB_INDEX_RIDES_BUILD=1): the index rides the build. One real build emits the
        // generated files + resolved refs; the index is a Roslyn pass over that output — accurate razor (from
        // the build's .g.cs), real paths, no MSBuildWorkspace/BuildHost. Single-project only for now; anything
        // else falls back to the existing in-proc loader (unchanged default).
        string? buildProject = IndexRidesBuild() ? ResolveSingleProject(settings.WatchedSolutionPath) : null;
        if (buildProject is not null)
        {
            CompileIndexTrace.Record(
                settings,
                "index-from-build.start",
                buildProject,
                "index rides the build (ADR-0007): one real build emits generated + refs, the index reads them — no MSBuildWorkspace/BuildHost");
            BuildOutputSnapshotResult buildResult = await new BuildOutputSnapshotLoader()
                .OpenProjectFromBuildAsync(buildProject, cancellationToken: cancellationToken);
            snapshot = buildResult.Snapshot;
            snapshotStopwatch.Stop();
            CompileIndexTrace.Record(
                settings,
                "index-from-build.done",
                buildProject,
                $"buildSucceeded={buildResult.BuildSucceeded} projects={snapshot.Projects.Count} ms={snapshotStopwatch.ElapsedMilliseconds}");
        }
        else
        {
            CompileIndexTrace.Record(
                settings,
                "index-compile.start",
                indexInputPath,
                "in-proc MSBuildWorkspace open — the index runs its OWN compile of the watched source tree (this is NOT the gate's out-of-proc dotnet build)");
            string extension = Path.GetExtension(settings.WatchedSolutionPath);
            snapshot = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? await workspaceLoader.OpenProjectAsync(settings.WatchedSolutionPath, cancellationToken, timingSink)
                : await workspaceLoader.OpenSolutionAsync(settings.WatchedSolutionPath, cancellationToken, timingSink);
            snapshotStopwatch.Stop();
            CompileIndexTrace.Record(
                settings,
                "index-compile.done",
                indexInputPath,
                $"in-proc compile ms={snapshotStopwatch.ElapsedMilliseconds}");
        }
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

        CompileIndexTrace.Record(
            settings,
            "index-file-refresh.start",
            normalizedProjectPath,
            $"in-proc project-scoped reload of {normalizedFilePaths.Length} file(s): {string.Join(", ", normalizedFilePaths.Select(Path.GetFileName))}");
        System.Diagnostics.Stopwatch msbuildSnapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        MSBuildProjectFileSnapshot fileSnapshot = await workspaceLoader.OpenProjectFilesAsync(
            normalizedProjectPath,
            normalizedFilePaths,
            existingSymbolsByIdentity,
            cancellationToken,
            timingSink);
        msbuildSnapshotStopwatch.Stop();
        CompileIndexTrace.Record(
            settings,
            "index-file-refresh.done",
            normalizedProjectPath,
            $"in-proc reload ms={msbuildSnapshotStopwatch.ElapsedMilliseconds} documents={fileSnapshot.Documents.Count} symbols={fileSnapshot.Symbols.Count}");
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

    private static bool IndexRidesBuild()
    {
        return Environment.GetEnvironmentVariable("CWB_INDEX_RIDES_BUILD") is "1" or "true" or "TRUE";
    }

    // ADR-0007 build-output path is single-project for now: a .csproj directly, or a .slnx containing exactly
    // one project. Anything else returns null so the existing loader handles it.
    private static string? ResolveSingleProject(string solutionOrProjectPath)
    {
        string extension = Path.GetExtension(solutionOrProjectPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(solutionOrProjectPath);
        }

        if (!extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(solutionOrProjectPath)) ?? string.Empty;
            string[] projects = System.Text.RegularExpressions.Regex
                .Matches(
                    File.ReadAllText(solutionOrProjectPath),
                    "Path=\"([^\"]+\\.csproj)\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(match => Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return projects.Length == 1 ? projects[0] : null;
        }
        catch
        {
            return null;
        }
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
