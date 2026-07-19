using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeWorkbench.Host.Services;

// Host-side, operator-authorized git access to the watched solution. The agent
// NEVER runs git — commit and push are deliberate operator actions in the UI, the
// same posture as the accept-write in EngineReviewWorkflow. This is a thin, testable
// wrapper over the git CLI; the working directory is always the watched folder (or
// any path inside its repo — git walks up to the enclosing .git).
//
// Policy for now (operator-driven): accepts write bytes to the working tree; the
// operator clicks Commit to bundle the current working-tree changes into one commit,
// and clicks Push to send them to the remote. Nothing here pushes automatically.
public sealed partial class GitService
{
    private readonly string gitExecutable;

    public GitService(string gitExecutable = "git")
    {
        this.gitExecutable = gitExecutable;
    }

    // Core exec: run git in workingDirectory with args; capture stdout/stderr/exit.
    // Never throws for a non-zero git exit — the caller inspects GitResult.Ok.
    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = gitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        using Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            // git not on PATH / bad executable — surface as a failed result, not a throw.
            return new GitResult(-1, string.Empty, exception.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new GitResult(
            process.ExitCode,
            stdout.ToString().TrimEnd('\r', '\n'),
            stderr.ToString().TrimEnd('\r', '\n'));
    }

    // True when `directory` is inside a git work tree.
    public async Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    // Absolute path of the enclosing repository root, or null if not a repo.
    public async Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    // True when at least one remote is configured.
    public async Task<bool> HasRemoteAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["remote"], cancellationToken).ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Length > 0;
    }

    // Initialize a new repository at `directory` on the given default branch. Used
    // by the "prompt to add git" path when the watched solution is not yet a repo.
    public Task<GitResult> InitAsync(string directory, string defaultBranch = "main", CancellationToken cancellationToken = default)
        => RunAsync(directory, ["init", "-b", defaultBranch], cancellationToken);

    // Branch, upstream, ahead/behind, remote presence, and the changed-file set.
    public async Task<GitStatus> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult status = await RunAsync(
            directory,
            ["status", "--porcelain=v1", "--branch"],
            cancellationToken).ConfigureAwait(false);
        if (!status.Ok)
        {
            return GitStatus.NotARepository;
        }

        bool hasRemote = await HasRemoteAsync(directory, cancellationToken).ConfigureAwait(false);
        return ParseStatus(status.StdOut, hasRemote);
    }

    // Operator-batched commit: stage everything in the working tree (including new
    // and deleted files) and commit with the given message. Fails cleanly if there
    // is nothing to commit or the message is empty.
    public async Task<GitResult> CommitAsync(string directory, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitResult(-1, string.Empty, "Commit message must not be empty.");
        }

        GitResult stage = await RunAsync(directory, ["add", "-A"], cancellationToken).ConfigureAwait(false);
        if (!stage.Ok)
        {
            return stage;
        }

        return await RunAsync(directory, ["commit", "-m", message], cancellationToken).ConfigureAwait(false);
    }

    // Explicit, user-driven push. When remote and branch are supplied the upstream is
    // set (-u), which is what a branch's first push needs; otherwise a plain push.
    public Task<GitResult> PushAsync(
        string directory,
        string? remote = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(remote) && !string.IsNullOrWhiteSpace(branch))
        {
            return RunAsync(directory, ["push", "-u", remote, branch], cancellationToken);
        }

        return RunAsync(directory, ["push"], cancellationToken);
    }

    private static GitStatus ParseStatus(string porcelain, bool hasRemote)
    {
        string? branch = null;
        string? upstream = null;
        int ahead = 0;
        int behind = 0;
        List<GitChange> changes = [];

        foreach (string rawLine in porcelain.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                string info = line[3..];

                Match ab = AheadBehindRegex().Match(info);
                if (ab.Success)
                {
                    if (ab.Groups["ahead"].Success)
                    {
                        ahead = int.Parse(ab.Groups["ahead"].Value);
                    }

                    if (ab.Groups["behind"].Success)
                    {
                        behind = int.Parse(ab.Groups["behind"].Value);
                    }

                    info = info[..ab.Index].TrimEnd();
                }

                // "No commits yet on main" (fresh repo) has no upstream.
                const string noCommits = "No commits yet on ";
                if (info.StartsWith(noCommits, StringComparison.Ordinal))
                {
                    branch = info[noCommits.Length..].Trim();
                }
                else if (info.Contains("...", StringComparison.Ordinal))
                {
                    int sep = info.IndexOf("...", StringComparison.Ordinal);
                    branch = info[..sep];
                    upstream = info[(sep + 3)..].Trim();
                }
                else
                {
                    branch = info.Trim();
                }

                continue;
            }

            // Change line: two-char XY status + space + path (renames use " -> ").
            if (line.Length >= 3)
            {
                string code = line[..2];
                string path = line[3..];
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path[(arrow + 4)..];
                }

                changes.Add(new GitChange(code, path));
            }
        }

        return new GitStatus(true, branch, upstream, ahead, behind, hasRemote, changes);
    }

    [GeneratedRegex(@"\[(?:ahead (?<ahead>\d+))?(?:, )?(?:behind (?<behind>\d+))?\]")]
    private static partial Regex AheadBehindRegex();
}

// Result of a single git invocation. Ok == process exited 0.
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

// One changed path in the working tree, with its porcelain XY status code.
public sealed record GitChange(string Code, string Path);

// A snapshot of the watched repo's state for the operator's Git panel.
public sealed record GitStatus(
    bool IsRepository,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool HasRemote,
    IReadOnlyList<GitChange> Changes)
{
    public static GitStatus NotARepository { get; } =
        new(false, null, null, 0, 0, false, []);

    // True when the working tree has staged or unstaged changes to commit.
    public bool HasChanges => Changes.Count > 0;
}
