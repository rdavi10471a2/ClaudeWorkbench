using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Components;
using ClaudeWorkbench.Host.Console;
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
        builder.WebHost.UseStaticWebAssets();

        string repositoryRoot = GetOption(args, "--repo-root") ?? builder.Environment.ContentRootPath;
        string configPath = GetOption(args, "--config") ?? Path.Combine(repositoryRoot, "config", "appsettings.json");
        // First run: the mutable config is git-ignored, so seed it from the committed
        // template (placeholder solution -> the app opens the workspace picker).
        if (!File.Exists(configPath))
        {
            string templatePath = Path.Combine(repositoryRoot, "config", "appsettings.template.json");
            if (File.Exists(templatePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.Copy(templatePath, configPath);
            }
        }

        MonitorSettings settings = MonitorSettingsLoader.Load(repositoryRoot, configPath);

        builder.Services.AddSingleton<IMonitorLogger>(_ =>
            new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(settings)));
        builder.Services.AddSingleton(new WorkspaceManager(
            settings.RepositoryRoot,
            settings.RuntimeRoot,
            settings.WinMergeCandidatePaths,
            settings));
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
        builder.Services.AddSingleton<AgentSettingsService>();
        builder.Services.AddSingleton<DirectoryBrowserService>();
        builder.Services.AddSingleton<RuntimeProvisioner>();
        builder.Services.AddSingleton<WorkspaceCoordinator>();
        builder.Services.AddScoped<UploadService>();
        builder.Services.AddHttpClient<SidecarClient>(client => client.BaseAddress = new Uri(sidecarBase));
        builder.Services.AddSingleton<SidecarEventStream>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SidecarEventStream>());
        builder.Services.AddScoped<SidecarOperatorConsole>();
        builder.Services.AddScoped<IOperatorConsole>(provider => provider.GetRequiredService<SidecarOperatorConsole>());
        builder.Services.AddScoped<IApprovalQueue>(provider => provider.GetRequiredService<SidecarOperatorConsole>());
        builder.Services.AddSingleton<IReviewWorkflow, EngineReviewWorkflow>();
        builder.Services.AddSingleton<Source.SourceWorkspace>();

        builder.Services.AddRadzenComponents();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        WebApplication app = builder.Build();

        // Ensure the already-configured workspace's runtime (skeleton + task DB) exists
        // at startup. Idempotent; no index rebuild here (that is on-demand / on select).
        WorkspaceManager startupWorkspace = app.Services.GetRequiredService<WorkspaceManager>();
        if (startupWorkspace.HasWorkspace)
        {
            app.Services.GetRequiredService<RuntimeProvisioner>().EnsureRuntime(startupWorkspace.Settings);
        }

        app.MapStaticAssets();
        app.UseAntiforgery();
        app.MapMcp("/mcp");
        app.MapGet("/health", (WorkspaceManager workspace) => Results.Ok(new
        {
            status = "ok",
            repositoryRoot = workspace.RepositoryRoot,
            hasWorkspace = workspace.HasWorkspace,
            watchedSolutionPath = workspace.WatchedSolutionPath,
            uploadsPath = workspace.HasWorkspace
                ? Path.Combine(MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(workspace.Settings), "uploads")
                : null
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
