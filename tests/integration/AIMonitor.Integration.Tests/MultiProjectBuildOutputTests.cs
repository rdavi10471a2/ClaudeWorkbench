using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.MSBuild;

namespace AIMonitor.Integration.Tests;

// ADR-0007 whole-solution read: the build-output index must handle a MULTI-project solution — reindex every
// project (or, one day, the affected subset) through the SAME read engine, not fall back to the old loader.
// MixedTfmSample is the bed: ConsoleApp (net8), WinFormsApp (net9), BlazorApp (net10) all reference a shared
// net8 library (Shared.SharedGreeter.Greet). One incremental build emits every project's generated files +
// per-project refs; the index assembles them into one Roslyn solution with ProjectReferences, so a consumer's
// call to the shared method resolves cross-project to the same symbol.
public sealed class MultiProjectBuildOutputTests
{
    [Fact]
    public async Task Whole_solution_read_indexes_all_projects_and_resolves_cross_project_references()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "MixedTfmSample");
        Assert.True(Directory.Exists(sampleSrc), $"MixedTfmSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorMultiProject", Guid.NewGuid().ToString("N"));
        string dst = Path.Combine(work, "MixedTfmSample");
        CopyTree(sampleSrc, dst);
        string slnx = Path.Combine(dst, "MixedTfmSample.slnx");

        // The resolver enumerates all four projects.
        IReadOnlyList<string> projects = WatchedSolutionInfo.ResolveAllProjects(slnx);
        Assert.Equal(4, projects.Count);

        // One build of the whole solution -> read every project's output into one multi-project snapshot.
        BuildOutputSnapshotResult result = await new BuildOutputSnapshotLoader()
            .OpenSolutionFromBuildAsync(slnx, projects);
        Assert.True(result.BuildSucceeded, $"solution build failed (exit {result.BuildExitCode}):\n{result.BuildOutput}");

        MSBuildSolutionSnapshot snapshot = result.Snapshot;
        Assert.Equal(4, snapshot.Projects.Count);

        // The shared method is declared once, in the Shared library.
        MSBuildSymbolSnapshot? greet = snapshot.Projects
            .SelectMany(project => project.Symbols)
            .FirstOrDefault(symbol => symbol.Name == "Greet"
                && symbol.Signature.Contains("SharedGreeter", StringComparison.Ordinal));
        Assert.NotNull(greet);
        Assert.EndsWith("SharedGreeter.cs", greet!.FilePath, StringComparison.OrdinalIgnoreCase);

        // The whole point: a consumer project (NOT Shared) references that method, resolved cross-project to the
        // SAME symbol key — which only happens if all projects share one Roslyn solution wired with ProjectReferences.
        MSBuildReferenceSnapshot? crossProjectReference = snapshot.Projects
            .SelectMany(project => project.References)
            .FirstOrDefault(reference => reference.TargetStableKey == greet.StableKey
                && !reference.FilePath.Replace('/', '\\').Contains("\\Shared\\", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(crossProjectReference);
    }

    [Fact]
    public void Declared_project_dependencies_answer_who_references_shared_from_the_index()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "MixedTfmSample");
        string work = Path.Combine(Path.GetTempPath(), "AIMonitorProjDeps", Guid.NewGuid().ToString("N"));
        string dst = Path.Combine(work, "MixedTfmSample");
        CopyTree(sampleSrc, dst);
        MonitorSettings settings = MonitorSettings.Create(
            repoRoot, Path.Combine(dst, "MixedTfmSample.slnx"), Path.Combine(work, "runtime"));

        // Rebuild through the multi-project read so the project_references graph is persisted in the index.
        new SolutionIndexRebuildService().RebuildAsync(settings).GetAwaiter().GetResult();

        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);

        // Inbound: the three consumers all DECLARE a ProjectReference to Shared — the affected set for a Shared change.
        ProjectDependencyResult sharedDeps = query.GetProjectDependencies(
            Path.GetFullPath(Path.Combine(dst, "Shared", "Shared.csproj")));
        Assert.Contains(sharedDeps.ReferencedBy, p => p.EndsWith("ConsoleApp.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sharedDeps.ReferencedBy, p => p.EndsWith("WinFormsApp.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sharedDeps.ReferencedBy, p => p.EndsWith("BlazorApp.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(sharedDeps.References); // Shared references nobody

        // Outbound: ConsoleApp references Shared.
        ProjectDependencyResult consoleDeps = query.GetProjectDependencies(
            Path.GetFullPath(Path.Combine(dst, "ConsoleApp", "ConsoleApp.csproj")));
        Assert.Contains(consoleDeps.References, p => p.EndsWith("Shared.csproj", StringComparison.OrdinalIgnoreCase));
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
