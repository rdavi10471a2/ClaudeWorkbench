using AIMonitor.Core;
using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — Phase 1 CMB-parity ground-truth against REAL repository instances, authored by Claude
// (review+test role; no production edits).
//
// The hermetic ClaudeSmokes prove extraction on a tiny fixture; these prove it holds on real, large watched
// solutions (Blazor + WinForms shapes) — the "couple of repositories" ground-truth check. They rebuild a fresh
// index from the real .sln and assert the Phase-1 surfaces (symbols/references/relationships/call-sites) actually
// populate at scale. Guarded by File.Exists: they no-op on machines that don't have the repo, so they are a
// local-only ground-truth gate, never a portable-CI failure.
public sealed class ClaudeSmokesRepositoryInstanceTests
{
    [Theory]
    [Trait("Suite", "ClaudeSmokes")]
    [InlineData(@"C:\SchemaStudioWebViewer V 1.1 - Monitor\SchemaStudioWebViewer.sln")]  // Blazor / Razor
    [InlineData(@"C:\Schema Studio - DBV2\Schema Studio.sln")]                            // WinForms
    public async Task ClaudeSmokes_real_repository_instance_populates_phase1_index_surfaces(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            return; // local-only ground-truth: skip when the repo isn't on this machine
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesRepo", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(tempRoot, solutionPath, Path.Combine(tempRoot, "runtime"));

        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);
        MonitorStatusResult status = query.GetMonitorStatus();

        // The real solution loaded and indexed.
        Assert.True(status.ProjectCount > 0, $"No projects indexed from {solutionPath}.");
        Assert.True(status.SymbolCount > 0, "No symbols indexed.");
        Assert.True(status.ReferenceCount > 0, "No references indexed.");

        // Phase-1 surfaces populate at scale on a real repo (not just the hermetic fixture).
        Assert.True(status.RelationshipCount > 0, "No symbol relationships indexed from the real repository.");
        Assert.True(status.CallSiteCount > 0, "No call sites indexed from the real repository.");

        // And the surfaces are queryable: at least one relationship row and one call-site row come back.
        Assert.NotEmpty(store.ListRelationships());
        Assert.NotEmpty(store.ListCallSites());
    }
}
