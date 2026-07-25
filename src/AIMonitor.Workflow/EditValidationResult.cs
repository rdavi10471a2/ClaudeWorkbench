namespace AIMonitor.Workflow;

public sealed record EditSyntaxValidationResult(
    bool HasErrors,
    IReadOnlyList<EditSyntaxDiagnostic> Diagnostics);

public sealed record EditSyntaxDiagnostic(
    string Id,
    string Message,
    int Line,
    int Column);
