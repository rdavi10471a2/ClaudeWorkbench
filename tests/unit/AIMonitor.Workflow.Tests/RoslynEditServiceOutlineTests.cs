using AIMonitor.Core;

namespace AIMonitor.Workflow.Tests;

public sealed class RoslynEditServiceOutlineTests
{
    [Fact]
    public void GetFileOutline_returns_roslyn_declarations_and_ignores_comment_and_string_lookalikes()
    {
        OutlineFixture fixture = CreateFixture(
            """
            namespace Example;

            // public void CommentLookalike() { }

            public sealed class Widget
            {
                private const string Text = "public int StringLookalike { get; }";
                private readonly int count;

                public int Count => count;

                public string Format(int value)
                {
                    return value.ToString();
                }
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynFileOutlineResult result = service.GetFileOutline(fixture.SourceFilePath);

        Assert.Equal("parsed", result.ParseStatus);
        Assert.Equal(0, result.DiagnosticCount);
        Assert.Equal("Program.cs", result.RelativePath);
        Assert.Contains(result.Items, item => item.Kind == "class" && item.Name == "Widget");
        Assert.Contains(result.Items, item => item.Kind == "field" && item.Name == "count");
        Assert.Contains(result.Items, item => item.Kind == "property" && item.Name == "Count");
        Assert.Contains(result.Items, item => item.Kind == "method" && item.Name == "Format" && item.Signature.Contains("Format(int value)", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.Name.Contains("CommentLookalike", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.Name.Contains("StringLookalike", StringComparison.Ordinal));
    }

    [Fact]
    public void GetFileOutline_reports_parse_errors_without_falling_back_to_text_heuristics()
    {
        OutlineFixture fixture = CreateFixture(
            """
            namespace Example;

            public sealed class Broken
            {
                public void MissingBody(
            }
            """);
        RoslynEditService service = new(fixture.Settings);

        RoslynFileOutlineResult result = service.GetFileOutline(fixture.SourceFilePath);

        Assert.Equal("parse-error", result.ParseStatus);
        Assert.True(result.DiagnosticCount > 0);
        Assert.Empty(result.Items);
    }

    private static OutlineFixture CreateFixture(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorOutlineTests", Guid.NewGuid().ToString("N"));
        string watchedRoot = Path.Combine(root, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string sourceFilePath = Path.Combine(watchedRoot, "Program.cs");
        Directory.CreateDirectory(watchedRoot);
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
        return new OutlineFixture(MonitorSettings.Create(root, projectPath), sourceFilePath);
    }

    private sealed record OutlineFixture(MonitorSettings Settings, string SourceFilePath);
}
