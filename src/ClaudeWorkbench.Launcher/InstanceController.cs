using System.Diagnostics;
using System.Net.Sockets;
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly LauncherState state;
    private JobObject? job;
    private Process? host;
    private Process? browser;
    private StreamWriter? hostLog;

    // Where this instance's host stdout/stderr is captured, for diagnosing a crash.
    public string? HostLogPath { get; private set; }

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

            // Open the browser IMMEDIATELY — pointed at a static loading page (spinner + self-redirect
            // to the host once it answers), not the app URL. The host can take many seconds to come up
            // on a busy machine, and nothing it serves can paint until it does; opening early means the
            // whole cold-boot window shows a visible "Starting…" instead of an absent/blank window.
            if (launchBrowser)
            {
                browser = LaunchBrowser(instanceDir);
                if (browser is not null)
                {
                    job.Assign(browser.Handle);
                }
            }

            // Track health for the Launcher's own status; the browser does its own waiting via the
            // loading page. Only a host that actually EXITED is a failure — a live-but-slow host is
            // fine (the loading page redirects once it catches up) and must not be killed.
            bool healthy = await WaitForHealthAsync(TimeSpan.FromSeconds(120));
            if (!healthy && host.HasExited)
            {
                throw new InvalidOperationException("The host process exited during startup.");
            }

            Status = InstanceStatus.Running;
        }
        catch (Exception exception)
        {
            Status = InstanceStatus.Error;
            Stop(); // flushes + closes the host log
            LastError = exception.Message + ReadLogTail();
        }
    }

    private string ReadLogTail()
    {
        try
        {
            if (HostLogPath is not null && File.Exists(HostLogPath))
            {
                string[] lines = File.ReadAllLines(HostLogPath);
                string tail = string.Join(Environment.NewLine, lines.TakeLast(12));
                return $"{Environment.NewLine}{Environment.NewLine}Host log ({HostLogPath}):{Environment.NewLine}{tail}";
            }
        }
        catch
        {
            // best effort
        }

        return HostLogPath is not null ? $"{Environment.NewLine}(see {HostLogPath})" : string.Empty;
    }

    // Detect a backend that went away on its own (e.g. the browser tab was closed and the
    // host's CWB_EXIT_WITH_BROWSER shut it down). Use the PORT as ground truth: it stops
    // listening early in shutdown, so the row flips to "stopped" promptly instead of waiting
    // for the whole process to finish exiting.
    public void Poll()
    {
        if (Status != InstanceStatus.Running)
        {
            return;
        }

        bool exited;
        try
        {
            exited = host is null || host.HasExited;
        }
        catch
        {
            exited = true;
        }

        if (exited || !PortResponds(HostPort))
        {
            Stop();
        }
    }

    private static bool PortResponds(int port)
    {
        try
        {
            using TcpClient client = new();
            return client.ConnectAsync("127.0.0.1", port).Wait(300) && client.Connected;
        }
        catch
        {
            return false;
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
        hostLog?.Dispose();
        hostLog = null;
        if (Status != InstanceStatus.Error)
        {
            Status = InstanceStatus.Stopped;
        }
    }

    private void WriteHostLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        StreamWriter? writer = hostLog;
        if (writer is null)
        {
            return;
        }

        lock (writer)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch
            {
                // best effort
            }
        }
    }

    private Process LaunchHost(string configPath, string instanceDir)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = state.HostExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(state.HostExePath) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(instanceDir);

        // The host's appsettings.json pins Kestrel:Endpoints:Http:Url to :6100, and that
        // Kestrel endpoint config OUTRANKS ASPNETCORE_URLS — so a second instance would also
        // try to bind :6100 and crash. An environment variable outranks appsettings.json, so
        // override the exact endpoint key to force this instance's port unambiguously.
        startInfo.Environment["Kestrel__Endpoints__Http__Url"] = Url;
        startInfo.Environment["ASPNETCORE_URLS"] = Url;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["Sidecar__BaseUrl"] = $"http://localhost:{SidecarPort}";
        startInfo.Environment["Sidecar__McpUrl"] = $"{Url}/mcp";
        startInfo.Environment["Sidecar__Directory"] = state.SidecarDirectory;
        // The tab owns the instance: closing the last tab shuts the host (and its sidecar) down.
        startInfo.Environment["CWB_EXIT_WITH_BROWSER"] = "1";

        // Tell the host which browser the operator chose (the same one the workbench window opens in),
        // so a Source-tab "Run" launches the watched app in that browser — a normal window, not --app —
        // instead of guessing the OS default (which drags in the user's other session tabs).
        BrowserResolver.Resolved runBrowser = BrowserResolver.Resolve(state);
        if (runBrowser.ExePath is not null && runBrowser.IsChromium)
        {
            startInfo.Environment["CWB_BROWSER_EXE"] = runBrowser.ExePath;
        }

        // Capture host output so a startup crash is diagnosable (the launcher has no console).
        HostLogPath = Path.Combine(instanceDir, "host.log");
        hostLog = new StreamWriter(HostLogPath, append: false) { AutoFlush = true };

        Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => WriteHostLog(e.Data);
        process.ErrorDataReceived += (_, e) => WriteHostLog(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private Process? LaunchBrowser(string instanceDir)
    {
        BrowserResolver.Resolved resolved = BrowserResolver.Resolve(state);
        string openUrl = BuildStartupUrl();

        if (resolved.ExePath is null || !resolved.IsChromium)
        {
            // Default browser (or unresolved): open the URL via the shell. This window can't
            // be job-controlled, but closing the tab still stops the backend via the host.
            Process.Start(new ProcessStartInfo(openUrl) { UseShellExecute = true });
            return null;
        }

        string profileDir = Path.Combine(instanceDir, "browser-profile");
        Directory.CreateDirectory(profileDir);

        ProcessStartInfo startInfo = new()
        {
            FileName = resolved.ExePath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--app={openUrl}");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDir}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        // Without this, the --app window opens at a default that spills its bottom past the work
        // area (behind the taskbar) — a "faux maximized" whose bottom edge is off-screen. Open it
        // TRULY maximized so it respects the work area, exactly like clicking the maximize button.
        startInfo.ArgumentList.Add("--start-maximized");

        Process process = new() { StartInfo = startInfo };
        process.Start();
        return process;
    }

    // The URL the browser first opens: a static loading page that shows a spinner and self-redirects
    // to the app the instant the host answers, so the cold-boot wait is a visible "Starting…" rather
    // than a delayed blank window. Falls back to the app URL directly if the page didn't ship.
    private string BuildStartupUrl()
    {
        string loadingHtml = Path.Combine(AppContext.BaseDirectory, "loading.html");
        return File.Exists(loadingHtml)
            ? new Uri(loadingHtml).AbsoluteUri + "?target=" + Uri.EscapeDataString(Url)
            : Url;
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

        // The instance directory IS the runtime root: provisioning writes its
        // watched-solutions\ and logs\ here, alongside this config and host.log, so everything
        // for one workspace sits under <workbench>\runtime\<workspace>.
        var config = new
        {
            Monitor = new
            {
                WatchedSolutionPath = Workspace.SolutionPath,
                RuntimeRoot = instanceDir,
            },
        };

        string configPath = Path.Combine(configDir, "appsettings.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return configPath;
    }

    private string InstanceDirectory() => state.InstanceDirectoryFor(Workspace);

    public void Dispose() => Stop();
}
