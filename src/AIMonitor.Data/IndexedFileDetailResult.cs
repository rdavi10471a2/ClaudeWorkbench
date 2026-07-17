namespace AIMonitor.Data;

public sealed record IndexedFileDetailResult(
    string RequestedPath,
    string FullPath,
    bool FileExists,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256,
    string ParseStatus,
    bool IsIndexed,
    bool IsStale,
    int? DiagnosticCount,
    IReadOnlyList<IndexedDocumentRow> Files,
    IReadOnlyList<IndexedSymbolRow> Symbols,
    IReadOnlyList<IndexedReferenceRow> References);
