using AIMonitor.Core;
using AIMonitor.MSBuild;
using AIMonitor.Workflow;

namespace AIMonitor.Integration.Tests;

// ADR-0007: the build-after-accept (SolutionBuildService.Build) is the ONE real build. When the index rides the
// flag, that build ALSO emits every project's generated .g.cs and dumps each project's refs into its own obj (the
// shared per-project target) — so the reindex READS that whole-solution output instead of compiling its own. One
// path for 1..N projects: no single-project harvest, no single-file special case. With the flag off the build is
// byte-for-byte the ordinary build (no emit, no dump).
public sealed class IndexPostAcceptBuildOutputTests
{
    [Fact]
    public void Build_riding_the_flag_emits_generated_and_per_project_refs_for_the_read()
    {
        (MonitorSettings settings, string projectDir) = CopySampleAsWatchedSolution();

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

        string objDir = Path.Combine(projectDir, "obj");
        // Per-project refs dumped into the project's own obj (what ReadSolutionSnapshotAsync reads).
        Assert.NotEmpty(Directory.GetFiles(objDir, IndexRidesBuild.PerProjectRefsFileName, SearchOption.AllDirectories));
        // Razor source generator's .g.cs emitted (EmitCompilerGeneratedFiles).
        Assert.NotEmpty(Directory.GetDirectories(objDir, "generated", SearchOption.AllDirectories));
    }

    [Fact]
    public void Ordinary_build_off_flag_dumps_no_index_refs()
    {
        (MonitorSettings settings, string projectDir) = CopySampleAsWatchedSolution();

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

        Assert.False(result.IsError, $"ordinary build failed: {result.Message}");
        string objDir = Path.Combine(projectDir, "obj");
        Assert.True(Directory.Exists(objDir), "obj should exist after a build");
        Assert.Empty(Directory.GetFiles(objDir, IndexRidesBuild.PerProjectRefsFileName, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Read_from_the_build_output_maps_a_razor_member_to_the_razor()
    {
        (MonitorSettings settings, _) = CopySampleAsWatchedSolution();

        string? previous = Environment.GetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, "1");
            SolutionBuildService.BuildResult build = new SolutionBuildService().Build(settings);
            Assert.False(build.IsError, $"build failed: {build.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexRidesBuild.EnvironmentVariable, previous);
        }

        // The reindex READS that output for the whole solution (here a single project) — no compile.
        IReadOnlyList<string> projects = WatchedSolutionInfo.ResolveAllProjects(settings.WatchedSolutionPath);
        MSBuildSolutionSnapshot snapshot = await new BuildOutputSnapshotLoader()
            .ReadSolutionSnapshotAsync(settings.WatchedSolutionPath, projects);

        Assert.Contains(
            snapshot.Projects.SelectMany(project => project.Symbols),
            symbol => symbol.Name == "LoadAsync"
                && symbol.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
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
