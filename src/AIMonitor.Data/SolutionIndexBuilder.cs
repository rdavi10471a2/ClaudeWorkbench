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
        string? buildProject = IndexRidesBuild.Enabled ? WatchedSolutionInfo.ResolveSingleProject(settings.WatchedSolutionPath) : null;
        IReadOnlyList<string> buildSolutionProjects = IndexRidesBuild.Enabled && buildProject is null
            ? WatchedSolutionInfo.ResolveAllProjects(settings.WatchedSolutionPath)
            : [];
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
        else if (buildSolutionProjects.Count > 1)
        {
            // ADR-0007 whole-solution read: enumerate every project, one incremental build of the solution emits
            // all their generated files + per-project refs, then the index reads them into one multi-project
            // Roslyn solution (cross-project references resolved). Same read engine as single-project — no fallback.
            CompileIndexTrace.Record(
                settings,
                "index-from-build.start",
                indexInputPath,
                $"index rides the build (ADR-0007): whole-solution read of {buildSolutionProjects.Count} projects — one build emits generated + per-project refs, the index reads them");
            BuildOutputSnapshotResult buildResult = await new BuildOutputSnapshotLoader()
                .OpenSolutionFromBuildAsync(Path.GetFullPath(settings.WatchedSolutionPath), buildSolutionProjects, cancellationToken: cancellationToken);
            snapshot = buildResult.Snapshot;
            snapshotStopwatch.Stop();
            CompileIndexTrace.Record(
                settings,
                "index-from-build.done",
                indexInputPath,
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

    // ADR-0007 accept path: the index reads a build's ALREADY-produced output — the generated .g.cs the
    // build-after-accept emitted, plus its harvested reference set — with NO compile of its own. This is the
    // no-3-builds path: the terminal gate build validated, the build-after-accept produced the real output,
    // and the index is a Roslyn pass over that. (RebuildAsync's own OpenProjectFromBuildAsync path still runs
    // a build; this one is handed the outputs and only reads them.)
    public async Task<SolutionIndexSummary> RebuildFromBuildOutputAsync(
        MonitorSettings settings,
        string projectPath,
        string generatedRoot,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        CompileIndexTrace.Record(
            settings,
            "index-from-build.read.start",
            projectPath,
            "index rides the build (ADR-0007): reading the build-after-accept's already-emitted generated files + refs — NO compile here");
        System.Diagnostics.Stopwatch snapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        MSBuildSolutionSnapshot snapshot = await new BuildOutputSnapshotLoader()
            .BuildProjectSnapshotAsync(projectPath, generatedRoot, references, cancellationToken);
        snapshotStopwatch.Stop();
        CompileIndexTrace.Record(
            settings,
            "index-from-build.read.done",
            projectPath,
            $"projects={snapshot.Projects.Count} refs={references.Count} ms={snapshotStopwatch.ElapsedMilliseconds}");
        timingSink?.Invoke(
            "index.full.snapshot",
            snapshotStopwatch.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["inputPath"] = Path.GetFullPath(settings.WatchedSolutionPath),
                ["source"] = "build-output-read"
            });

        System.Diagnostics.Stopwatch saveFromBuildStopwatch = System.Diagnostics.Stopwatch.StartNew();
        SolutionIndexSummary summary = store.SaveSnapshot(snapshot, timingSink);
        saveFromBuildStopwatch.Stop();
        timingSink?.Invoke(
            "index.full.sqlite-save",
            saveFromBuildStopwatch.ElapsedMilliseconds,
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
