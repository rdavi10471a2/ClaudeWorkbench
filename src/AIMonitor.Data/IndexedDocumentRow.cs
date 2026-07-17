namespace AIMonitor.Data;

public sealed record IndexedDocumentRow(
    string ProjectPath,
    string StableKey,
    string Name,
    string FilePath,
    string Folders,
    string ContentHash = "");
