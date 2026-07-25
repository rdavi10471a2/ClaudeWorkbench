using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — Phase 6 (per-edit validation is SYNTAX-ONLY feedback), authored by Claude (review+test
// role; no production edits). LOCAL.
//
// The per-edit overlay COMPILE was removed: a flat in-memory CSharpCompilation with <solutionRoot>/bin
// references could not model project types / references / SDKs / .razor, so it reported WRONG results on
// cross-project, Blazor and multi-TFM edits. Per-edit validation is now SYNTAX only (fast, always accurate);
// the authoritative semantic/compile gate is the REAL pre-merge build at complete_edit_plan and at accept.
// These pin that recalibrated contract: a per-edit blocks broken SYNTAX but never blocks a
// semantically-wrong-but-parseable edit (that is the real build's job), and never C#-validates non-C# assets.
public sealed class ClaudeSmokesPhase6ValidationTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_semantic_error_edit_is_not_blocked_at_edit_time()
    {
        (WorkflowEditService service, string watchedRoot, string programFilePath) = CreateFixture();
        EditSessionStatus refresh = service.Refresh(programFilePath);
        File.WriteAllText(refresh.WorkingFilePath, "namespace Example { internal static class Program { static int M() => 0; } }");

        // Inject a CS0103 (undefined name) — a SEMANTIC error that is still syntactically valid.
        ReplaceTextResult result = service.ReplaceText(programFilePath, "0", "MissingThing", expectedMatches: 1);

        // Per-edit validation is syntax-only, so a parseable-but-semantically-wrong edit is NOT blocked here;
        // the real build at plan-complete / accept is what reports the semantic failure.
        Assert.True(result.Changed);
        Assert.NotNull(result.SyntaxValidation);
        Assert.False(result.SyntaxValidation!.HasErrors);
    }

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_syntactically_invalid_edit_is_rejected_at_submit()
    {
        (WorkflowEditService service, string _, string programFilePath) = CreateFixture();
        service.Refresh(programFilePath);

        // An unbalanced brace is a SYNTAX error — the per-edit gate must reject it and write nothing.
        Assert.ThrowsAny<Exception>(() =>
            service.SubmitFile(programFilePath, "namespace Example { internal static class Program { "));
        Assert.Equal(0, service.GetStatus(programFilePath).OperationCount);
    }

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_non_csharp_edit_is_not_csharp_validated()
    {
        (WorkflowEditService service, string watchedRoot, _) = CreateFixture();
        string jsonPath = Path.Combine(watchedRoot, "config.json");
        File.WriteAllText(jsonPath, "{ \"flag\": \"on\" }");

        EditSessionStatus refresh = service.Refresh(jsonPath);
        File.WriteAllText(refresh.WorkingFilePath, "{ \"flag\": \"on\" }");

        ReplaceTextResult result = service.ReplaceText(jsonPath, "on", "off", expectedMatches: 1);

        Assert.True(result.Changed);
        // Non-.cs assets get no C# syntax validation (and never overlay compilation).
        Assert.NotNull(result.SyntaxValidation);
        Assert.False(result.SyntaxValidation!.HasErrors);
    }

    private static (WorkflowEditService Service, string WatchedRoot, string ProgramFilePath) CreateFixture()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesP6", Guid.NewGuid().ToString("N"));
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        string programFilePath = Path.Combine(watchedRoot, "Program.cs");
        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(programFilePath, "namespace Example { internal static class Program { } }");
        MonitorSettings settings = MonitorSettings.Create(Path.Combine(tempRoot, "Repo"), projectPath, Path.Combine(tempRoot, "Runtime"));
        return (new WorkflowEditService(settings), watchedRoot, programFilePath);
    }
}
