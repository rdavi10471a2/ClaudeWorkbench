namespace AIMonitor.Logging;

public interface IMonitorLogger
{
    void Write(
        MonitorLogLevel level,
        string source,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string>? properties = null);
}
