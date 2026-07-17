using AIMonitor.Core;

namespace AIMonitor.Logging;

public static class MonitorLogPaths
{
    public static string GetDefaultLogPath(MonitorSettings settings)
    {
        return Path.Combine(settings.RuntimeRoot, "logs", "aimonitor.ndjson");
    }
}
