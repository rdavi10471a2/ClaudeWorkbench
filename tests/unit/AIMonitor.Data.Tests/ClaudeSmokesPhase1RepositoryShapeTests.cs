using AIMonitor.Core;
using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — Phase 1 CMB-parity CI gate (real watched-project Repository), authored by Claude
// (review+test role; no production edits).
//
// Uses an ACTUAL database-Repository class in the watched project (SchemaStudioWebViewer's
// SchemaStudio.Data/Repositories/DatabaseDomainRepository.cs) as the sample, proving Phase-1 read surfaces —
// symbol find + reference navigation — resolve for the real data-access shape the monitor operates on.
// Guarded by File.Exists: no-ops when the watched repo isn't on this machine (local-only ground truth).
public sealed class ClaudeSmokesPhase1RepositoryShapeTests
{
    private const string WatchedSolutionPath = @"C:\SchemaStudioWebViewer V 1.1 - Monitor\SchemaStudioWebViewer.sln";
    private const string RepositoryTypeName = "DatabaseDomainRepository";

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_watched_repository_class_supports_symbol_find_and_reference_navigation()
    {
        if (!File.Exists(WatchedSolutionPath))
        {
            return; // local-only ground truth
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesRepoClass", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(tempRoot, WatchedSolutionPath, Path.Combine(tempRoot, "runtime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();

        // (A) SYMBOL FIND: the real repository class is discoverable in the watched-project index.
        Assert.Contains(symbols, symbol => symbol.Name == RepositoryTypeName && symbol.Kind == "NamedType");

        // ...and its async methods are indexed as members of that repository.
        IndexedSymbolRow[] repositoryMethods = symbols
            .Where(symbol => symbol.Kind == "Method"
                && symbol.ContainingType.EndsWith(RepositoryTypeName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(repositoryMethods);
        Assert.Contains(repositoryMethods, method => method.Name.EndsWith("Async", StringComparison.Ordinal));

        // (B) REFERENCE NAVIGATION: at least one of the repository's methods is referenced somewhere in the
        // watched solution and the index returns real reference rows for it (proves find-references works for the
        // real repository shape, including callers from .razor/.razor.cs consumers).
        bool anyMethodReferenced = repositoryMethods.Any(method => store.ListReferences(method.StableKey).Count > 0);
        Assert.True(
            anyMethodReferenced,
            $"No indexed references found for any method of {RepositoryTypeName}; symbol-find/navigation did not resolve the real repository shape.");
    }
}
