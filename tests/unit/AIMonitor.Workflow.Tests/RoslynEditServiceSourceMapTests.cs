using AIMonitor.Core;

namespace AIMonitor.Workflow.Tests;

public sealed class RoslynEditServiceSourceMapTests
{
    [Fact]
    public void GetSourceMap_auto_mode_resolves_to_selector_for_file_and_navigation_for_project()
    {
        SourceMapFixture fixture = CreateFixture("Program.cs", """
            namespace Example;

            public sealed class Widget
            {
                public string Format(int value) => value.ToString();
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynSourceMapResult fileMap = service.GetSourceMap(fixture.SourceFilePath);
        RoslynSourceMapResult projectMap = service.GetSourceMap(null);

        Assert.Equal("file", fileMap.Scope);
        Assert.Equal("selector", fileMap.Mode);
        Assert.Equal("stable-symbol-selection", fileMap.ModePurpose);
        Assert.Equal("project", projectMap.Scope);
        Assert.Equal("navigation", projectMap.Mode);
        Assert.Equal("broad-orientation", projectMap.ModePurpose);
    }

    [Fact]
    public void GetSourceMap_shapes_navigation_selector_and_full_payloads_differently()
    {
        SourceMapFixture fixture = CreateFixture("Program.cs", """
            using System;

            namespace Example;

            public sealed class Widget
            {
                public string Format(int value) => value.ToString();
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynSourceMapResult navigation = service.GetSourceMap(fixture.SourceFilePath, "file", "navigation");
        RoslynSourceMapResult selector = service.GetSourceMap(fixture.SourceFilePath, "file", "selector");
        RoslynSourceMapResult full = service.GetSourceMap(fixture.SourceFilePath, "file", "full");

        RoslynSourceMapSymbol navigationMethod = navigation.Files.Single().Symbols.Single(symbol => symbol.Name == "Format");
        RoslynSourceMapSymbol selectorMethod = selector.Files.Single().Symbols.Single(symbol => symbol.Name == "Format");
        RoslynSourceMapFile fullFile = full.Files.Single();

        Assert.Null(navigationMethod.StableSymbolKey);
        Assert.Null(navigationMethod.TextHash);
        Assert.NotNull(selectorMethod.StableSymbolKey);
        Assert.NotNull(selectorMethod.TextHash);
        Assert.Contains(selector.SuggestedNextCalls!, call => call.Tool == "get_symbol" && call.Arguments.ContainsKey("symbolSelectorJson"));
        Assert.Equal(fixture.SourceFilePath, fullFile.SourceFilePath);
        Assert.NotNull(fullFile.Sha256);
        Assert.True(fullFile.Length > 0);
    }

    [Fact]
    public void GetSourceMap_truncates_oversized_payloads_with_narrowing_and_next_calls()
    {
        string members = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 700).Select(index => $"    public string Method{index}() => \"{index}\";"));
        SourceMapFixture fixture = CreateFixture("BigFile.cs", $$"""
            namespace Example;

            public sealed class BigFile
            {
            {{members}}
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynSourceMapResult result = service.GetSourceMap(null, "project", "full");

        Assert.True(result.WasTruncated);
        Assert.True(result.EstimatedTokenProxy > result.BudgetLimit);
        Assert.Empty(result.Files);
        Assert.NotNull(result.SuggestedNarrowing);
        Assert.Contains(result.SuggestedNarrowing!, suggestion => suggestion.RelativePath == "BigFile.cs");
    }

    [Fact]
    public void GetSourceMap_filters_ai_history_attributes_without_hiding_user_attributes()
    {
        SourceMapFixture fixture = CreateFixture("Metadata.cs", """
            namespace Example;

            [FileVersion("1.0")]
            [AIFileContext("Metadata.cs", "Purpose")]
            [AIChange("1.0", "History noise")]
            [Obsolete("Real user attribute")]
            public sealed class Metadata
            {
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynSourceMapResult result = service.GetSourceMap(fixture.SourceFilePath, "file", "detail");
        RoslynSourceMapSymbol type = result.Files.Single().Symbols.Single(symbol => symbol.Name == "Metadata");
        string[] attributes = type.Attributes!.Select(attribute => attribute.Name).ToArray();

        Assert.Contains("FileVersion", attributes);
        Assert.Contains("AIFileContext", attributes);
        Assert.Contains("Obsolete", attributes);
        Assert.DoesNotContain("AIChange", attributes);
    }

    [Fact]
    public void GetSourceMap_collapses_designer_noise_with_visible_markers()
    {
        SourceMapFixture fixture = CreateFixture("MainForm.Designer.cs", """
            namespace Example;

            public partial class MainForm
            {
                private System.Windows.Forms.Button saveButton;

                protected override void Dispose(bool disposing)
                {
                }

                private void InitializeComponent()
                {
                    saveButton = new System.Windows.Forms.Button();
                }
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynSourceMapResult navigation = service.GetSourceMap(fixture.SourceFilePath, "file", "navigation");
        RoslynSourceMapResult full = service.GetSourceMap(fixture.SourceFilePath, "file", "full");

        RoslynSourceMapSymbol collapsedInitialize = navigation.Files.Single().Symbols.Single(symbol => symbol.Name == "InitializeComponent");
        RoslynSourceMapSymbol fullInitialize = full.Files.Single().Symbols.Single(symbol => symbol.Name == "InitializeComponent");

        Assert.True(collapsedInitialize.IsElided);
        Assert.Equal("winforms-designer-initialize-component", collapsedInitialize.ElisionReason);
        Assert.Null(collapsedInitialize.StableSymbolKey);
        Assert.True(fullInitialize.IsElided);
        Assert.NotNull(fullInitialize.StableSymbolKey);
        Assert.Contains(navigation.Files.Single().Symbols, symbol => symbol.ElisionReason == "winforms-designer-field");
    }

    [Fact]
    public void GetSymbol_reads_monitor_owned_working_candidate_and_reports_source_kind()
    {
        SourceMapFixture fixture = CreateFixture("Program.cs", """
            namespace Example;

            public sealed class Widget
            {
                public string Format() => "watched";
            }
            """);
        WorkflowEditService workflow = new(fixture.Settings);
        EditSessionStatus status = workflow.Refresh(fixture.SourceFilePath);
        File.WriteAllText(status.WorkingFilePath, """
            namespace Example;

            public sealed class Widget
            {
                public string Format() => "working";
            }
            """);
        RoslynEditService service = new(fixture.Settings);
        const string selector = """{"containingType":"Widget","memberKind":"method","name":"Format"}""";

        RoslynSymbolReadResult result = service.GetSymbol(fixture.SourceFilePath, selector);

        Assert.Equal("working-candidate", result.SourceKind);
        Assert.Contains("\"working\"", result.Text, StringComparison.Ordinal);
    }

    private static SourceMapFixture CreateFixture(string relativePath, string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorSourceMapTests", Guid.NewGuid().ToString("N"));
        string watchedRoot = Path.Combine(root, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string sourceFilePath = Path.Combine(watchedRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFilePath)!);
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(sourceFilePath, source);
        return new SourceMapFixture(MonitorSettings.Create(root, projectPath), sourceFilePath);
    }

    private sealed record SourceMapFixture(MonitorSettings Settings, string SourceFilePath);
}
