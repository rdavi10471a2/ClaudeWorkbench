using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Components;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;
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

        string repositoryRoot = GetOption(args, "--repo-root") ?? ResolveContentRoot(builder.Environment.ContentRootPath);
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
        builder.Services.AddSingleton(new MonitorConfigPath(Path.GetFullPath(configPath)));

        builder.Services.AddSingleton<IMonitorLogger>(_ =>
            new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(settings)));
        builder.Services.AddSingleton(new WorkspaceManager(
            settings.RepositoryRoot,
            settings.RuntimeRoot,
            settings));
        builder.Services.AddSingleton<AIMonitorMcpRuntimeState>();
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "claude-workbench", Version = "0.1.0" };
            })
            .WithHttpTransport()
            .WithTools<AIMonitorTools>()
            .WithTools<Tasks.TaskMcpTools>()
            .WithTools<GitMcpTools>();

        string sidecarBase = builder.Configuration["Sidecar:BaseUrl"] ?? "http://localhost:6110";
        builder.Services.AddSingleton(new SidecarOptions { BaseUrl = sidecarBase });

        // Launch + supervise the Node sidecar as a child process (single-start app).
        string sidecarDirectory = builder.Configuration["Sidecar:Directory"]
            ?? ResolveSidecarDirectory(repositoryRoot);
        builder.Services.AddSingleton(new SidecarLaunchOptions
        {
            AutoStart = builder.Configuration.GetValue("Sidecar:AutoStart", true),
            NodeExecutable = builder.Configuration["Sidecar:NodeExecutable"] ?? "node",
            SidecarDirectory = sidecarDirectory,
            Port = new Uri(sidecarBase).Port,
            McpUrl = builder.Configuration["Sidecar:McpUrl"] ?? "http://localhost:6100/mcp"
        });
        builder.Services.AddHostedService<SidecarProcessHost>();
        builder.Services.AddSingleton<AgentSettingsService>();
        builder.Services.AddSingleton<DirectoryBrowserService>();
        builder.Services.AddSingleton<RuntimeProvisioner>();
        builder.Services.AddSingleton<WorkspaceCoordinator>();
        builder.Services.AddScoped<UploadService>();
        builder.Services.AddHttpClient<SidecarClient>(client => client.BaseAddress = new Uri(sidecarBase));
        builder.Services.AddSingleton<SidecarEventStream>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SidecarEventStream>());
        builder.Services.AddSingleton<AuthStatusProbe>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AuthStatusProbe>());
        builder.Services.AddScoped<SidecarOperatorConsole>();
        builder.Services.AddScoped<IOperatorConsole>(provider => provider.GetRequiredService<SidecarOperatorConsole>());
        builder.Services.AddScoped<IApprovalQueue>(provider => provider.GetRequiredService<SidecarOperatorConsole>());
        builder.Services.AddSingleton<IReviewWorkflow, EngineReviewWorkflow>();
        builder.Services.AddSingleton<Tasks.TaskBoardRepositoryFactory>();
        builder.Services.AddScoped<Tasks.IWorkflowTaskBoardViewService, Tasks.WorkflowTaskBoardViewService>();
        builder.Services.AddSingleton<Source.SourceWorkspace>();

        // Operator-driven git backing for the watched solution (host-side; the agent
        // never runs git). GitService is a stateless CLI wrapper; GitWorkspaceService
        // binds it to the current watched workspace for the Git panel.
        builder.Services.AddSingleton<GitService>();
        builder.Services.AddSingleton<GitWorkspaceService>();
        builder.Services.AddSingleton<IndexRebuildStatus>();

        // Launcher-owned instance: shut down when the last browser tab closes (the tab
        // owns the instance's lifetime; graceful shutdown kills the sidecar too). Opt-in,
        // so a plain `dotnet run` dev session is unaffected.
        if (string.Equals(Environment.GetEnvironmentVariable("CWB_EXIT_WITH_BROWSER"), "1", StringComparison.Ordinal))
        {
            builder.Services.AddSingleton<BrowserPresenceTracker>();
            builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, BrowserLifetimeCircuitHandler>();
            // Tab-close shutdown should be snappy: don't let a slow-to-stop background service
            // hold the process for the default ~30s. Force completion within a few seconds.
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(4));
        }

        builder.Services.AddRadzenComponents();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        WebApplication app = builder.Build();

        // Ensure the already-configured workspace's runtime (skeleton + task DB) exists
        // at startup. Idempotent; the skeleton build is synchronous and cheap.
        WorkspaceManager startupWorkspace = app.Services.GetRequiredService<WorkspaceManager>();
        if (startupWorkspace.HasWorkspace)
        {
            app.Services.GetRequiredService<RuntimeProvisioner>().EnsureRuntime(startupWorkspace.Settings);

            // Reopening the app with a solution attached would otherwise leave the index
            // COLD until a manual Source-tab rebuild — which makes the first agent turn
            // flaky (start_monitor_session can't prove the single owning project). Warm it
            // here, but ONLY once Kestrel is up and ONLY in the background (a large solution
            // can take a while to index), behind the IndexRebuildStatus spinner. Startup is
            // never blocked; no solution attached => this never runs.
            IndexRebuildStatus rebuildStatus = app.Services.GetRequiredService<IndexRebuildStatus>();
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
            {
                using (rebuildStatus.Begin())
                {
                    try
                    {
                        await startupWorkspace.ProvisionAsync();
                    }
                    catch (Exception exception)
                    {
                        app.Services.GetRequiredService<IMonitorLogger>().Write(
                            MonitorLogLevel.Warning,
                            "Host",
                            "startup-index-rebuild-failed",
                            exception.Message);
                    }
                }
            }));
        }

        app.MapStaticAssets();
        app.UseAntiforgery();

        // Serves upload-folder files (images) referenced in chat markdown so the browser
        // can load them; only files under the workspace uploads/ folder are served.
        app.MapLocalFiles();

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
        // The staging procedure, served as plain text for the sidecar to splice into the
        // agent's governance card at startup. Same content as the `get_staging_guide` MCP
        // tool, from the same source (AgentGuidance) — the sidecar is not an MCP client
        // itself, so it reads this the same way it already reads /health. Deliberately one
        // source: the card used to restate these steps as TypeScript literals and drifted.
        app.MapGet("/guidance/staging", () => Results.Text(AgentGuidance.StagingGuide, "text/markdown"));

        // --- test-only review HTTP surface -------------------------------------
        // Accept is normally an OPERATOR action at the Merge Review dialog and the ONLY
        // path that writes watched source. These endpoints expose the SAME
        // IReviewWorkflow.ListPending/Accept the dialog's buttons call, so the sidecar
        // flow smoke can drive an end-to-end accept over HTTP without a human at the UI.
        // They bypass the human merge window, so they are OFF by default and only mapped
        // when CWB_ENABLE_REVIEW_API=1 — the fixture launch opts in; production never does.
        if (string.Equals(Environment.GetEnvironmentVariable("CWB_ENABLE_REVIEW_API"), "1", StringComparison.Ordinal))
        {
            app.MapGet("/review/pending", (IReviewWorkflow review) => Results.Ok(review.ListPending()));
            app.MapPost("/review/accept", (AcceptReviewRequest request, IReviewWorkflow review) =>
            {
                if (string.IsNullOrWhiteSpace(request.StagedRecordId))
                {
                    return Results.BadRequest(new { error = "stagedRecordId is required." });
                }

                ReviewActionResult result = review.Accept(request.StagedRecordId, request.ForceApprove);
                bool accepted = result.Message.StartsWith("Accepted", StringComparison.Ordinal);
                return Results.Ok(new { accepted, message = result.Message, agentSummary = result.AgentSummary });
            });
            app.MapPost("/review/reject", (AcceptReviewRequest request, IReviewWorkflow review) =>
            {
                if (string.IsNullOrWhiteSpace(request.StagedRecordId))
                {
                    return Results.BadRequest(new { error = "stagedRecordId is required." });
                }

                ReviewActionResult result = review.Reject(request.StagedRecordId);
                return Results.Ok(new { rejected = true, message = result.Message });
            });
            // Build the workspace index on demand. Startup only provisions the skeleton
            // (index build is normally deferred to a UI select), so a config-launched
            // fixture starts with a COLD index — which makes start_monitor_session flaky
            // (it can't prove the single owning project). The smoke calls this once up
            // front so every agent turn runs against a warm index.
            app.MapPost("/review/warmup", async (WorkspaceManager workspace) =>
            {
                if (!workspace.HasWorkspace)
                {
                    return Results.BadRequest(new { error = "no workspace configured." });
                }

                await workspace.ProvisionAsync();
                return Results.Ok(new { warmed = true, watchedSolutionPath = workspace.WatchedSolutionPath });
            });
        }

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
    }

    // The folder holding config/ — the host project under `dotnet run`, the publish folder
    // otherwise. ContentRootPath is the process's current directory, which is only right when
    // the host was started from its own project: the launcher starts it from its bin folder.
    // Fall back to the binary's own location, which the build populates with config/.
    private static string ResolveContentRoot(string contentRootPath)
    {
        if (Directory.Exists(Path.Combine(contentRootPath, "config")))
        {
            return contentRootPath;
        }

        string baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return Directory.Exists(Path.Combine(baseDirectory, "config")) ? baseDirectory : contentRootPath;
    }

    // The Node sidecar, found by walking up from the binary's own location — so it resolves
    // the same however the host was started. Covers both the repo layout (<repo>\sidecar) and
    // a publish that ships the sidecar next to the exe.
    private static string ResolveSidecarDirectory(string repositoryRoot)
    {
        string? directory = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory, "sidecar");
            if (File.Exists(Path.Combine(candidate, "dist", "index.js")))
            {
                return Path.GetFullPath(candidate);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Path.GetFullPath(Path.Combine(repositoryRoot, "sidecar"));
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

// Body for POST /review/accept (test-only surface, see CWB_ENABLE_REVIEW_API).
internal sealed record AcceptReviewRequest(string StagedRecordId, bool ForceApprove = false);
