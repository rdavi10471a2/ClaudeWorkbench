using AIMonitor.Core;
using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — Phase 6 CI-portable index gate, authored by Claude (review+test role; no production edits). LOCAL.
//
// Phase 6 added an in-repo watched solution, samples/watched-solutions/WorkflowHarnessSample, but only the
// operator-run ToolSmokeTests drive it. This smoke makes it an AUTOMATED, machine-independent index target: it
// rebuilds the index from the checked-in sample (no external C:\ path, no File.Exists guard) and asserts the
// sample's real symbols. This is what lets the ClaudeSmokes ground-truth stop depending on external repos.
public sealed class ClaudeSmokesHarnessSampleTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_indexes_in_repo_workflow_harness_sample()
    {
        string repositoryRoot = FindRepositoryRoot();
        string solutionPath = Path.Combine(
            repositoryRoot, "samples", "watched-solutions", "WorkflowHarnessSample", "WorkflowHarnessSample.slnx");
        Assert.True(File.Exists(solutionPath), $"In-repo harness sample missing: {solutionPath}");

        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesHarness", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(tempRoot, solutionPath, Path.Combine(tempRoot, "runtime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();

        // Real symbols of samples/.../WorkflowHarnessSample/AppConfig/AppConfig.cs.
        Assert.Contains(symbols, symbol => symbol.Name == "AppConfig" && symbol.Kind == "NamedType");
        Assert.Contains(symbols, symbol => symbol.Name == "InitiaWorkflowTestPassed3" && symbol.Kind == "Property");
        Assert.Contains(symbols, symbol => symbol.Name == "CreateDefault" && symbol.Kind == "Method");
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
