using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — meaningful WinForms watched sample, index ground-truth. Authored by Claude (review+test; no src
// edits). LOCAL. Read-only against the in-repo samples/watched-solutions/WinFormsSample (no mutation, repeatable).
//
// Mean-teacher: asserts the FULL repository/relationship/caller graph with EXACT identities and a negative check,
// so it fails on any extraction regression (faked callers, wrong override target, missing relationship kind).
public sealed class ClaudeSmokesWinFormsSampleTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_winforms_sample_index_exposes_repository_graph_and_exact_caller_identity()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(), "samples", "watched-solutions", "WinFormsSample", "WinFormsSample.csproj");
        Assert.True(File.Exists(projectPath), $"In-repo WinForms sample missing: {projectPath}");

        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(projectPath);
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesWinForms", Guid.NewGuid().ToString("N"), "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        store.SaveSnapshot(snapshot);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        const string repoType = "WinFormsSample.Repositories.CustomerRepository";
        const string serviceType = "WinFormsSample.Repositories.CustomerService";

        // Symbol-find: the repository class + its override method + the calling service method are indexed.
        Assert.Contains(symbols, s => s.Name == "CustomerRepository" && s.Kind == "NamedType");
        string repoGetById = Assert.Single(symbols, s => s.Name == "GetByIdAsync" && s.ContainingType == repoType).StableKey;
        string loadAsyncKey = Assert.Single(symbols, s => s.Name == "LoadAsync" && s.ContainingType == serviceType).StableKey;

        // Relationships: real inheritance / interface-impl / partial (MainForm spans .cs + .Designer.cs).
        // NOTE (mean-teacher catch, documented for Codex): the index does NOT emit an `overrides` relationship for
        // CustomerRepository.GetByIdAsync overriding the GENERIC base RepositoryBase<T>.GetByIdAsync, although it does
        // for non-generic bases (Phase-1 fixture). So `overrides` is intentionally NOT asserted here — it's a tracked
        // extraction gap, not a silent drop. The override is still partly observable via implements_interface_member.
        System.Collections.Generic.IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships();
        Assert.Contains(relationships, r => r.RelationshipKind == "inherits_from" && r.SourceName == "CustomerRepository" && r.TargetName == "RepositoryBase");
        Assert.Contains(relationships, r => r.RelationshipKind == "inherits_from" && r.SourceName == "CustomerRepository" && r.TargetName == "ICustomerRepository");
        Assert.Contains(relationships, r => r.RelationshipKind == "implements_interface_member" && r.SourceName == "GetByIdAsync");
        Assert.Contains(relationships, r => r.RelationshipKind == "partial_declaration" && r.SourceName == "MainForm");

        // Caller identity: LoadAsync's call to repository.GetByIdAsync resolves the REAL caller and the CONCRETE
        // override target (NOT the base) — proves FindContainingSymbol + overload resolution, not a kind heuristic.
        System.Collections.Generic.IReadOnlyList<IndexedCallSiteRow> callSites = store.ListCallSites();
        IndexedCallSiteRow loadCall = Assert.Single(
            callSites, c => c.CallKind == "InvocationExpression" && c.CallerName == "LoadAsync");
        Assert.Equal(loadAsyncKey, loadCall.CallerStableKey);
        Assert.Equal(repoGetById, loadCall.TargetStableKey);            // resolved to CustomerRepository.GetByIdAsync
        Assert.Equal("Method", loadCall.CallerKind);
        // Object creation is extracted (the sample constructs repositories/services via `new`).
        Assert.Contains(callSites, c => c.CallKind == "ObjectCreationExpression");

        // Negative (mean-teacher): the resolved target is the concrete override, never the abstract base method.
        string baseGetById = Assert.Single(
            symbols, s => s.Name == "GetByIdAsync" && s.ContainingType.Contains("RepositoryBase", StringComparison.Ordinal)).StableKey;
        Assert.NotEqual(baseGetById, loadCall.TargetStableKey);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClaudeWorkbench.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find ClaudeWorkbench.slnx.");
    }
}
