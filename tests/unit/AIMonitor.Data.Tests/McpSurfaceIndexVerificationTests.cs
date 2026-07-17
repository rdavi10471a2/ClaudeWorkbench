using AIMonitor.Core;
using AIMonitor.MSBuild;
using System.Linq;
using Xunit.Abstractions;

namespace AIMonitor.Data.Tests;

// Ground-truth verification on the REAL WebViewer copy (not a hermetic fixture): proves the indexer actually extracts
// Razor and cross-project references with the installed SDK/build artifacts, and that SolutionIndexProbe's closure
// query works. Local-only (File.Exists-gated). This is the foundation the MCP-surface suite builds on; if Razor/
// cross-project extraction did not land, every higher test that assumes them would be meaningless.
//
// RESOLVED (2026-06-08): an earlier run found the full RebuildAsync threw "SQLite Error 19: FOREIGN KEY constraint
// failed" on the real WebViewer because symbol_references.target_stable_key (and the call_sites/symbol_relationships
// stable_key columns) carried a `references symbols(stable_key) on delete cascade` FK, and SaveSnapshot inserts a
// project's references before later projects' target symbols exist. That cross-symbol FK has been removed from the
// schema (the columns are now plain `text not null`), so the rebuild must SUCCEED here and the razor/cross-project
// assertions run for real. No FK-19 workaround remains; any rebuild failure now hard-fails the test.
public sealed class McpSurfaceIndexVerificationTests
{
    private const string BenchSolution = @"C:\VSCodeProjects\SchemaStudioBench\SchemaStudioWebViewer.sln";

    private readonly ITestOutputHelper output;

    public McpSurfaceIndexVerificationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Real_webviewer_index_has_razor_and_cross_project_references()
    {
        if (!File.Exists(BenchSolution))
        {
            return; // local-only ground truth
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorMcpSuite", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(tempRoot, BenchSolution, Path.Combine(tempRoot, "runtime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));

        // The cross-symbol FK is gone, so this must succeed on the real multi-project WebViewer. Any failure (including
        // the old SQLite-19) now propagates and fails the test rather than being swallowed.
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        SolutionIndexProbe probe = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexCounts counts = probe.GetCounts();

        Assert.True(counts.Projects >= 4, $"projects={counts.Projects}");
        Assert.True(counts.Symbols > 100, $"symbols={counts.Symbols}");
        Assert.True(counts.References > 100, $"references={counts.References}");

        // Razor extraction actually ran on the real, restored solution (the hermetic fixture's blind spot).
        Assert.True(probe.HasReferenceKindPrefix("razor"), "no 'razor*' references indexed on the real WebViewer");

        // Cross-project references exist — the exact population a project-scoped cascade endangers (HIGH #1).
        Assert.True(probe.GetCrossProjectReferenceCount() > 0, "no cross-project references indexed");

        // Closure query: SchemaStudio.Data is referenced by higher projects, so its inbound-dependent set is non-empty.
        string dataProjectPath = store.ListProjects()
            .Select(project => project.ProjectPath)
            .First(path => path.EndsWith("SchemaStudio.Data.csproj", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<string> inbound = probe.GetInboundDependentProjectPaths(dataProjectPath);
        Assert.NotEmpty(inbound);

        // Record (do not hard-fail) whether razor-generated rows specifically are produced by this indexer build,
        // and the reference-kind histogram, so the suite documents the real behavior rather than assuming it.
        bool hasRazorGenerated = probe.HasReferenceKindPrefix("razor-generated");
        string histogram = string.Join(", ", probe.GetReferenceKindCounts().Select(k => $"{k.Kind}={k.Count}"));
        output.WriteLine(
            $"REAL WEBVIEWER REBUILD SUCCEEDED. razor present={probe.HasReferenceKindPrefix("razor")}; "
            + $"razor-generated present={hasRazorGenerated}; crossProjectRefs={probe.GetCrossProjectReferenceCount()}; "
            + $"inboundDepsOfData={inbound.Count}; kinds=[{histogram}]");
        Assert.True(
            counts.References > 0,
            $"razor-generated present={hasRazorGenerated}; inboundDepsOfData={inbound.Count}; kinds=[{histogram}]");
    }
}
