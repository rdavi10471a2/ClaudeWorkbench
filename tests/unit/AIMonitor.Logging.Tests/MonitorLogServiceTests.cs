namespace AIMonitor.Logging.Tests;

public sealed class MonitorLogServiceTests
{
    [Fact]
    public void Write_persists_line_and_notifies_listeners()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string logPath = Path.Combine(root, "runtime", "logs", "aimonitor.ndjson");
        MonitorLogService service = new(logPath);
        List<MonitorLogEntry> entries = [];
        service.EntryWritten += entry => entries.Add(entry);

        service.Write(
            MonitorLogLevel.Information,
            "test",
            "service.event",
            "Service event written.");

        Assert.True(File.Exists(logPath));
        Assert.Single(File.ReadLines(logPath));
        MonitorLogEntry entry = Assert.Single(entries);
        Assert.Equal("service.event", entry.EventName);
    }
}
