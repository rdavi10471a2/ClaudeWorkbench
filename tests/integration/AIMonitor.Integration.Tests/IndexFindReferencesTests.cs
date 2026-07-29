using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;

namespace AIMonitor.Integration.Tests;

// Deterministic proof that the REAL index resolves find-references — no agent, no flake. Build the index
// over BlazorSample through the same path the post-accept rebuild uses (SolutionIndexRebuildService), then
// query references the way find_indexed_references does (SolutionIndexQueryService) and assert the index
// actually knows where symbols are used. This is the "the index works" backstop under the watchable e2e.
public sealed class IndexFindReferencesTests
{
    [Fact]
    public void Rebuilt_index_captures_queryable_references_at_real_source_locations()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorIndexRefs", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);
        MonitorSettings settings = MonitorSettings.Create(
            repoRoot,
            Path.Combine(projDir, "BlazorSample.slnx"),
            Path.Combine(work, "runtime"));

        // Build the index via the real post-accept path.
        SolutionIndexSummary summary = new SolutionIndexRebuildService()
            .RebuildAsync(settings)
            .GetAwaiter()
            .GetResult();
        Assert.True(summary.ProjectCount > 0, "the index built no projects");

        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);
        Assert.NotEmpty(query.ListSymbols());

        // 1) The index captured references, and they point at REAL source in this project.
        IReadOnlyList<IndexedReferenceRow> allReferences = query.ListReferences();
        Assert.NotEmpty(allReferences);
        Assert.Contains(allReferences, r => r.FilePath.Replace('/', '\\')
            .Contains("\\BlazorSample\\", StringComparison.OrdinalIgnoreCase));

        // 2) find-references works per-symbol: at least one indexed symbol has its usages resolvable by
        //    stable key — which is exactly what find_indexed_references returns to the agent.
        bool anySymbolHasResolvableReferences = query.ListSymbols()
            .Any(symbol => query.ListReferences(symbol.StableKey).Count > 0);
        Assert.True(anySymbolHasResolvableReferences,
            "no symbol's references were resolvable by stable key — find-references would return nothing");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj" or "test-prompts"))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClaudeWorkbench.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (ClaudeWorkbench.slnx).");
    }
}
