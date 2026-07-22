using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Operator-facing git bound to the currently watched solution. Resolves the repo
// root (so porcelain paths and diffs line up even when the .sln sits in a subfolder),
// wraps GitService, and drafts commit messages. Shared (singleton) by BOTH surfaces:
// the operator Git panel and the gated GitMcpTools the agent calls. Stateless.
public sealed class GitWorkspaceService
{
    private readonly GitService git;
    private readonly WorkspaceManager workspace;

    public GitWorkspaceService(GitService git, WorkspaceManager workspace)
    {
        this.git = git;
        this.workspace = workspace;
    }

    public bool HasWorkspace => workspace.HasWorkspace;

    // The watched solution's folder — where a not-yet-initialized repo would be created.
    public string? WatchedDirectory
    {
        get
        {
            string? solution = workspace.WatchedSolutionPath;
            return string.IsNullOrEmpty(solution) ? null : Path.GetDirectoryName(solution);
        }
    }

    public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        return directory is null
            ? GitStatus.NotARepository
            : await git.GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    // Prompt-to-add path: initialize the watched folder as a repo.
    public Task<GitResult> InitAsync(CancellationToken cancellationToken = default)
        => WithWatchedDirectory(directory => git.InitAsync(directory, cancellationToken: cancellationToken));

    // Unified diff text (for the agent's git_diff MCP tool — compact and model-friendly).
    public Task<string> DiffAsync(string path, bool staged, bool untracked, CancellationToken cancellationToken = default)
        => WithRepoDirectory(
            directory => git.DiffTextAsync(directory, path, staged, untracked, cancellationToken),
            string.Empty);

    // Before/after file contents for the shared side-by-side DiffView, both normalized
    // to \n so line-ending differences do not render as spurious changes.
    public async Task<GitDiffContent> GetDiffContentAsync(string path, bool staged, bool untracked, CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return GitDiffContent.Empty;
        }

        string oldText;
        string newText;
        if (untracked)
        {
            oldText = string.Empty;
            newText = ReadWorking(directory, path);
        }
        else if (staged)
        {
            // Staged file: HEAD (committed) on the left, the staged index copy on the right.
            oldText = await git.ShowAsync(directory, $"HEAD:{path}", cancellationToken).ConfigureAwait(false);
            newText = await git.ShowAsync(directory, $":{path}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Unstaged file: index (or HEAD) on the left, the working-tree copy on the right.
            string indexText = await git.ShowAsync(directory, $":{path}", cancellationToken).ConfigureAwait(false);
            oldText = string.IsNullOrEmpty(indexText)
                ? await git.ShowAsync(directory, $"HEAD:{path}", cancellationToken).ConfigureAwait(false)
                : indexText;
            newText = ReadWorking(directory, path);
        }

        return new GitDiffContent(Normalize(oldText), Normalize(newText));
    }

    public Task<GitResult> StageAsync(string path, CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.StageAsync(directory, path, cancellationToken));

    public Task<GitResult> StageAllAsync(CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.StageAllAsync(directory, cancellationToken));

    public Task<GitResult> UnstageAsync(string path, CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.UnstageAsync(directory, path, cancellationToken));

    public Task<GitResult> DiscardAsync(string path, bool untracked, CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.DiscardAsync(directory, path, untracked, cancellationToken));

