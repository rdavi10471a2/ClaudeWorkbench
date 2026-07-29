namespace AIMonitor.Data;

public sealed class MonitorStatusResult
{
    public string WatchedSolutionPath { get; set; } = string.Empty;

    public string RuntimeRoot { get; set; } = string.Empty;

    public string DatabasePath { get; set; } = string.Empty;

    public bool DatabaseExists { get; set; }

    public string IndexedInputPath { get; set; } = string.Empty;

    public DateTimeOffset IndexedAtUtc { get; set; } = DateTimeOffset.MinValue;

    public int ProjectCount { get; set; }

    public int DocumentCount { get; set; }

    public int SymbolCount { get; set; }

    public int ReferenceCount { get; set; }

    public int CallSiteCount { get; set; }

    public int RelationshipCount { get; set; }

    public int StaleFileCount { get; set; }

    public int DiagnosticCount { get; set; }

    // True when a schema-versioned full recreate emptied the index and a full rebuild has not yet repopulated it
    // (SolutionIndexDatabase.NeedsFullRebuildKey is set). While set, the index is stale: solution-index rows must not
    // be trusted, scoped refreshes are refused/upgraded to full, and a full RebuildAsync is required to clear it.
    public bool RebuildRequired { get; set; }

    // True when the LAST reindex was BLOCKED because the build was red (ADR-0007: the index rides the build, so a
    // failed build leaves the last-good index in place rather than overwriting it). This is distinct from ordinary
    // staleness (StaleFileCount > 0): ordinary staleness clears on a reindex, but a blocked index will NOT advance
    // until the build compiles — reindexing again just re-hits the same errors. LastBuildError carries the compiler
    // diagnostics and BlockedAtUtc when it happened. Cleared on the next successful (green) reindex.
    public bool IndexUpdateBlocked { get; set; }

    public string? LastBuildError { get; set; }

    public DateTimeOffset? BlockedAtUtc { get; set; }
}
