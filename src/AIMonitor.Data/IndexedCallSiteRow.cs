namespace AIMonitor.Data;

public sealed record IndexedCallSiteRow(
    string ProjectPath,
    string CallerStableKey,
    string CallerName,
    string CallerKind,
    string TargetStableKey,
    string FilePath,
    int Line,
    int Column,
    string CallKind,
    string Snippet);
