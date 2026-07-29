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

        // ADR-0007: the index rides the build — ALWAYS, no flag. ResolveAllProjects yields [the-one] for a
        // single project and OpenSolutionFromBuildAsync handles N>=1, so ONE build emits every project's
        // generated .g.cs + per-project refs and the index READS them. The in-proc MSBuildWorkspace open
        // survives ONLY for a watched entry that resolves to no buildable project — never as a build-failure
        // fallback.
        IReadOnlyList<string> rideProjects = WatchedSolutionInfo.ResolveAllProjects(settings.WatchedSolutionPath);
        if (rideProjects.Count >= 1)
        {
            CompileIndexTrace.Record(
                settings,
                "index-from-build.start",
                indexInputPath,
                $"index rides the build (ADR-0007): whole-solution read of {rideProjects.Count} project(s) — one build emits generated + per-project refs, the index reads them");
            BuildOutputSnapshotResult buildResult = await new BuildOutputSnapshotLoader()
                .OpenSolutionFromBuildAsync(indexInputPath, rideProjects, cancellationToken: cancellationToken);
            if (!buildResult.BuildSucceeded)
            {
                // RED BUILD → the index is GATED. Preserve the last-good snapshot untouched: no overwrite, no
                // in-proc recompile. A failed build must never replace the index (it is the best map of what
                // last compiled), and recompiling failed source in-proc is exactly the double-compile this
                // ordering exists to kill. Return Built=false + the errors so the caller blocks and reports.
                snapshotStopwatch.Stop();
                CompileIndexTrace.Record(
                    settings,
                    "index-from-build.blocked",
                    indexInputPath,
                    $"build FAILED (exit {buildResult.BuildExitCode}) — index PRESERVED (last good), NOT reindexed");
                return new SolutionIndexSummary(
                    indexInputPath, DateTimeOffset.MinValue, 0, 0, 0,
                    Built: false, BuildError: ExtractBuildErrors(buildResult.BuildOutput));
            }

            snapshot = buildResult.Snapshot;
            CompileIndexTrace.Record(
                settings,
                "index-from-build.done",
                indexInputPath,
                $"buildSucceeded=True projects={snapshot.Projects.Count} ms={snapshotStopwatch.ElapsedMilliseconds}");
        }
        else
        {
            // No buildable project resolved (a degenerate/unreadable watched entry) — last-resort in-proc open
            // so the index isn't simply empty. NOT a build-failure fallback.
            CompileIndexTrace.Record(
                settings,
                "index-compile.start",
                indexInputPath,
                "no buildable project resolved — last-resort in-proc MSBuildWorkspace open");
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

    // Pull the compiler/MSBuild error lines out of a failed build's output for the operator-facing message —
    // lines in the MSBuild diagnostic format (": error "); falls back to the last several non-empty lines when
    // none match. Deduped and capped so a wall of output stays readable in the dialog.
    private static string ExtractBuildErrors(string buildOutput)
    {
        if (string.IsNullOrWhiteSpace(buildOutput))
        {
            return "The build failed but produced no output.";
        }

        string[] lines = buildOutput.Split('\n');
        string[] errors = lines
            .Select(line => line.TrimEnd('\r', ' '))
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToArray();
        if (errors.Length > 0)
        {
            return string.Join(Environment.NewLine, errors);
        }

        return string.Join(
            Environment.NewLine,
            lines.Select(line => line.TrimEnd('\r')).Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(15));
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
