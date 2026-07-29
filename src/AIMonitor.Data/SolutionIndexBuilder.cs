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
        MSBuildSolutionSnapshot? snapshot = null;

        // ADR-0007, opt-in (CWB_INDEX_RIDES_BUILD=1): the index rides the build. One real build emits every
        // project's generated files + per-project resolved refs; the index is a Roslyn pass over that output —
        // accurate razor (from the build's .g.cs), real paths, no MSBuildWorkspace/BuildHost. ONE path for 1..N
        // projects (review finding C): ResolveAllProjects yields [the-one] for a single project and
        // OpenSolutionFromBuildAsync handles N>=1, so there is no single-project special case (and no divergent
        // CoreCompile-dump path that goes empty on an up-to-date build). Flag off, 0 projects, or a FAILED build
        // fall back to the existing in-proc loader (unchanged default).
        IReadOnlyList<string> rideProjects = IndexRidesBuild.Enabled
            ? WatchedSolutionInfo.ResolveAllProjects(settings.WatchedSolutionPath)
            : [];
        if (rideProjects.Count >= 1)
        {
            CompileIndexTrace.Record(
                settings,
                "index-from-build.start",
                indexInputPath,
                $"index rides the build (ADR-0007): whole-solution read of {rideProjects.Count} project(s) — one build emits generated + per-project refs, the index reads them");
            BuildOutputSnapshotResult buildResult = await new BuildOutputSnapshotLoader()
                .OpenSolutionFromBuildAsync(indexInputPath, rideProjects, cancellationToken: cancellationToken);
            if (buildResult.BuildSucceeded)
            {
                snapshot = buildResult.Snapshot;
                CompileIndexTrace.Record(
                    settings,
                    "index-from-build.done",
                    indexInputPath,
                    $"buildSucceeded=True projects={snapshot.Projects.Count} ms={snapshotStopwatch.ElapsedMilliseconds}");
            }
            else
            {
                // Review #2: a failed build emits missing generated files + empty refs → a degraded snapshot.
                // Do NOT overwrite a good index with it. Fall through to the in-proc loader, which compiles from
                // source and tolerates errors — exactly as the accept path guards on IsError.
                CompileIndexTrace.Record(
                    settings,
                    "index-from-build.failed",
                    indexInputPath,
                    $"build FAILED (exit {buildResult.BuildExitCode}) — falling back to in-proc compile so a transient failure does not replace the index with a degraded one");
            }
        }

        if (snapshot is null)
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
            CompileIndexTrace.Record(
                settings,
                "index-compile.done",
                indexInputPath,
                $"in-proc compile ms={snapshotStopwatch.ElapsedMilliseconds}");
        }

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

    // ADR-0007 accept path: the index reads the build-after-accept's ALREADY-produced output for the WHOLE
    // solution (every project's generated .g.cs + per-project refs) with NO compile of its own. This is the
    // no-3-builds path: the terminal gate build validated, the build-after-accept produced the real output for
    // all N projects, and the index is a Roslyn pass over that. One read path for 1..N projects — no
    // single-project and no single-file special case.
    public async Task<SolutionIndexSummary> RebuildFromBuildOutputAsync(
        MonitorSettings settings,
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        CompileIndexTrace.Record(
            settings,
            "index-from-build.read.start",
            solutionPath,
            $"index rides the build (ADR-0007): reading the build-after-accept's already-emitted output for {projectPaths.Count} project(s) — NO compile here");
        System.Diagnostics.Stopwatch snapshotStopwatch = System.Diagnostics.Stopwatch.StartNew();
        MSBuildSolutionSnapshot snapshot = await new BuildOutputSnapshotLoader()
            .ReadSolutionSnapshotAsync(solutionPath, projectPaths, cancellationToken: cancellationToken);
        snapshotStopwatch.Stop();
        CompileIndexTrace.Record(
            settings,
            "index-from-build.read.done",
            solutionPath,
            $"projects={snapshot.Projects.Count} ms={snapshotStopwatch.ElapsedMilliseconds}");
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
