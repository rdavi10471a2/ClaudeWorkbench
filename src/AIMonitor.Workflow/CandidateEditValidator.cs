using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AIMonitor.Workflow;

// Fast, per-file C# SYNTAX validation for the edit path. Semantic/compile validation is the real
// pre-merge `dotnet build` (PreMergeValidationService), run at plan-complete and at accept - NOT an
// in-memory flat overlay. The flat overlay was removed: a single flat CSharpCompilation with
// <solutionRoot>/bin references could not model per-project types, references, SDKs, or .razor, so it
// produced wrong results on cross-project / Blazor / multi-TFM edits. The real build is the source of truth.
internal sealed class CandidateEditValidator
{
    public EditSyntaxValidationResult ValidateSyntaxIfCSharp(string watchedFilePath, string content)
    {
        if (!watchedFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return new EditSyntaxValidationResult(false, []);
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(content, path: watchedFilePath);
        EditSyntaxDiagnostic[] diagnostics = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic =>
            {
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                return new EditSyntaxDiagnostic(
                    diagnostic.Id,
                    diagnostic.GetMessage(),
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1);
            })
            .ToArray();

        return new EditSyntaxValidationResult(diagnostics.Length > 0, diagnostics);
    }
}
