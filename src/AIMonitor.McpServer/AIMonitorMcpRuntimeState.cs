using AIMonitor.Logging;
using System.Runtime.CompilerServices;
using System.Text;

namespace AIMonitor.McpServer;

public sealed class AIMonitorMcpRuntimeState
{
    private readonly IMonitorLogger logger;
    private long lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int shutdownRequested;

    public AIMonitorMcpRuntimeState(IMonitorLogger logger)
    {
        this.logger = logger;
    }

    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), TimeSpan.Zero);

    public bool ShutdownRequested => Volatile.Read(ref shutdownRequested) == 1;

    public void Touch([CallerMemberName] string toolName = "")
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        logger.Write(
            MonitorLogLevel.Information,
            "AIMonitor.McpServer",
            "adapter.mcp.tool.called",
            "MCP tool call observed.",
            new Dictionary<string, string>
            {
                ["requestId"] = Guid.NewGuid().ToString("N"),
                ["adapterProtocol"] = "mcp",
                ["toolName"] = ToSnakeCase(toolName),
                ["memberName"] = toolName,
                ["isError"] = "false"
            });
    }

    public void RequestShutdown(string? reason)
    {
        _ = reason;
        Volatile.Write(ref shutdownRequested, 1);
        Touch();
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        StringBuilder builder = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
