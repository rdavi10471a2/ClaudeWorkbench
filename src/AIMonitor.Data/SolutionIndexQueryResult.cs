namespace AIMonitor.Data;

public sealed record SolutionIndexQueryResult(
    MonitorStatusResult Status,
    IReadOnlyList<IndexedDocumentRow> Files,
    IReadOnlyList<IndexedSymbolRow> Symbols,
    string Scope,
    string? Value,
    int MaxFiles,
    int MaxSymbols,
    int TotalFileCount,
    int TotalSymbolCount,
    bool LimitsClamped);
