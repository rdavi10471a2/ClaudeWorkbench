using System.Diagnostics;
using System.Text.Json;

namespace ClaudeWorkbench.Launcher;

public enum InstanceStatus
{
    Stopped,
    Starting,
    Running,
    Error,
}

// Owns the full lifecycle of one workspace instance: allocate ports, write an isolated
// config, launch the host (which spawns the sidecar) and a browser window, all inside one
// Job Object. Stop() (or disposing the launcher) terminates the job, taking host + sidecar
// + browser down together. If the host exits on its own (the browser tab was closed and
// CWB_EXIT_WITH_BROWSER stopped it), Poll() notices and cleans up.
public sealed class InstanceController : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly LauncherState state;
    private JobObject? job;
    private Process? host;
    private Process? browser;

    public InstanceController(WorkspaceEntry workspace, LauncherState state)
    {
        Workspace = workspace;
        this.state = state;
    }

    public WorkspaceEntry Workspace { get; }

    public InstanceStatus Status { get; private set; } = InstanceStatus.Stopped;

    public int HostPort { get; private set; }

    public int SidecarPort { get; private set; }

    public string? LastError { get; private set; }

    public string Url => $"http://localhost:{HostPort}";

    public async Task StartAsync(IEnumerable<int> portsInUse, bool launchBrowser = true)
    {
        if (Status is InstanceStatus.Running or InstanceStatus.Starting)
        {
            return;
        }

        LastError = null;
        Status = InstanceStatus.Starting;
        try
        {
            if (!File.Exists(state.HostExePath))
            {
                throw new FileNotFoundException($"Host exe not found. Set it in Settings. ({state.HostExePath})");
            }

            (HostPort, SidecarPort) = Ports.FindFreePair(portsInUse);
            string instanceDir = InstanceDirectory();
            string configPath = WriteConfig(instanceDir);

            job = new JobObject();
            host = LaunchHost(configPath, instanceDir);
            job.Assign(host.Handle);

            if (!await WaitForHealthAsync(TimeSpan.FromSeconds(40)))
            {
                throw new TimeoutException("The host did not report healthy in time.");
            }

            if (launchBrowser)
            {
                browser = LaunchBrowser(instanceDir);
                if (browser is not null)
                {
                    job.Assign(browser.Handle);
                }
            }

            Status = InstanceStatus.Running;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = InstanceStatus.Error;
            Stop();
        }
    }

    // Detect a backend that exited on its own (e.g. the browser tab was closed and the
    // host's CWB_EXIT_WITH_BROWSER shut it down).
    public void Poll()
    {
        if (Status == InstanceStatus.Running && host is { HasExited: true })
        {
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            job?.Terminate();
        }
        catch
        {
            // best effort
        }

        job?.Dispose();
        job = null;
        host = null;
        browser = null;
        if (Status != InstanceStatus.Error)
        {
            Status = InstanceStatus.Stopped;
        }
    }

    private Process LaunchHost(string configPath, string instanceDir)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = state.HostExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(state.HostExePath) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(instanceDir);

        startInfo.Environment["ASPNETCORE_URLS"] = Url;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["Sidecar__BaseUrl"] = $"http://localhost:{SidecarPort}";
        startInfo.Environment["Sidecar__McpUrl"] = $"{Url}/mcp";
        startInfo.Environment["Sidecar__Directory"] = state.SidecarDirectory;
        // The tab owns the instance: closing the last tab shuts the host (and its sidecar) down.
        startInfo.Environment["CWB_EXIT_WITH_BROWSER"] = "1";

        Process process = new() { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private Process? LaunchBrowser(string instanceDir)
    {
        BrowserResolver.Resolved resolved = BrowserResolver.Resolve(state);

        if (resolved.ExePath is null || !resolved.IsChromium)
        {
            // Default browser (or unresolved): open the URL via the shell. This window can't
            // be job-controlled, but closing the tab still stops the backend via the host.
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            return null;
        }

        string profileDir = Path.Combine(instanceDir, "browser-profile");
        Directory.CreateDirectory(profileDir);

        ProcessStartInfo startInfo = new()
        {
            FileName = resolved.ExePath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--app={Url}");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDir}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");

        Process process = new() { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private async Task<bool> WaitForHealthAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host is { HasExited: true })
            {
                return false;
            }

            try
            {
                using HttpResponseMessage response = await Http.GetAsync($"{Url}/health");
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // not up yet
            }

            await Task.Delay(500);
        }

        return false;
    }

    private string WriteConfig(string instanceDir)
    {
        string configDir = Path.Combine(instanceDir, "config");
        Directory.CreateDirectory(configDir);
        string runtimeRoot = Path.Combine(instanceDir, "runtime");
        Directory.CreateDirectory(runtimeRoot);

        var config = new
        {
            Monitor = new
            {
                WatchedSolutionPath = Workspace.SolutionPath,
                RuntimeRoot = runtimeRoot,
                WinMergeCandidatePaths = Array.Empty<string>(),
            },
        };

        string configPath = Path.Combine(configDir, "appsettings.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return configPath;
    }

    private string InstanceDirectory()
    {
        string dir = Path.Combine(LauncherState.StateDirectory, "instances", Workspace.Id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose() => Stop();
}
