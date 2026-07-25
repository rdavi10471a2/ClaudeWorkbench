using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

public sealed class SolutionIndexBuilderTests
{
    [Fact]
    public async Task RebuildAsync_loads_configured_solution_and_writes_sqlite_index()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string solutionPath = Path.Combine(root, "Fixture.slnx");
        string projectPath = Path.Combine(root, "Fixture", "Fixture.csproj");
        string sourcePath = Path.Combine(root, "Fixture", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);

        await File.WriteAllTextAsync(solutionPath, $$"""
            <Solution>
              <Project Path="Fixture/Fixture.csproj" />
            </Solution>
            """);

        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(sourcePath, """
            namespace Fixture;

            public sealed class Program
            {
                public static void Main()
                {
                }
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(root, solutionPath);
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(new MSBuildWorkspaceLoader(), store);

        SolutionIndexSummary summary = await builder.RebuildAsync(settings);
        IReadOnlyList<IndexedDocumentRow> documents = store.ListDocuments();
        IReadOnlyList<IndexedProjectRow> projects = store.ListProjects();

        Assert.True(File.Exists(databasePath));
        Assert.StartsWith(
            MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings),
            databasePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, summary.ProjectCount);
        Assert.Single(projects);
        Assert.Equal("net10.0", projects[0].TargetFramework);
        Assert.Contains(documents, document => document.Name == "Program.cs");
    }

    [Fact]
    public async Task RefreshProjectFilesAsync_rebuilds_project_references_for_refreshed_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "CascadeFixture.csproj");
        string providerPath = Path.Combine(root, "Provider.cs");
        string callerPath = Path.Combine(root, "Caller.cs");

        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(providerPath, """
            namespace CascadeFixture;

            public sealed class Provider
            {
                public string Target()
                {
                    return "old";
                }
            }
            """);

        await File.WriteAllTextAsync(callerPath, """
            namespace CascadeFixture;

            public sealed class Caller
            {
                public string Use(Provider provider)
                {
                    return provider.Target();
                }
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(root, projectPath);
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(new MSBuildWorkspaceLoader(), store);

        await builder.RebuildAsync(settings);
        IndexedSymbolRow oldTarget = store.ListSymbols().Single(symbol => symbol.Name == "Target" && symbol.Kind == "Method");

        Assert.Contains(store.ListReferences(), reference =>
            reference.TargetStableKey == oldTarget.StableKey
            && reference.FilePath.Equals(callerPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.ListCallSites(), callSite =>
            callSite.TargetStableKey == oldTarget.StableKey
            && callSite.FilePath.Equals(callerPath, StringComparison.OrdinalIgnoreCase));

        await File.WriteAllTextAsync(providerPath, """
            namespace CascadeFixture;

            public sealed class Provider
            {
                public string RenamedTarget()
                {
                    return "new";
                }
            }
            """);

        List<(string Phase, long DurationMs, IReadOnlyDictionary<string, string> Properties)> timings = [];
        await builder.RefreshProjectFilesAsync(
            settings,
            projectPath,
            [providerPath],
            timingSink: (phase, durationMs, properties) =>
            {
                timings.Add((phase, durationMs, new Dictionary<string, string>(properties, StringComparer.Ordinal)));
            });

        Assert.DoesNotContain(store.ListSymbols(), symbol => symbol.StableKey == oldTarget.StableKey);
        Assert.Contains(store.ListSymbols(), symbol => symbol.Name == "RenamedTarget" && symbol.Kind == "Method");
        Assert.DoesNotContain(store.ListReferences(), reference => reference.TargetStableKey == oldTarget.StableKey);
        Assert.DoesNotContain(store.ListCallSites(), callSite => callSite.TargetStableKey == oldTarget.StableKey);
        Assert.Contains(timings, timing => timing.Phase == "msbuild.file.get-compilation-in-memory");
        Assert.Contains(timings, timing => timing.Phase == "index.project.msbuild-snapshot");
        Assert.Contains(timings, timing => timing.Phase == "index.project.sqlite-replace");
        Assert.All(timings, timing => Assert.True(timing.DurationMs >= 0));
    }
}
