using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Workflow;

namespace AIMonitor.Integration.Tests;

// ADR-0007, increment 4b-2: the build-after-accept (SolutionBuildService.Build) is the ONE real build. When
// the index rides the flag, that build ALSO emits the generated .g.cs and dumps the resolved reference set,
// and reports where — so the reindex can READ this build's output instead of running its own compile. With
// the flag off the build args are unchanged (no emit, no dump, no handoff).
public sealed class IndexPostAcceptBuildOutputTests
{
    [Fact]
    public void Post_accept_build_emits_generated_and_harvests_refs_when_riding_the_flag()
    {
        (MonitorSettings settings, string projDir) = CopySampleAsWatchedSolution();

        string? previous = Environment.GetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable);
        SolutionBuildService.BuildResult result;
        try
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, "1");
            result = new SolutionBuildService().Build(settings);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, previous);
        }

        Assert.False(result.IsError, $"post-accept build failed: {result.Message} {string.Join(" | ", result.Diagnostics)}");

        // The build knows which single project its outputs belong to.
        Assert.NotNull(result.RidesBuildProject);
        Assert.EndsWith("BlazorSample.csproj", result.RidesBuildProject, StringComparison.OrdinalIgnoreCase);

        // It emitted the razor source generator's .g.cs (the accurate, in-compile razor the reindex reads).
        Assert.False(string.IsNullOrEmpty(result.GeneratedRoot), "no generated root was reported");
        Assert.True(Directory.Exists(result.GeneratedRoot), $"generated root does not exist: {result.GeneratedRoot}");
        Assert.NotEmpty(Directory.GetFiles(result.GeneratedRoot!, "*.g.cs", SearchOption.AllDirectories));

        // It harvested the resolved reference set the compile used.
        Assert.NotEmpty(result.HarvestedReferences);
        Assert.Contains(result.HarvestedReferences, reference => reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ordinary_build_carries_no_build_output_handoff_when_the_flag_is_off()
    {
        (MonitorSettings settings, _) = CopySampleAsWatchedSolution();

        string? previous = Environment.GetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable);
        SolutionBuildService.BuildResult result;
        try
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, null);
            result = new SolutionBuildService().Build(settings);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, previous);
        }

        Assert.False(result.IsError, $"ordinary build failed: {result.Message} {string.Join(" | ", result.Diagnostics)}");
        Assert.Null(result.RidesBuildProject);
        Assert.Null(result.GeneratedRoot);
        Assert.Empty(result.HarvestedReferences);
    }

    [Fact]
    public async Task Read_only_reindex_from_build_output_lands_razor_in_the_index_without_its_own_compile()
    {
        (MonitorSettings settings, _) = CopySampleAsWatchedSolution();

        // The ONE real build (the build-after-accept) emits the generated files + harvests refs.
        string? previous = Environment.GetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable);
        SolutionBuildService.BuildResult build;
        try
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, "1");
            build = new SolutionBuildService().Build(settings);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, previous);
        }

        Assert.False(build.IsError, $"post-accept build failed: {build.Message}");
        Assert.NotNull(build.RidesBuildProject);
        Assert.False(string.IsNullOrEmpty(build.GeneratedRoot));

        // The reindex READS that output — no compile of its own (flag deliberately left off here to prove the
        // read path is compile-free and flag-independent).
        SolutionIndexSummary summary = await new SolutionIndexRebuildService().RebuildFromBuildOutputAsync(
            settings,
            build.RidesBuildProject!,
            build.GeneratedRoot!,
            build.HarvestedReferences);
        Assert.True(summary.ProjectCount > 0, "the build-output read produced no projects");

        // Through the real SQLite index: a .razor @code member is stored at the .razor, mapped via the build's
        // #line directives — the index rode the build's output.
        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);
        Assert.Contains(
            query.ListSymbols(name: "LoadAsync"),
            symbol => symbol.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
    }

    private static (MonitorSettings Settings, string ProjectDir) CopySampleAsWatchedSolution()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorPostAcceptBuild", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);
        MonitorSettings settings = MonitorSettings.Create(
            repoRoot,
            Path.Combine(projDir, "BlazorSample.slnx"),
            Path.Combine(work, "runtime"));
        return (settings, projDir);
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
