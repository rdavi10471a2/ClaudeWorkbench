namespace AIMonitor.Data;

public sealed record SolutionIndexSummary(
    string InputPath,
    DateTimeOffset IndexedAtUtc,
    int ProjectCount,
    int DocumentCount,
    int DiagnosticCount);
