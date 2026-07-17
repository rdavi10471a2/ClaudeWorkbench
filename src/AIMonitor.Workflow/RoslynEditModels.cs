using System.Text.Json.Serialization;

namespace AIMonitor.Workflow;

public sealed record RoslynEditResult(
    string Operation,
    string WatchedFilePath,
    string WorkingFilePath,
    string RelativePath,
    string Status,
    string Message,
    string WorkingHash,
    int OperationCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManifestJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EditSyntaxValidationResult? SyntaxValidation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EditOverlayValidationResult? OverlayValidation = null);

public sealed record RoslynSymbolSelector(
    string? ContainingNamespace = null,
    string? ContainingType = null,
    string? MemberKind = null,
    string? Name = null,
    IReadOnlyList<string>? ParameterTypes = null,
    int? Arity = null,
    string? StableSymbolKey = null);

public sealed record RoslynSymbolReadResult(
    string WatchedFilePath,
    string WorkingFilePath,
    string RelativePath,
    string Kind,
    string Name,
    int StartLine,
    int EndLine,
    string Text,
    string SourceKind = "working-candidate");

public sealed record RoslynSourceMapResult(
    string Scope,
    string Mode,
    string ModePurpose,
    string? RequestedPath,
    string? RequestedNamespace,
    int FileCount,
    int SymbolCount,
    IReadOnlyList<RoslynSourceMapFile> Files,
    long EstimatedTokenProxy = 0,
    int BudgetLimit = 0,
    bool WasTruncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WatchedProjectAlias = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WatchedProjectFolder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RoslynSourceMapNarrowingSuggestion>? SuggestedNarrowing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RoslynSourceMapNextCall>? SuggestedNextCalls = null);

public sealed record RoslynSourceMapFile(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceFilePath,
    string RelativePath,
    string ParseStatus,
    int DiagnosticCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Usings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Namespaces,
    IReadOnlyList<RoslynSourceMapSymbol> Symbols,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Length = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RoslynSourceMapDiagnostic>? DiagnosticsSummary = null);

public sealed record RoslynSourceMapDiagnostic(
    string Id,
    string Severity,
    string Message,
    int StartLine,
    int EndLine);

public sealed record RoslynSourceMapSymbol(
    string Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StableSymbolKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature,
    string? Namespace,
    string? ContainingType,
    int StartLine,
    int EndLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Modifiers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReturnType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ParameterTypes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ParameterNames,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Arity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SyntaxKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BaseTypes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RoslynSourceMapAttribute>? Attributes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasDocumentation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasAttributes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsStatic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAsync = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsOverride = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVirtual = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsPartial = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsElided = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ElisionReason = null);

public sealed record RoslynSourceMapAttribute(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Arguments = null);

public sealed record RoslynSourceMapNarrowingSuggestion(
    string RelativePath,
    string Reason,
    int SymbolCount,
    int DiagnosticCount);

public sealed record RoslynSourceMapNextCall(
    int Rank,
    string Tool,
    string Reason,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record RoslynFileOutlineResult(
    string SourceFilePath,
    string RelativePath,
    string ParseStatus,
    int DiagnosticCount,
    IReadOnlyList<RoslynFileOutlineItem> Items);

public sealed record RoslynFileOutlineItem(
    string Kind,
    string Name,
    int StartLine,
    int EndLine,
    string Signature,
    string? Namespace,
    string? ContainingType,
    string? SyntaxKind);
