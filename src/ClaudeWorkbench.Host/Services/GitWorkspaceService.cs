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
