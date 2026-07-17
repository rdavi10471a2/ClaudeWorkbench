using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — meaningful Blazor watched sample, index ground-truth. Authored by Claude (review+test; no src
// edits). LOCAL. Read-only against the in-repo samples/watched-solutions/BlazorSample (repeatable).
//
// Mean-teacher: proves the repository graph + caller identity index the same in a Blazor project AND that the
// .razor @code references map back to the .razor source (the defensible Razor boundary). Markup component-attribute
// bindings (@bind) are deliberately NOT asserted — documented Razor boundary.
public sealed class ClaudeSmokesBlazorSampleTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_blazor_sample_indexes_repository_graph_and_maps_razor_code_references()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(), "samples", "watched-solutions", "BlazorSample", "BlazorSample.csproj");
        Assert.True(File.Exists(projectPath), $"In-repo Blazor sample missing: {projectPath}");

        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(projectPath);
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesBlazor", Guid.NewGuid().ToString("N"), "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        store.SaveSnapshot(snapshot);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        const string repoType = "BlazorSample.Repositories.CustomerRepository";
        const string serviceType = "BlazorSample.Repositories.CustomerService";

        // Symbol-find: repository + service + the .razor.cs code-behind class are indexed.
        Assert.Contains(symbols, s => s.Name == "CustomerRepository" && s.Kind == "NamedType");
        Assert.Contains(symbols, s => s.Name == "RecordLoad" && s.ContainingType.EndsWith("CustomerList", StringComparison.Ordinal));
        string repoGetById = Assert.Single(symbols, s => s.Name == "GetByIdAsync" && s.ContainingType == repoType).StableKey;
        string serviceLoad = Assert.Single(symbols, s => s.Name == "LoadAsync" && s.ContainingType == serviceType).StableKey;

        // Repository relationships (same shape holds in a Blazor project).
        System.Collections.Generic.IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships();
        Assert.Contains(relationships, r => r.RelationshipKind == "inherits_from" && r.SourceName == "CustomerRepository" && r.TargetName == "RepositoryBase");
        Assert.Contains(relationships, r => r.RelationshipKind == "implements_interface_member" && r.SourceName == "GetByIdAsync");

        // Caller identity: CustomerService.LoadAsync -> CustomerRepository.GetByIdAsync (exact, concrete override).
        IndexedCallSiteRow serviceCall = Assert.Single(
            store.ListCallSites(repoGetById),
            c => c.CallKind == "InvocationExpression" && c.CallerStableKey == serviceLoad);
        Assert.Equal("LoadAsync", serviceCall.CallerName);

        // Razor: the .razor @code C# references map back to the .razor source.
        Assert.Contains(
            store.ListReferences(),
            r => r.ReferenceKind.StartsWith("razor", StringComparison.OrdinalIgnoreCase)
                && r.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
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
