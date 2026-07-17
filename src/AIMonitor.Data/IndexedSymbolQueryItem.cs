namespace AIMonitor.Data;

public sealed record IndexedSymbolQueryItem(
    IndexedSymbolRow Symbol,
    string RelativePath,
    string SelectorHintJson);
