using System.Diagnostics;
using System.Net.Sockets;

namespace ClaudeWorkbench.Host.Services;

// Launches and supervises the Node sidecar as a child process so the installed
// app is a single start (`dotnet run` / the published exe). Skips launching when
// something is already listening on the sidecar port (a standalone/dev sidecar),
// and kills the child on host shutdown.
public sealed class SidecarProcessHost : IHostedService, IDisposable
{
    private readonly SidecarLaunchOptions options;
    private readonly ILogger<SidecarProcessHost> logger;
    private Process? process;

    public SidecarProcessHost(SidecarLaunchOptions options, ILogger<SidecarProcessHost> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoStart)
        {
            logger.LogInformation("Sidecar auto-start disabled (Sidecar:AutoStart=false).");
            return;
        }

        if (await IsPortOpenAsync(options.Port, cancellationToken))
        {
            logger.LogInformation("Sidecar already listening on port {Port}; not launching a child process.", options.Port);
            return;
        }

        string script = Path.Combine(options.SidecarDirectory, "dist", "index.js");
        if (!File.Exists(script))
        {
            logger.LogWarning(
                "Sidecar entry not found at {Script}. Run 'npm install && npm run build' in the sidecar folder (or set Sidecar:Directory). The agent console will not work until the sidecar runs.",
                script);
            return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = options.NodeExecutable,
            WorkingDirectory = options.SidecarDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["SIDECAR_PORT"] = options.Port.ToString();
        startInfo.Environment["WORKBENCH_MCP_URL"] = options.McpUrl;

        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    logger.LogInformation("[sidecar] {Line}", args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    logger.LogWarning("[sidecar] {Line}", args.Data);
                }
            };
            process.Exited += (_, _) => logger.LogWarning("Sidecar process exited (code {Code}).", SafeExitCode());
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            logger.LogInformation(
                "Launched sidecar: {Node} {Script} (pid {Pid}, port {Port}).",
                options.NodeExecutable,
                script,
                process.Id,
                options.Port);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to launch the sidecar with '{Node}'. Ensure Node.js is installed and on PATH, or set Sidecar:NodeExecutable to a full path.",
                options.NodeExecutable);
            process = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                logger.LogInformation("Sidecar process stopped.");
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Error stopping the sidecar process.");
        }

        return Task.CompletedTask;
    }

    private int SafeExitCode()
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using (TcpClient client = new())
            {
                Task connect = client.ConnectAsync("127.0.0.1", port);
                Task finished = await Task.WhenAny(connect, Task.Delay(400, cancellationToken));
                return finished == connect && client.Connected;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        process?.Dispose();
    }
}
