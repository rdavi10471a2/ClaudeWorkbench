using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeWorkbench.Host.Services;

// Host-side, operator-authorized git access to the watched solution. The agent
// reaches this only through gated MCP tools; the operator reaches it through the Git
// panel. Either way this is a thin wrapper that launches the `git` executable as a
// child process (no shell, no HTTP) and captures its output. Args are passed as an
// argv array, so there is no shell-injection surface.
public sealed partial class GitService
{
    private const char Unit = ''; // ASCII unit separator, for safe log field splitting.
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

    public async Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    public async Task<bool> HasRemoteAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["remote"], cancellationToken).ConfigureAwait(false);
        return result.Ok && result.StdOut.Trim().Length > 0;
    }

    public Task<GitResult> InitAsync(string directory, string defaultBranch = "main", CancellationToken cancellationToken = default)
        => RunAsync(directory, ["init", "-b", defaultBranch], cancellationToken);

    public async Task<GitStatus> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult status = await RunAsync(
            directory,
            ["status", "--porcelain=v1", "--branch"],
            cancellationToken).ConfigureAwait(false);
        // ExitCode -1 is our sentinel for "the git process could not be started"
        // (e.g. git not installed / not on PATH), distinct from git running and
        // reporting "not a repository" (exit 128).
        if (status.ExitCode == -1)
        {
            return GitStatus.Unavailable(string.IsNullOrWhiteSpace(status.StdErr)
                ? "The git executable could not be started. Is git installed and on PATH?"
                : status.StdErr);
        }

        if (!status.Ok)
        {
            return GitStatus.NotARepository;
        }

        bool hasRemote = await HasRemoteAsync(directory, cancellationToken).ConfigureAwait(false);
        return ParseStatus(status.StdOut, hasRemote);
    }

    // --- staging -----------------------------------------------------------
    public Task<GitResult> StageAsync(string directory, string path, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["add", "--", path], cancellationToken);

    public Task<GitResult> StageAllAsync(string directory, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["add", "-A"], cancellationToken);

    public Task<GitResult> UnstageAsync(string directory, string path, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["restore", "--staged", "--", path], cancellationToken);

    // Discard working-tree changes. Untracked files are removed (clean); tracked
    // files are restored from the index/HEAD. Destructive — caller confirms.
    public Task<GitResult> DiscardAsync(string directory, string path, bool untracked, CancellationToken cancellationToken = default)
        => untracked
            ? RunAsync(directory, ["clean", "-f", "--", path], cancellationToken)
            : RunAsync(directory, ["restore", "--worktree", "--", path], cancellationToken);

    // --- diff --------------------------------------------------------------
    // Unified diff text for one path. Staged = index-vs-HEAD; otherwise
    // worktree-vs-index; untracked = the whole file as additions (via --no-index).
    public async Task<string> DiffTextAsync(
        string directory,
        string path,
        bool staged,
        bool untracked,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> args = untracked
            ? ["diff", "--no-index", "--", "/dev/null", path]
            : staged
                ? ["diff", "--cached", "--", path]
                : ["diff", "--", path];

        GitResult result = await RunAsync(directory, args, cancellationToken).ConfigureAwait(false);
        // --no-index exits 1 when the files differ; that is expected and StdOut is valid.
        if (!string.IsNullOrEmpty(result.StdOut))
        {
            return result.StdOut;
        }

        return result.Ok || untracked ? result.StdOut : result.StdErr;
    }

    // Raw content of a path at a git revision spec ("HEAD:foo.cs" = committed,
    // ":foo.cs" = staged index). Returns empty when the object does not exist
    // (e.g. a brand-new file has no HEAD version). Used to build a side-by-side diff.
    public async Task<string> ShowAsync(string directory, string spec, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["show", spec], cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.StdOut : string.Empty;
    }

    // --- commit / push / sync ---------------------------------------------
    public async Task<GitResult> CommitAsync(string directory, string message, bool stageAll, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitResult(-1, string.Empty, "Commit message must not be empty.");
        }

        if (stageAll)
        {
            GitResult stage = await StageAllAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!stage.Ok)
            {
                return stage;
            }
        }

        return await RunAsync(directory, ["commit", "-m", message], cancellationToken).ConfigureAwait(false);
    }

    public Task<GitResult> PushAsync(
        string directory,
        string? remote = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
        => !string.IsNullOrWhiteSpace(remote) && !string.IsNullOrWhiteSpace(branch)
            ? RunAsync(directory, ["push", "-u", remote, branch], cancellationToken)
            : RunAsync(directory, ["push"], cancellationToken);

    public Task<GitResult> FetchAsync(string directory, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["fetch", "--prune"], cancellationToken);

    public Task<GitResult> PullAsync(string directory, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["pull", "--ff-only"], cancellationToken);

    // --- branches ----------------------------------------------------------
    public async Task<IReadOnlyList<string>> ListBranchesAsync(string directory, CancellationToken cancellationToken = default)
    {
        GitResult result = await RunAsync(directory, ["branch", "--format=%(refname:short)"], cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    // Create a branch and switch to it.
    public Task<GitResult> CreateBranchAsync(string directory, string name, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["switch", "-c", name], cancellationToken);

    public Task<GitResult> SwitchBranchAsync(string directory, string name, CancellationToken cancellationToken = default)
        => RunAsync(directory, ["switch", name], cancellationToken);

    // --- log ---------------------------------------------------------------
    public async Task<IReadOnlyList<GitCommit>> LogAsync(string directory, int count = 20, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(count, 1, 200);
        GitResult result = await RunAsync(
            directory,
            ["log", $"-n{take}", $"--pretty=format:%h{Unit}%s{Unit}%an{Unit}%ar"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return [];
        }

        List<GitCommit> commits = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(Unit);
            if (parts.Length >= 4)
            {
                commits.Add(new GitCommit(parts[0], parts[1], parts[2], parts[3]));
            }
        }

        return commits;
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

            // "XY path" — X = index (staged) status, Y = work-tree (unstaged) status.
            if (line.Length >= 3)
            {
                char index = line[0];
                char workTree = line[1];
                string path = line[3..];
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path[(arrow + 4)..];
                }

                changes.Add(new GitChange(index, workTree, path.Trim('"')));
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

// One changed path in porcelain terms: Index = staged status, WorkTree = unstaged.
// A file can be both (e.g. "MM"): staged edit plus a further unstaged edit.
public sealed record GitChange(char Index, char WorkTree, string Path)
{
    public bool IsStaged => Index != ' ' && Index != '?';

    public bool IsUnstaged => WorkTree != ' ';

    public bool IsUntracked => Index == '?' && WorkTree == '?';

    // Single-letter label for a group view (staged shows Index; else WorkTree).
    public char StagedCode => Index == ' ' ? '?' : Index;

    public char UnstagedCode => IsUntracked ? 'U' : WorkTree;
}

// One row of `git log` for the history view.
public sealed record GitCommit(string Hash, string Subject, string Author, string RelativeDate);

// A snapshot of the watched repo's state for the operator's Git panel.
public sealed record GitStatus(
    bool IsRepository,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool HasRemote,
    IReadOnlyList<GitChange> Changes,
    bool GitAvailable = true,
    string? Error = null)
{
    public static GitStatus NotARepository { get; } = new(false, null, null, 0, 0, false, []);

    // git could not be launched at all (not installed / not on PATH).
    public static GitStatus Unavailable(string error) =>
        new(false, null, null, 0, 0, false, [], GitAvailable: false, Error: error);

    public IReadOnlyList<GitChange> Staged => Changes.Where(change => change.IsStaged).ToList();

    public IReadOnlyList<GitChange> Unstaged => Changes.Where(change => change.IsUnstaged).ToList();

    public bool HasStaged => Changes.Any(change => change.IsStaged);

    public bool HasChanges => Changes.Count > 0;
}
