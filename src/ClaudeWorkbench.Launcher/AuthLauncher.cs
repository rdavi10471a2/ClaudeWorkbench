using System.Diagnostics;

namespace ClaudeWorkbench.Launcher;

// Opens a terminal window on the Claude or GitHub CLI login flow.
//
// WHY A TERMINAL, AND NOT A REDIRECTED PROCESS
// --------------------------------------------
// Both `claude auth login` and `gh auth login` are interactive OAuth flows: they print a URL and a
// one-time code, open a browser, and then BLOCK on the console waiting for the round-trip to
// complete. That only works with a real console attached.
//
// The launcher's own child processes (host, sidecar) run with RedirectStandardOutput = true and
// CreateNoWindow = true — correct for a long-lived service we capture logs from, and exactly wrong
// here. A redirected login has no TTY: it cannot show the code, cannot accept the paste-back some
// flows use, and would simply hang with the launcher holding a pipe nobody reads.
//
// So we do the opposite of LaunchHost: give the CLI its OWN visible console (cmd.exe /k) and get
// out of the way. /k keeps the window open after the command returns, so the user can read the
// result — "Login successful", or an error — instead of it vanishing on exit.
//
// These processes are deliberately NOT placed in the launcher's Job Object. A login is the user's,
// not an instance's; it must outlive the launcher (you might close the launcher while the browser
// tab is still completing the flow), and killing it when the launcher exits would abort a login
// mid-handshake.
internal static class AuthLauncher
{
    // Each provider names its CLI, the candidate executables to resolve on PATH (the Claude CLI
    // ships as a Windows shim — claude.cmd/.exe/.ps1 — so we accept any), and the login/status
    // argument lists. Verified against the installed CLIs:
    //   claude auth login | claude auth status
    //   gh auth login     | gh auth status
    internal sealed record Provider(
        string DisplayName,
        string[] Executables,
        string[] LoginArgs,
        string[] LogoutArgs,
        string[] StatusArgs,
        string InstallHint);

    internal static readonly Provider Claude = new(
        "Claude",
        ["claude.cmd", "claude.exe", "claude.ps1", "claude"],
        ["auth", "login"],
        ["auth", "logout"],
        ["auth", "status"],
        "The Claude CLI was not found on PATH. Install it with:\r\n\r\n    npm install -g @anthropic-ai/claude-code\r\n\r\nthen reopen the launcher.");

    internal static readonly Provider GitHub = new(
        "GitHub",
        ["gh.exe", "gh"],
        ["auth", "login"],
        ["auth", "logout"],
        ["auth", "status"],
        "The GitHub CLI (gh) was not found on PATH. Install it from https://cli.github.com/ , then reopen the launcher.");

    // Returns null with a reason if the CLI is missing, so the caller can show a helpful dialog
    // rather than flashing a console that says "command not found" and disappears.
    internal static string? ResolveExecutable(Provider provider)
    {
        foreach (string candidate in provider.Executables)
        {
            string? resolved = FindOnPath(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    internal static void LaunchLogin(Provider provider) => LaunchInTerminal(provider, provider.LoginArgs);

    internal static void LaunchLogout(Provider provider) => LaunchInTerminal(provider, provider.LogoutArgs);

    internal static void LaunchStatus(Provider provider) => LaunchInTerminal(provider, provider.StatusArgs);

    private static void LaunchInTerminal(Provider provider, string[] args)
    {
        // Run via cmd.exe /k rather than the CLI directly, for three reasons: the Claude CLI is a
        // .cmd shim that needs a command interpreter; /k keeps the window open so the result is
        // readable; and cmd resolves the shim off PATH itself, matching how the user runs it.
        //
        // The resolved executable name is passed (not the full path) so the .cmd shim's own PATH
        // logic still runs; ResolveExecutable already proved it exists.
        string exeName = Path.GetFileName(ResolveExecutable(provider) ?? provider.Executables[0]);

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            // A visible, interactive console. UseShellExecute = true gives cmd its own window and
            // its own process group, so it is independent of the launcher's lifetime.
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/k");
        startInfo.ArgumentList.Add("title");
        startInfo.ArgumentList.Add($"ClaudeWorkbench - {provider.DisplayName} sign-in");
        startInfo.ArgumentList.Add("&");
        startInfo.ArgumentList.Add(exeName);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo);
    }

    // Minimal PATH resolver: walk %PATH% for an exact filename. Candidates already carry their
    // extension, so PATHEXT expansion is unnecessary here.
    private static string? FindOnPath(string fileName)
    {
        // An absolute path that exists is its own answer.
        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
        {
            return fileName;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // A malformed PATH entry must not stop the search.
            }
        }

        return null;
    }
}
