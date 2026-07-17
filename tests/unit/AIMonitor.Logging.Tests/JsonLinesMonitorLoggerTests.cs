using System.Text.Json;

namespace AIMonitor.Logging.Tests;

public sealed class JsonLinesMonitorLoggerTests
{
    [Fact]
    public void Write_appends_structured_json_line()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        string logPath = Path.Combine(root, "runtime", "logs", "aimonitor.ndjson");
        JsonLinesMonitorLogger logger = new(logPath);

        logger.Write(
            MonitorLogLevel.Information,
            "test",
            "unit.event",
            "Unit event written.",
            new Dictionary<string, string>
            {
                ["runId"] = "42"
            });

        string line = File.ReadLines(logPath).Single();
        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal("Information", document.RootElement.GetProperty("level").GetString());
        Assert.Equal("test", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("unit.event", document.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("42", document.RootElement.GetProperty("properties").GetProperty("runId").GetString());
    }
}
