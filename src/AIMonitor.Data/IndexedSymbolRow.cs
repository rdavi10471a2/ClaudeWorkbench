namespace AIMonitor.Data;

public sealed record IndexedSymbolRow(
    string ProjectPath,
    string StableKey,
    string Name,
    string Kind,
    string Namespace,
    string ContainingType,
    string FilePath,
    int StartLine,
    int EndLine,
    string Signature,
    string Accessibility = "",
    bool IsStatic = false,
    bool IsAbstract = false,
    bool IsSealed = false,
    bool IsVirtual = false,
    bool IsOverride = false,
    string MethodKind = "");
