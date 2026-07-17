namespace AIMonitor.Data;

public sealed record IndexedRelationshipRow(
    string ProjectPath,
    string SourceStableKey,
    string SourceName,
    string SourceKind,
    string TargetStableKey,
    string TargetName,
    string TargetKind,
    string RelationshipKind,
    string FilePath,
    int Line,
    int Column,
    string Snippet);
