namespace ClaudeWorkbench.Host.Console;

// A read of the agent's live usage, off the SDK Query handle. All nullable: the
// underlying SDK methods are experimental and only return once a thread exists.
public sealed record UsageSnapshot(
    bool Available,
    double? ContextPercent,
    long? ContextUsedTokens,
    long? ContextMaxTokens,
    long? AutoCompactThresholdTokens,
    string? PlanType,
    double? WeeklyPercent,
    string? WeeklyResetsAt,
    double? FiveHourPercent,
    string? FiveHourResetsAt,
    double? MonthlyPercent)
{
    public static UsageSnapshot Empty { get; } = new(false, null, null, null, null, null, null, null, null, null, null);

    // Percentage of the window still free before auto-compaction fires.
    public double? FreeUntilCompactPercent
    {
        get
        {
            if (ContextUsedTokens is long used && AutoCompactThresholdTokens is long threshold && threshold > 0)
            {
                double free = 100.0 * (threshold - used) / threshold;
                return free < 0 ? 0 : free;
            }

            return null;
        }
    }
}
