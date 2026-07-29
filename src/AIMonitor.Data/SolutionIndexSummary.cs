namespace AIMonitor.Data;

public sealed record SolutionIndexSummary(
    string InputPath,
    DateTimeOffset IndexedAtUtc,
    int ProjectCount,
    int DocumentCount,
    int DiagnosticCount,
    // ADR-0007: false when the reindex was BLOCKED because the build was red — the index was NOT rewritten and
    // the last-good snapshot is preserved. Callers must NOT mark the index fresh in that case, and the UI shows
    // BuildError. True on every successful reindex (the default keeps existing call sites unchanged).
    bool Built = true,
    string? BuildError = null);
