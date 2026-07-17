namespace AIMonitor.Data;

public sealed record IndexedPackageReferenceRow(
    string ProjectPath,
    string Include,
    string Version);
