using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — WinForms source-map noise filter. Authored by Claude (review+test; no src edits). LOCAL.
// Read-only against the in-repo WinForms sample (repeatable). Mean-teacher: the designer boilerplate must be
// COLLAPSED WITH A MARKER (not silently dropped) and user-authored code must be KEPT.
public sealed class ClaudeSmokesWinFormsSourceMapTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_winforms_source_map_collapses_designer_noise_but_keeps_user_code()
    {
        string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "WinFormsSample");
        string projectPath = Path.Combine(sampleRoot, "WinFormsSample.csproj");
        string designerPath = Path.Combine(sampleRoot, "Forms", "MainForm.Designer.cs");
        string formPath = Path.Combine(sampleRoot, "Forms", "MainForm.cs");

        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesWinFormsMap", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(Path.Combine(tempRoot, "Repo"), projectPath, Path.Combine(tempRoot, "Runtime"));
        RoslynEditService roslyn = new(settings);

        // Designer file: InitializeComponent + designer fields collapsed, WITH a winforms-designer marker.
        RoslynSourceMapSymbol[] designer = roslyn.GetSourceMap(designerPath, "file", "navigation")
            .Files.SelectMany(file => file.Symbols).ToArray();
        RoslynSourceMapSymbol initialize = Assert.Single(designer, s => s.Name == "InitializeComponent");
        Assert.True(initialize.IsElided == true, "InitializeComponent should be elided.");
        Assert.Equal("winforms-designer-initialize-component", initialize.ElisionReason);
        Assert.Contains(designer, s => s.IsElided == true && s.ElisionReason == "winforms-designer-field");
        // Collapse-not-drop: the elided members are still LISTED (with a marker), never silently removed.
        Assert.Contains(designer, s => s.Name == "InitializeComponent");

        // User-authored code in MainForm.cs is NOT elided.
        RoslynSourceMapSymbol[] form = roslyn.GetSourceMap(formPath, "file", "navigation")
            .Files.SelectMany(file => file.Symbols).ToArray();
        Assert.Contains(form, s => s.Name == "OnLoadClicked");
        Assert.DoesNotContain(form, s => s.Name == "OnLoadClicked" && s.IsElided == true);
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
