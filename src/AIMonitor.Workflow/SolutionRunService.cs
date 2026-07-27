using System.Diagnostics;
using System.Text.Json;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Launches the built executable of the watched solution — the "run" half of build & run. Capability,
// not a workflow phase: an operator "Run" button and the optional run-after-accept both call it. Finds
// the one executable project (OutputType Exe/WinExe), locates its built exe under bin/<config>, and
// starts it detached. Requires a prior build (it runs the artifact, it does not compile). Operator-only;
// the agent never launches processes.
public sealed class SolutionRunService
{
    // The most recently launched app, so a re-run stops the previous instance instead of piling up
    // windows across accepts. Process lives for the Host's lifetime; static is the right scope.
    private static Process? lastLaunched;
    private static Process? lastRunBrowser;
    private static StreamWriter? lastRunLog;
    private static readonly object gate = new();
    private static readonly object logGate = new();

    public sealed record RunResult(bool IsError, string Message, string? ExecutablePath);

    public RunResult Run(MonitorSettings settings, string configuration)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return new RunResult(true, "Run skipped — the watched solution file is missing.", null);
        }

        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;
        string config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();

        List<string> runnable = FindRunnableProjects(solutionRoot);
        if (runnable.Count == 0)
        {
            return new RunResult(true, "Nothing to run — no project with OutputType Exe/WinExe.", null);
        }

        if (runnable.Count > 1)
        {
            return new RunResult(
                true,
                $"Multiple executable projects ({runnable.Count}) — pick one from the Source tab's Run action.",
                null);
        }

        return LaunchProject(runnable[0], config, solutionRoot, settings);
    }

    // Run a specific project the operator picked from the Source tab's project dropdown. Unlike Run(),
    // this never guesses — no "which one?" ambiguity — so it's the path used whenever there's a UI to
    // choose from. Still requires a prior build; it launches the artifact, it does not compile.
    public RunResult RunProject(MonitorSettings settings, string configuration, string projectPath)
    {
        string config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return new RunResult(true, "Run skipped — the selected project file is missing.", null);
        }

        string solutionRoot = Path.GetDirectoryName(Path.GetFullPath(settings.WatchedSolutionPath))
            ?? settings.WatchedProjectFolder;
        return LaunchProject(Path.GetFullPath(projectPath), config, solutionRoot, settings);
    }

    // Start `project` detached, stopping any previously launched instance first. Shared by Run()
    // (single auto-detected project) and RunProject() (operator-picked). Web apps go through
    // `dotnet run` (see LaunchWebProject); console/WinForms apps run their built exe directly.
    private static RunResult LaunchProject(string project, string config, string solutionRoot, MonitorSettings settings)
    {
        string projectDirectory = Path.GetDirectoryName(project) ?? solutionRoot;
        string projectName = Path.GetFileNameWithoutExtension(project);

        // A web app (Microsoft.NET.Sdk.Web) can't be run as a bare exe from bin: a plain build leaves
        // no wwwroot/static-web-assets there, and the raw exe boots in Production, where the static
        // web assets manifest isn't loaded — Kestrel starts but serves a blank page. Launch it through
        // `dotnet run`, which builds if needed and honors launchSettings.json (environment,
        // applicationUrl, launchBrowser), exactly as VS and the CLI do.
        if (IsWebProject(project))
        {
            return LaunchWebProject(project, projectName, config, projectDirectory, settings);
        }

        string binConfig = Path.Combine(projectDirectory, "bin", config);
        if (!Directory.Exists(binConfig))
        {
            return new RunResult(true, $"No {config} build output for {projectName} — build first.", null);
        }

        // The exe lives under bin/<config>/<tfm>/; the tfm folder varies (net10.0, net8.0-windows, …),
        // so search and take the freshest match.
        string? executable = Directory
            .EnumerateFiles(binConfig, projectName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (executable is null)
        {
            return new RunResult(true, $"No built {projectName}.exe found under bin/{config} — build first.", null);
        }

        try
        {
            lock (gate)
            {
                StopLastLaunched();
                lastLaunched = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? projectDirectory,
                });
            }

            return new RunResult(false, $"Launched {projectName} ({config}).", executable);
        }
        catch (Exception ex)
        {
            return new RunResult(true, $"Could not launch {projectName}: {ex.Message}", executable);
        }
    }

    // Launch a web app via `dotnet run --project <csproj> -c <config> [--launch-profile <p>]`. Prefers an
    // "http" launch profile when the project defines one (plain HTTP, no dev-cert prompt), falls back to the
    // first Project-command profile, then to no explicit profile. `dotnet run` builds if needed, so unlike
    // the exe path this does not require a prior build. Output is redirected to a per-workspace run log
    // (UseShellExecute=false) so a crash is captured instead of vanishing with a console window that closes
    // too fast to read — and so the process tree can be killed reliably, freeing its port for the next Run.
    private static RunResult LaunchWebProject(
        string project, string projectName, string config, string projectDirectory, MonitorSettings settings)
    {
        ResolvedProfile? profile = ResolveLaunchProfile(projectDirectory, "http");
        List<string> arguments = ["run", "--project", project, "-c", config];
        if (profile is not null)
        {
            arguments.Add("--launch-profile");
            arguments.Add(profile.Name);
        }

        // `dotnet run` (unlike `dotnet watch`/VS) does NOT honor launchBrowser, so open the browser
        // ourselves the moment Kestrel reports the address it actually bound. On unless a profile says false.
        bool wantBrowser = profile?.LaunchBrowser ?? true;
        int browserOpened = 0;

        string logPath = RunLogPath(settings, projectName);
        try
        {
            lock (gate)
            {
                StopLastLaunched();
                Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? projectDirectory);

                ProcessStartInfo startInfo = new("dotnet")
                {
                    WorkingDirectory = projectDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                // The child must bind ITS OWN ports (the launch profile's applicationUrl), never the
                // workbench host's. The Launcher starts the host with ASPNETCORE_URLS *and*
                // Kestrel__Endpoints__Http__Url pointing at the host's port (e.g. :6100); both are inherited
                // here and, via IConfiguration, override the profile's URL — so the child would try to bind
                // the host's port and die on "address already in use". Strip every inherited address/port
                // key so only the launch profile decides where the child listens.
                startInfo.Environment.Remove("ASPNETCORE_URLS");
                startInfo.Environment.Remove("ASPNETCORE_HTTP_PORTS");
                startInfo.Environment.Remove("ASPNETCORE_HTTPS_PORTS");
                foreach (string key in startInfo.Environment.Keys
                    .Where(k => k.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    startInfo.Environment.Remove(key);
                }
                if (profile is null)
                {
                    // No profile to carry ASPNETCORE_ENVIRONMENT — force Development so the static-web-assets
                    // manifest loads (otherwise a Blazor app serves a blank page: wwwroot not found).
                    startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                }

                StreamWriter log = new(logPath, append: false) { AutoFlush = true };
                log.WriteLine($"dotnet {string.Join(' ', arguments)}");
                log.WriteLine($"(cwd: {projectDirectory}; {(profile is null ? "no profile — forced Development" : $"profile: {profile.Name}")})");
                log.WriteLine("----------------------------------------");

                Process process = new() { StartInfo = startInfo };
                process.OutputDataReceived += (_, e) =>
                {
                    WriteRunLog(log, e.Data);
                    if (!wantBrowser || e.Data is null)
                    {
                        return;
                    }

                    // Open the app the moment IT reports listening — the host runs the build, so it already
                    // knows exactly when the child is up. Open the operator's chosen browser straight at that
                    // URL: a normal window in the workbench's own browser profile — instant, isolated, no
                    // splash, no redirect, no host-port detour.
                    string? url = ExtractListeningUrl(e.Data);
                    if (url is not null && Interlocked.Exchange(ref browserOpened, 1) == 0)
                    {
                        OpenRunTargetBrowser(url);
                    }
                };
                process.ErrorDataReceived += (_, e) => WriteRunLog(log, e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lastLaunched = process;
                lastRunLog = log;
            }

            string profileNote = profile is null ? "no profile — forced Development" : $"--launch-profile {profile.Name}";
            return new RunResult(
                false,
                $"Launched {projectName} via dotnet run ({config}, {profileNote}). Run log: {logPath}",
                project);
        }
        catch (Exception ex)
        {
            return new RunResult(true, $"Could not launch {projectName} via dotnet run: {ex.Message}", project);
        }
    }

    // Stop the previously launched app (whole tree) so a re-run replaces it instead of piling up windows,
    // and close its run log so the next Run starts a clean one.
    private static void StopLastLaunched()
    {
        if (lastLaunched is { HasExited: false })
        {
            try
            {
                lastLaunched.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort: the previous instance may already be gone or unkillable.
            }
        }

        if (lastRunBrowser is { HasExited: false })
        {
            try
            {
                lastRunBrowser.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort: the run-target browser window may already be closed.
            }
        }

        if (lastRunLog is not null)
        {
            lock (logGate)
            {
                try
                {
                    lastRunLog.Flush();
                    lastRunLog.Dispose();
                }
                catch (Exception)
                {
                    // Best-effort: a partially-written log is fine.
                }

                lastRunLog = null;
            }
        }
    }

    // Append one line of child output to the run log. Serialized against the log's disposal in
    // StopLastLaunched, and tolerant of late output arriving after the writer was closed.
    private static void WriteRunLog(StreamWriter writer, string? data)
    {
        if (data is null)
        {
            return;
        }

        lock (logGate)
        {
            try
            {
                writer.WriteLine(data);
            }
            catch (Exception)
            {
                // The writer was disposed when the run was stopped; drop late output.
            }
        }
    }

    // Per-workspace run log next to the other workspace logs, one file per project (truncated each Run).
    private static string RunLogPath(MonitorSettings settings, string projectName)
    {
        string root = MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings);
        string safeName = string.Concat(projectName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(root, "logs", $"run-{safeName}.log");
    }

    // Pull the bound address out of Kestrel's "Now listening on: <url>" line, http/https only.
    private static string? ExtractListeningUrl(string line)
    {
        const string marker = "Now listening on:";
        int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string url = line[(index + marker.Length)..].Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    // Open the operator's default browser at the app's URL. Best-effort — a missing browser just means no tab.
    private static void OpenBrowser(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser, or the shell rejected the URL.
        }
    }

    private static bool IsWebProject(string projectPath)
    {
        try
        {
            return File.ReadAllText(projectPath).Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The launch profile to use with `dotnet run`: the preferred one if present, else the first
    // Project-command profile, else null (let dotnet run choose). Carries launchBrowser so the caller can
    // open a browser. Never throws on a missing/malformed launchSettings.json — falls back to no profile.
    private static ResolvedProfile? ResolveLaunchProfile(string projectDirectory, string preferred)
    {
        string launchSettings = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettings))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(launchSettings));
            if (!document.RootElement.TryGetProperty("profiles", out JsonElement profiles)
                || profiles.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            ResolvedProfile? firstProjectProfile = null;
            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                if (!IsProjectProfile(profile.Value))
                {
                    continue;
                }

                ResolvedProfile resolved = new(profile.Name, WantsBrowser(profile.Value));
                if (string.Equals(profile.Name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return resolved;
                }

                firstProjectProfile ??= resolved;
            }

            return firstProjectProfile;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // launchBrowser from a profile — default true unless explicitly false, so the operator gets a browser
    // in the common case (SDK templates set it true) while an explicit false is respected.
    private static bool WantsBrowser(JsonElement profile) =>
        !profile.TryGetProperty("launchBrowser", out JsonElement value) || value.ValueKind != JsonValueKind.False;

    // Open the run target in the operator's CHOSEN browser (passed by the Launcher as CWB_BROWSER_EXE) in a
    // normal window (NOT --app) using the browser's DEFAULT profile, so the operator's saved passwords /
    // logins / autofill are available for testing the app. Falls back to the OS default browser if no
    // Chromium browser was configured.
    private static void OpenRunTargetBrowser(string url)
    {
        string? browserExe = Environment.GetEnvironmentVariable("CWB_BROWSER_EXE");
        if (string.IsNullOrWhiteSpace(browserExe) || !File.Exists(browserExe))
        {
            OpenBrowser(url);
            return;
        }

        try
        {
            // No --user-data-dir: the browser's default profile, so saved passwords/logins are present.
            // (A cold browser may restore its prior session — that's a Chrome "On startup" setting on the
            // operator's side, not ours.)
            ProcessStartInfo startInfo = new(browserExe) { UseShellExecute = false };
            startInfo.ArgumentList.Add("--new-window");
            startInfo.ArgumentList.Add(url);
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            lastRunBrowser = Process.Start(startInfo);
        }
        catch (Exception)
        {
            OpenBrowser(url);
        }
    }

    private sealed record ResolvedProfile(string Name, bool LaunchBrowser);

    // A profile that launches the project itself (as opposed to IIS Express or an external executable).
    // A missing commandName defaults to "Project", matching dotnet's own behavior.
    private static bool IsProjectProfile(JsonElement profile)
    {
        if (!profile.TryGetProperty("commandName", out JsonElement commandName)
            || commandName.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        return string.Equals(commandName.GetString(), "Project", StringComparison.OrdinalIgnoreCase);
    }

    // A project is runnable if its .csproj declares an executable OutputType, or uses the Web SDK
    // (which produces a runnable app without an explicit OutputType). Cheap text check — good enough to
    // distinguish apps from libraries without a full MSBuild evaluation.
    private static List<string> FindRunnableProjects(string solutionRoot)
    {
        List<string> runnable = new();
        foreach (string csproj in Directory.EnumerateFiles(solutionRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(solutionRoot, csproj);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(csproj);
            }
            catch (Exception)
            {
                continue;
            }

            if (text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                runnable.Add(csproj);
            }
        }

        return runnable;
    }
}
