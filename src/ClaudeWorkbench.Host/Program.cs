using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ModelContextProtocol.Protocol;

namespace ClaudeWorkbench.Host;

// In-process HTTP host for the extracted AIMonitor engine's MCP tool surface.
// Reuses the exact tool classes the stdio console host registers (AIMonitorTools);
// only the transport differs (Streamable HTTP instead of stdio) so the Claude
// Agent SDK sidecar can register it via its mcpServers option over a URL.
internal static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        string repositoryRoot = GetOption(args, "--repo-root") ?? Directory.GetCurrentDirectory();
        string? settingsPath = GetOption(args, "--config");
        MonitorSettings settings = MonitorSettingsLoader.Load(repositoryRoot, settingsPath);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IMonitorLogger>(_ =>
            new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(settings)));
        builder.Services.AddSingleton(SolutionIndexQueryService.Create(settings));
        builder.Services.AddSingleton(new WorkflowEditService(settings));
        builder.Services.AddSingleton(new RoslynEditService(settings));
        builder.Services.AddSingleton(new WorkflowEditPaths(settings));
        builder.Services.AddSingleton<AIMonitorMcpRuntimeState>();
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "claude-workbench",
                    Version = "0.1.0"
                };
            })
            .WithHttpTransport()
            .WithTools<AIMonitorTools>();

        WebApplication app = builder.Build();
        app.MapMcp("/mcp");
        app.MapGet("/health", (MonitorSettings monitorSettings) => Results.Ok(new
        {
            status = "ok",
            repositoryRoot = monitorSettings.RepositoryRoot,
            watchedSolutionPath = monitorSettings.WatchedSolutionPath
        }));

        app.Run();
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
