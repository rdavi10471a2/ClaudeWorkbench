using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ClaudeWorkbench.Host.Services;

// Read-only git surface for the agent. The agent NEVER runs a shell and NEVER writes
// git: it calls these specific read verbs (status/diff/log/branches), each a fixed
// argv to the git executable, and they all auto-allow at the sidecar gate. Every git
// WRITE (commit, push, branch, merge) is deliberately absent here — those live only in
// the operator's Git page (direct GitWorkspaceService calls, gated by the human at the
// UI). Keeping writes out of MCP entirely means auto-approve has nothing to bypass:
// the agent can *see* the repo to reason about it, but only the operator changes it.
// Backed by the same GitWorkspaceService as the operator Git panel.
[McpServerToolType]
public sealed class GitMcpTools
{
    private const int MaxDiffChars = 8000;
    private readonly GitWorkspaceService git;

    public GitMcpTools(GitWorkspaceService git)
    {
        this.git = git;
    }

    [McpServerTool]
    [Description("Show git status for the watched solution: current branch, upstream, ahead/behind counts, whether a remote exists, and the staged/unstaged/untracked changed files. Read-only.")]
    public async Task<GitStatusResult> GitStatus()
    {
        GitStatus status = await git.GetStatusAsync().ConfigureAwait(false);
        if (!status.GitAvailable)
        {
            return GitStatusResult.Unavailable(status.Error ?? "git is not available.");
        }

        if (!status.IsRepository)
        {
            return GitStatusResult.NotARepository();
        }

        List<GitFileChange> changes = status.Changes
            .Select(change => new GitFileChange(
                change.Path,
                $"{change.Index}{change.WorkTree}",
                change.IsStaged,
                change.IsUnstaged,
                change.IsUntracked))
            .ToList();

        return new GitStatusResult(
            true,
            true,
            status.Branch,
            status.Upstream,
            status.Ahead,
            status.Behind,
            status.HasRemote,
            changes,
            null);
    }

    [McpServerTool]
    [Description("Show the unified diff for one changed file in the watched solution. Pass the path exactly as reported by git_status. Read-only.")]
    public async Task<string> GitDiff(
        [Description("File path as shown by git_status (repo-relative).")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "A file path is required.";
        }

        GitStatus status = await git.GetStatusAsync().ConfigureAwait(false);
        GitChange? change = status.Changes.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(candidate.Path), path, StringComparison.OrdinalIgnoreCase));

        bool untracked = change?.IsUntracked ?? false;
        bool stagedOnly = change is { IsStaged: true, IsUnstaged: false };
        string diff = await git.DiffAsync(change?.Path ?? path, stagedOnly, untracked).ConfigureAwait(false);
        if (string.IsNullOrEmpty(diff))
        {
            return "(no diff — file is unchanged or not found in the working tree)";
        }

        return diff.Length > MaxDiffChars
            ? diff[..MaxDiffChars] + $"\n… (diff truncated at {MaxDiffChars} characters)"
            : diff;
    }

    [McpServerTool]
    [Description("List recent commits on the current branch (hash, subject, author, relative date), newest first. Read-only.")]
    public async Task<IReadOnlyList<GitCommitInfo>> GitLog(
        [Description("How many commits to return (default 20, max 200).")] int count = 20)
    {
        IReadOnlyList<GitCommit> commits = await git.LogAsync(count).ConfigureAwait(false);
        return commits
            .Select(commit => new GitCommitInfo(commit.Hash, commit.Subject, commit.Author, commit.RelativeDate))
            .ToList();
    }

    [McpServerTool]
    [Description("List local branches in the watched solution's repository. Read-only.")]
    public Task<IReadOnlyList<string>> GitListBranches() => git.ListBranchesAsync();

    // No git write tools (commit/push/branch/merge) are exposed to the agent by design.
    // Those actions belong to the operator alone and live in the Git page's UI, which
    // calls GitWorkspaceService directly. See the class remarks above.
}

public sealed record GitFileChange(string Path, string Status, bool Staged, bool Unstaged, bool Untracked);

public sealed record GitCommitInfo(string Hash, string Subject, string Author, string RelativeDate);

public sealed record GitStatusResult(
    bool IsRepository,
    bool GitAvailable,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool HasRemote,
    IReadOnlyList<GitFileChange> Changes,
    string? Message)
{
    public static GitStatusResult Unavailable(string message) =>
        new(false, false, null, null, 0, 0, false, [], message);

    public static GitStatusResult NotARepository() =>
        new(false, true, null, null, 0, 0, false, [], "The watched solution folder is not a git repository.");
}
