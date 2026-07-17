namespace AIMonitor.Logging;

public interface IMonitorLogEventSource
{
    event Action<MonitorLogEntry>? EntryWritten;
}
