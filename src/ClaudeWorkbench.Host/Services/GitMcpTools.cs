using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ClaudeWorkbench.Host.Services;

// Governed git surface for the agent. The agent NEVER runs a shell; it calls these
// specific verbs, each a fixed argv to the git executable. Reads (status/diff/log/
// branches) auto-allow at the sidecar gate; the mutations (commit/push/create_branch/
// switch_branch) pause at the operator gate for approval — so "type a prompt to do
// git" works while every outward/irreversible action still needs the operator's OK.
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

    [McpServerTool]
    [Description("Commit the watched solution's changes with a message. Commits the staged set if anything is staged, otherwise stages all changes and commits them. GOVERNED: this pauses at the operator's approval gate before running.")]
    public async Task<GitActionResult> GitCommit(
        [Description("The commit message.")] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitActionResult(false, "A commit message is required.");
        }

        return ToActionResult(await git.CommitAsync(message).ConfigureAwait(false), "Committed.");
    }

    [McpServerTool]
    [Description("Push the current branch's commits to the remote (sets upstream on a branch's first push). GOVERNED: this pauses at the operator's approval gate before running. Nothing reaches the remote without operator approval.")]
    public async Task<GitActionResult> GitPush()
        => ToActionResult(await git.PushAsync().ConfigureAwait(false), "Pushed to the remote.");

    [McpServerTool]
    [Description("Create a new branch in the watched solution and switch to it. GOVERNED: this pauses at the operator's approval gate before running.")]
    public async Task<GitActionResult> GitCreateBranch(
        [Description("The new branch name.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GitActionResult(false, "A branch name is required.");
        }

        return ToActionResult(await git.CreateBranchAsync(name).ConfigureAwait(false), $"Created and switched to branch '{name}'.");
    }

    [McpServerTool]
    [Description("Switch the watched solution to an existing branch. GOVERNED: this pauses at the operator's approval gate before running (it changes the working-tree files).")]
    public async Task<GitActionResult> GitSwitchBranch(
        [Description("The branch to switch to.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GitActionResult(false, "A branch name is required.");
        }

        return ToActionResult(await git.SwitchBranchAsync(name).ConfigureAwait(false), $"Switched to branch '{name}'.");
    }

    private static GitActionResult ToActionResult(GitResult result, string successMessage)
    {
        if (result.Ok)
        {
            return new GitActionResult(true, string.IsNullOrWhiteSpace(result.StdOut) ? successMessage : $"{successMessage} {result.StdOut}");
        }

        string detail = !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr : result.StdOut;
        return new GitActionResult(false, string.IsNullOrWhiteSpace(detail) ? "git command failed." : detail);
    }
}

public sealed record GitFileChange(string Path, string Status, bool Staged, bool Unstaged, bool Untracked);

public sealed record GitCommitInfo(string Hash, string Subject, string Author, string RelativeDate);

public sealed record GitActionResult(bool Success, string Message);

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