    // Operator-batched commit: commit the staged set if anything is staged, otherwise
    // stage everything and commit (the one-click "commit all" path).
    public async Task<GitResult> CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return NoWorkspace;
        }

        GitStatus status = await git.GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        bool stageAll = !status.HasStaged;
        return await git.CommitAsync(directory, message, stageAll, cancellationToken).ConfigureAwait(false);
    }

    // Explicit push. Sets the upstream on a branch's first push.
    public async Task<GitResult> PushAsync(CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return NoWorkspace;
        }

        GitStatus status = await git.GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!status.HasRemote)
        {
            return new GitResult(-1, string.Empty, "No remote is configured. Add one (git remote add origin <url>) before pushing.");
        }

        if (status.Upstream is null && !string.IsNullOrEmpty(status.Branch))
        {
            return await git.PushAsync(directory, "origin", status.Branch, cancellationToken).ConfigureAwait(false);
        }

        return await git.PushAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<GitResult> FetchAsync(CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.FetchAsync(directory, cancellationToken));

    public Task<GitResult> PullAsync(CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.PullAsync(directory, cancellationToken));

    public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default)
        => WithRepoDirectory(directory => git.ListBranchesAsync(directory, cancellationToken), []);

    public Task<GitResult> CreateBranchAsync(string name, CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.CreateBranchAsync(directory, name, cancellationToken));

    public Task<GitResult> SwitchBranchAsync(string name, CancellationToken cancellationToken = default)
        => WithRepoResult(directory => git.SwitchBranchAsync(directory, name, cancellationToken));

    public Task<IReadOnlyList<GitCommit>> LogAsync(int count = 20, CancellationToken cancellationToken = default)
        => WithRepoDirectory(directory => git.LogAsync(directory, count, cancellationToken), []);

    // --- read-only history review -----------------------------------------
    // The files a single commit changed, for the click-through history view.
    public Task<IReadOnlyList<GitCommitFile>> CommitFilesAsync(string hash, CancellationToken cancellationToken = default)
        => WithRepoDirectory<IReadOnlyList<GitCommitFile>>(
            directory => git.CommitFilesAsync(directory, hash, cancellationToken),
            []);

    // Before/after contents of one file as changed by a commit, for the side-by-side
    // DiffView: the parent revision on the left, the commit's revision on the right.
    // ShowAsync yields empty for a missing side, so an added file has an empty left, a
    // deleted file an empty right, and a root-commit file (no parent) an empty left.
    public async Task<GitDiffContent> GetCommitDiffContentAsync(string hash, string path, CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return GitDiffContent.Empty;
        }

        string oldText = await git.ShowAsync(directory, $"{hash}^:{path}", cancellationToken).ConfigureAwait(false);
        string newText = await git.ShowAsync(directory, $"{hash}:{path}", cancellationToken).ConfigureAwait(false);
        return new GitDiffContent(Normalize(oldText), Normalize(newText));
    }

    // --- merge back to main (operator-only, gated in the UI) ---------------
    // Integrate the current branch into main. Guarded: refuses on a dirty tree (so a
    // half-done edit is never swept into a merge), uses --no-ff for an explicit merge
    // commit, and on conflict aborts and returns to the feature branch rather than
    // leaving the tree mid-merge. Ends on main when it succeeds.
    public async Task<GitResult> MergeCurrentIntoMainAsync(string mainBranch = "main", CancellationToken cancellationToken = default)
    {
        string? directory = await ResolveDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return NoWorkspace;
        }

        GitStatus status = await git.GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!status.IsRepository)
        {
            return new GitResult(-1, string.Empty, "The watched solution folder is not a git repository.");
        }

        string? current = status.Branch;
        if (string.IsNullOrEmpty(current))
        {
            return new GitResult(-1, string.Empty, "No current branch to merge.");
        }

        if (string.Equals(current, mainBranch, StringComparison.Ordinal))
        {
            return new GitResult(-1, string.Empty, $"Already on {mainBranch}. Switch to the feature branch you want to merge in first.");
        }

        if (status.HasChanges)
        {
            return new GitResult(-1, string.Empty, "The working tree has uncommitted changes. Commit or discard them before merging.");
        }

        IReadOnlyList<string> branches = await git.ListBranchesAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!branches.Contains(mainBranch))
        {
            return new GitResult(-1, string.Empty, $"No '{mainBranch}' branch exists to merge into.");
        }

        GitResult switchMain = await git.SwitchBranchAsync(directory, mainBranch, cancellationToken).ConfigureAwait(false);
        if (!switchMain.Ok)
        {
            return switchMain;
        }

        GitResult merge = await git.MergeNoFfAsync(directory, current, cancellationToken).ConfigureAwait(false);
        if (merge.Ok)
        {
            return new GitResult(0, merge.StdOut, $"Merged '{current}' into {mainBranch}.");
        }

        // Conflict (or other failure): unwind so the operator is never left mid-merge.
        await git.MergeAbortAsync(directory, cancellationToken).ConfigureAwait(false);
        await git.SwitchBranchAsync(directory, current, cancellationToken).ConfigureAwait(false);
        string detail = !string.IsNullOrWhiteSpace(merge.StdErr) ? merge.StdErr : merge.StdOut;
        return new GitResult(
            merge.ExitCode,
            merge.StdOut,
            $"Merge of '{current}' into {mainBranch} failed and was aborted — you are back on '{current}'. {detail}".Trim());
    }

    // A first-draft commit message from the working-tree changes; the operator (or the
    // agent, via the MCP tool) edits it before committing.
    public static string DraftCommitMessage(GitStatus status)
    {
        if (status.Changes.Count == 0)
        {
            return string.Empty;
        }

        List<string> names = status.Changes
            .Select(change => Path.GetFileName(change.Path.TrimEnd('/', '\\')))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count switch
        {
            0 => $"Update {status.Changes.Count} path(s)",
            1 => $"Update {names[0]}",
            <= 3 => $"Update {string.Join(", ", names)}",
            _ => $"Update {names.Count} files"
        };
    }

    // The changed paths, one per line, for the commit body -- so the message itself records exactly
    // which files moved. Mirrors the working-tree list the Git panel shows (folders collapsed the way
    // git's porcelain reports them).
    public static string DraftFileList(GitStatus status)
        => string.Join("\n", status.Changes.Select(change => change.Path));

    // Prefer the repository root as the working directory so porcelain paths and diff
    // targets are consistent; fall back to the watched folder (e.g. before init).
    private async Task<string?> ResolveDirectoryAsync(CancellationToken cancellationToken)
    {
        string? watched = WatchedDirectory;
        if (watched is null)
        {
            return null;
        }

        string? root = await git.GetRepositoryRootAsync(watched, cancellationToken).ConfigureAwait(false);
        return root ?? watched;
    }

    private async Task<GitResult> WithRepoResult(Func<string, Task<GitResult>> action)
    {
        string? directory = await ResolveDirectoryAsync(CancellationToken.None).ConfigureAwait(false);
        return directory is null ? NoWorkspace : await action(directory).ConfigureAwait(false);
    }

    private async Task<T> WithRepoDirectory<T>(Func<string, Task<T>> action, T fallback)
    {
        string? directory = await ResolveDirectoryAsync(CancellationToken.None).ConfigureAwait(false);
        return directory is null ? fallback : await action(directory).ConfigureAwait(false);
    }

    private async Task<GitResult> WithWatchedDirectory(Func<string, Task<GitResult>> action)
    {
        string? directory = WatchedDirectory;
        return directory is null ? NoWorkspace : await action(directory).ConfigureAwait(false);
    }

    private static string ReadWorking(string directory, string path)
    {
        try
        {
            string full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static GitResult NoWorkspace => new(-1, string.Empty, "No watched solution is selected.");
}

// Before/after contents of one file for the side-by-side DiffView.
public sealed record GitDiffContent(string OldText, string NewText)
{
    public static GitDiffContent Empty { get; } = new(string.Empty, string.Empty);
}
