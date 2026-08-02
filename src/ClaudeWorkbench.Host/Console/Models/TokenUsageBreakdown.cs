namespace ClaudeWorkbench.Host.Console;

// The per-turn token anatomy read off the sidecar's `usage` events — the split the token study
// surfaced: input-side is mostly CACHE-READ (the whole context re-fed every round-trip at ~1/10
// price), with a little FRESH input and CACHE-CREATION on top, plus OUTPUT. RoundTrips is the real
// cost driver (cost ≈ context × round-trips), so it leads the display.
public sealed record TurnTokenUsage(
    int RoundTrips,
    long FreshInput,
    long CacheRead,
    long CacheCreation,
    long Output)
{
    public static TurnTokenUsage Empty { get; } = new(0, 0, 0, 0, 0);

    // Everything the model had to read on the input side of the turn.
    public long TotalInput => FreshInput + CacheRead + CacheCreation;

    // How much of that input was discounted cache re-read (the "94%" the study kept hitting).
    public double CacheReadPercent => TotalInput > 0 ? 100.0 * CacheRead / TotalInput : 0;

    public bool IsEmpty => RoundTrips == 0 && TotalInput == 0 && Output == 0;

    public TurnTokenUsage Add(TurnTokenUsage other) => new(
        RoundTrips + other.RoundTrips,
        FreshInput + other.FreshInput,
        CacheRead + other.CacheRead,
        CacheCreation + other.CacheCreation,
        Output + other.Output);
}

// Two views of the same stream: the just-finished turn and the running thread total. Derived from
// a cap-immune accumulator (the event ring buffer evicts old events, so the thread total cannot be
// recomputed from the snapshot — it is summed as turns complete and reset on New Thread).
public sealed record TokenUsageBreakdown(bool Available, TurnTokenUsage LastTurn, TurnTokenUsage Thread)
{
    public static TokenUsageBreakdown Empty { get; } = new(false, TurnTokenUsage.Empty, TurnTokenUsage.Empty);
}
