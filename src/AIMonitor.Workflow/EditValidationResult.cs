namespace AIMonitor.Workflow;

public sealed record EditSyntaxValidationResult(
    bool HasErrors,
    IReadOnlyList<EditSyntaxDiagnostic> Diagnostics);

public sealed record EditSyntaxDiagnostic(
    string Id,
    string Message,
    int Line,
    int Column);

public sealed record EditOverlayValidationResult(
    string Status,
    bool HasErrors,
    int SyntaxTreeCount,
    int OverlayFileCount,
    IReadOnlyList<EditOverlayDiagnostic> Diagnostics,
    bool FromCache = false);

public sealed record EditOverlayDiagnostic(
    string Id,
    string Message,
    string Path,
    int Line,
    int Column);
