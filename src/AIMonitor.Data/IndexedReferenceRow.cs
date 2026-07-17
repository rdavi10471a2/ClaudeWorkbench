namespace AIMonitor.Data;

public sealed record IndexedReferenceRow(
    string ProjectPath,
    string TargetStableKey,
    string FilePath,
    int Line,
    int Column,
    string ReferenceKind,
    string Snippet,
    string TargetName = "",
    string TargetKind = "",
    string CallerStableKey = "",
    string CallerName = "",
    string CallerKind = "",
    string FileContentHash = "");
