using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Components;
using ClaudeWorkbench.Host.Services;
using ModelContextProtocol.Protocol;
using Radzen;

namespace ClaudeWorkbench.Host;

// Single-surface process: serves the claude-workbench MCP tool surface over HTTP
// (for the sidecar) AND the Blazor operator console (for the human), sharing one
// engine + one logging sink in-process.
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
                options.ServerInfo = new Implementation { Name = "claude-workbench", Version = "0.1.0" };
            })
            .WithHttpTransport()
            .WithTools<AIMonitorTools>();

        string sidecarBase = builder.Configuration["Sidecar:BaseUrl"] ?? "http://localhost:6110";
        builder.Services.AddSingleton(new SidecarOptions { BaseUrl = sidecarBase });
        builder.Services.AddHttpClient<SidecarClient>(client => client.BaseAddress = new Uri(sidecarBase));
        builder.Services.AddSingleton<SidecarEventStream>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SidecarEventStream>());

        builder.Services.AddRadzenComponents();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        WebApplication app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapMcp("/mcp");
        app.MapGet("/health", (MonitorSettings monitorSettings) => Results.Ok(new
        {
            status = "ok",
            repositoryRoot = monitorSettings.RepositoryRoot,
            watchedSolutionPath = monitorSettings.WatchedSolutionPath
        }));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
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
