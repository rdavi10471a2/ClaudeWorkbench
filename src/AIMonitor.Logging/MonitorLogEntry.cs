namespace AIMonitor.Logging;

public sealed record MonitorLogEntry(
    DateTimeOffset TimestampUtc,
    MonitorLogLevel Level,
    string Source,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string> Properties);
