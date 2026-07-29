using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;

namespace AIMonitor.Integration.Tests;

// ADR-0007, increment 4a: the LIVE index path (SolutionIndexRebuildService -> SolutionIndexBuilder) uses the
// build-output loader when CWB_INDEX_RIDES_BUILD=1, and the result lands in the real SQLite index with razor
// mapped to the .razor. Off by default -> the existing loader, unchanged.
public sealed class IndexRidesBuildWiringTests
{
    [Fact]
    public void Live_index_rebuild_rides_the_build_when_flagged_and_maps_razor_to_the_razor()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorRidesBuild", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);
        MonitorSettings settings = MonitorSettings.Create(
            repoRoot,
            Path.Combine(projDir, "BlazorSample.slnx"),
            Path.Combine(work, "runtime"));

        SolutionIndexSummary summary = new SolutionIndexRebuildService()
            .RebuildAsync(settings)
            .GetAwaiter()
            .GetResult();
        Assert.True(summary.ProjectCount > 0, "the build-output index built no projects");

        // Through the real SQLite index: a .razor @code member is stored at the .razor, mapped via the build's
        // #line directives — i.e. the live index rode the build.
        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);
        Assert.Contains(
            query.ListSymbols(name: "LoadAsync"),
            symbol => symbol.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
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
