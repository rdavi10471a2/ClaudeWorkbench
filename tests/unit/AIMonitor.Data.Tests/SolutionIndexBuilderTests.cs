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

    [Fact(Skip = "razor-generated:* reference rows are environment-dependent: they only index when the MSBuildWorkspace host Roslyn matches the SDK's Razor source generator version. On a skewed host the generator is silently skipped (0 source-gen docs), so this row cannot be asserted. Documented behavior (document-don't-pin); the razor:* code-behind path stays covered.")]
    public async Task RefreshProjectFilesAsync_preserves_razor_generated_reference_rows()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "ComponentBindingFixture.csproj");
        string importsPath = Path.Combine(root, "_Imports.razor");
        string boundInputPath = Path.Combine(root, "BoundInput.razor");
        string consumerMarkupPath = Path.Combine(root, "Consumer.razor");
        string consumerCodeBehindPath = Path.Combine(root, "Consumer.razor.cs");

        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(importsPath, """
            @using ComponentBindingFixture
            @using Microsoft.AspNetCore.Components
            """);

        await File.WriteAllTextAsync(boundInputPath, """
            <input value="@Value" />

            @code {
                [Parameter] public string Value { get; set; } = "";
                [Parameter] public EventCallback<string> ValueChanged { get; set; }
            }
            """);

        await File.WriteAllTextAsync(consumerMarkupPath, """
            <BoundInput @bind-Value="DisplayName" />
            """);

        await File.WriteAllTextAsync(consumerCodeBehindPath, """
            using Microsoft.AspNetCore.Components;

            namespace ComponentBindingFixture;

            public partial class Consumer : ComponentBase
            {
                public string DisplayName { get; set; } = "ready";
            }
            """);

        MonitorSettings settings = MonitorSettings.Create(root, projectPath);
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(new MSBuildWorkspaceLoader(), store);

        await builder.RebuildAsync(settings);
        IndexedSymbolRow displayName = store.ListSymbols()
            .Single(symbol => symbol.Name == "DisplayName" && symbol.Kind == "Property");

        Assert.Contains(store.ListDocuments(), document =>
            document.FilePath.EndsWith("Consumer.razor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(store.ListReferences(displayName.StableKey), reference =>
            reference.FilePath.EndsWith("Consumer.razor", StringComparison.OrdinalIgnoreCase)
            && reference.ReferenceKind.StartsWith("razor-generated:", StringComparison.Ordinal)
            && reference.Snippet.Contains("DisplayName", StringComparison.Ordinal));

        await File.WriteAllTextAsync(consumerCodeBehindPath, """
            using Microsoft.AspNetCore.Components;

            namespace ComponentBindingFixture;

            public partial class Consumer : ComponentBase
            {
                public string DisplayName { get; set; } = "ready";

                public string DisplayLabel => DisplayName;
            }
            """);

        await builder.RefreshProjectFilesAsync(
            settings,
            projectPath,
            [consumerCodeBehindPath]);

        IndexedSymbolRow refreshedDisplayName = store.ListSymbols()
            .Single(symbol => symbol.Name == "DisplayName" && symbol.Kind == "Property");
        IndexedReferenceRow refreshedRazorReference = store.ListReferences(refreshedDisplayName.StableKey)
            .Single(reference =>
                reference.FilePath.EndsWith("Consumer.razor", StringComparison.OrdinalIgnoreCase)
                && reference.ReferenceKind.StartsWith("razor-generated:", StringComparison.Ordinal)
                && reference.Snippet.Contains("DisplayName", StringComparison.Ordinal));

        Assert.Contains(store.ListDocuments(), document =>
            document.FilePath.EndsWith("Consumer.razor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(string.Empty, refreshedRazorReference.CallerName);
        Assert.Contains(store.ListSymbols(), symbol => symbol.Name == "DisplayLabel" && symbol.Kind == "Property");
    }
}
