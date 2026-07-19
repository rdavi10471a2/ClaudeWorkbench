using System.Net;
using System.Net.Sockets;

namespace ClaudeWorkbench.Launcher;

// Finds a free host/sidecar port pair. Host ports are spaced by 100 (6100, 6200, ...)
// with the sidecar at host+10, matching the manual convention and keeping instances
// visually distinct.
public static class Ports
{
    public static (int HostPort, int SidecarPort) FindFreePair(IEnumerable<int> inUse)
    {
        HashSet<int> taken = new(inUse);
        for (int host = 6100; host <= 7000; host += 100)
        {
            int sidecar = host + 10;
            if (taken.Contains(host) || taken.Contains(sidecar))
            {
                continue;
            }

            if (IsFree(host) && IsFree(sidecar))
            {
                return (host, sidecar);
            }
        }

        throw new InvalidOperationException("No free port pair found in 6100-7000.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            TcpListener listener = new(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

// Resolves the configured browser to an executable and whether it speaks Chromium's
// --app flags (which give a distinct, job-controllable window). Firefox / default-shell
// can't be job-controlled, so those windows are opened but not force-closed on Stop.
public static class BrowserResolver
{
    public sealed record Resolved(string? ExePath, bool IsChromium);

    public static Resolved Resolve(LauncherState state)
    {
        return state.Browser switch
        {
            BrowserKind.Chrome => new Resolved(FirstExisting(ChromePaths()), true),
            BrowserKind.Edge => new Resolved(FirstExisting(EdgePaths()), true),
            BrowserKind.Custom => new Resolved(
                string.IsNullOrWhiteSpace(state.CustomBrowserPath) ? null : state.CustomBrowserPath,
                true),
            _ => new Resolved(null, false), // DefaultShell
        };
    }

    private static string? FirstExisting(IEnumerable<string> candidates)
        => candidates.FirstOrDefault(File.Exists);

    private static IEnumerable<string> ChromePaths()
    {
        yield return Combine(Environment.SpecialFolder.ProgramFiles, @"Google\Chrome\Application\chrome.exe");
        yield return Combine(Environment.SpecialFolder.ProgramFilesX86, @"Google\Chrome\Application\chrome.exe");
        yield return Combine(Environment.SpecialFolder.LocalApplicationData, @"Google\Chrome\Application\chrome.exe");
    }

    private static IEnumerable<string> EdgePaths()
    {
        yield return Combine(Environment.SpecialFolder.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe");
        yield return Combine(Environment.SpecialFolder.ProgramFiles, @"Microsoft\Edge\Application\msedge.exe");
    }

    private static string Combine(Environment.SpecialFolder folder, string tail)
        => Path.Combine(Environment.GetFolderPath(folder), tail);
}
