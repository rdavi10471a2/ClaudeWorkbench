using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

// Operator Git page — a READ-ONLY review of what has been done, plus a small, clearly
// separated bar of operator-only write actions.
//
// Review (the focus): the uncommitted working-tree changes and the commit history are
// both read-only and click-through — selecting a working file or a file within a commit
// opens it in the shared DiffView (the same side-by-side surface the merge dialog uses,
// reused unchanged). Bringing the agent's work INTO source is the merge workflow's job,
// not this page's; this page is the rear-view mirror over the result.
//
// Writes (branch / commit / push / merge-to-main) are the operator's alone. They call
// GitWorkspaceService directly — never MCP — and the outward/irreversible ones confirm
// first. The agent has read-only git via MCP and cannot write the repo at all.
public partial class GitTab : IAsyncDisposable
{
    [Inject]
    private GitWorkspaceService Git { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    // Collapsible / resizable left review panel vs the diff on the right.
    private ElementReference gitBody;
    private ElementReference gitLeft;
    private ElementReference gitRight;
    private ElementReference gitSplitter;
    private IJSObjectReference? resizeModule;
    private bool leftCollapsed;

    private GitStatus? status;
    private IReadOnlyList<string> branches = [];
    private IReadOnlyList<GitCommit> history = [];
    private bool busy;
    private string? resultMessage;
    private bool resultIsError;

    // --- selection / diff state -------------------------------------------
    // Exactly one of these selections is active at a time; picking one clears the other.
    // A working-tree file (working-vs-HEAD diff)...
    private string? selectedWorkingPath;
    // ...or a file within a commit (parent-vs-commit diff).
    private string? selectedCommit;
    private string? selectedCommitPath;

    // The commit whose file list is expanded inline in the history.
    private string? expandedCommit;
    private IReadOnlyList<GitCommitFile> expandedFiles = [];

    // What the DiffView on the right is currently showing.
    private GitDiffContent? diffContent;
    private string diffTitle = string.Empty;
    private string diffOldLabel = string.Empty;
    private string diffNewLabel = string.Empty;

    // Inline write forms.
    private bool newBranchOpen;
    private string newBranchName = string.Empty;
    private bool commitOpen;
    private string commitMessage = string.Empty;
    // The commit body: pre-filled with the changed file list, editable, appended under the message.
    private string commitBody = string.Empty;

    private const string MainBranch = "main";

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The split layout only exists once a repository is shown.
        if (status is null || !status.IsRepository || leftCollapsed)
        {
            return;
        }

        resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");

        // The splitter element itself carries the "already attached" guard (dataset flag),
        // so collapsing and restoring re-attaches only when the element is genuinely new.
        await resizeModule.InvokeVoidAsync("attachGitSplitter", gitBody, gitLeft, gitRight, gitSplitter);
    }

    private void ToggleLeft() => leftCollapsed = !leftCollapsed;

    private bool OnMain => string.Equals(status?.Branch, MainBranch, StringComparison.Ordinal);

    // True when a history commit's diff is showing (vs a working-tree file). The operator is looking
    // at the past, which is read-only, so the toolbar disables the write actions (commit / push /
    // merge / branch / fetch / pull) and shows a "viewing history" cue. Refresh and Hide Panel stay
    // live; clicking a working-tree file (or Refresh) returns to the actionable current state.
    private bool ViewingHistory => selectedCommit is not null;

    private async Task ReloadAsync()
    {
        busy = true;
        StateHasChanged();
        try
        {
            status = await Git.GetStatusAsync();
            if (status.IsRepository)
            {
                branches = await Git.ListBranchesAsync();
                history = await Git.LogAsync(30);

                // Keep an open working-tree diff current, or drop it if the file is no
                // longer changed. Commit diffs are immutable, so they need no refresh.
                if (selectedWorkingPath is not null)
                {
                    GitChange? still = status.Changes.FirstOrDefault(change =>
                        string.Equals(change.Path, selectedWorkingPath, StringComparison.Ordinal));
                    if (still is null)
                    {
                        ClearDiff();
                    }
                    else
                    {
                        diffContent = await Git.GetDiffContentAsync(still.Path, staged: false, still.IsUntracked);
                    }
                }

                // Drop an expanded commit that fell out of the reloaded history window.
                if (expandedCommit is not null && !history.Any(c => string.Equals(c.Hash, expandedCommit, StringComparison.Ordinal)))
                {
                    expandedCommit = null;
                    expandedFiles = [];
                }
            }
            else
            {
                branches = [];
                history = [];
                ClearDiff();
            }
        }
        finally
        {
            busy = false;
            StateHasChanged();
        }
    }

    // --- read-only review selection ---------------------------------------
    private async Task SelectWorkingFileAsync(GitChange change)
    {
        selectedWorkingPath = change.Path;
        selectedCommit = null;
        selectedCommitPath = null;
        diffContent = await Git.GetDiffContentAsync(change.Path, staged: false, change.IsUntracked);
        diffTitle = change.Path;
        diffOldLabel = "HEAD";
        diffNewLabel = "Working tree";
        StateHasChanged();
    }

    private async Task ToggleCommitAsync(GitCommit commit)
    {
        if (string.Equals(expandedCommit, commit.Hash, StringComparison.Ordinal))
        {
            expandedCommit = null;
            expandedFiles = [];
            return;
        }

        expandedCommit = commit.Hash;
        expandedFiles = await Git.CommitFilesAsync(commit.Hash);
        StateHasChanged();
    }

    private async Task SelectCommitFileAsync(GitCommit commit, GitCommitFile file)
    {
        selectedCommit = commit.Hash;
        selectedCommitPath = file.Path;
        selectedWorkingPath = null;
        diffContent = await Git.GetCommitDiffContentAsync(commit.Hash, file.Path);
        diffTitle = file.Path;
        diffOldLabel = $"{Short(commit.Hash)}~1";
        diffNewLabel = Short(commit.Hash);
        StateHasChanged();
    }

    private bool IsWorkingSelected(GitChange change) =>
        selectedWorkingPath is not null && string.Equals(selectedWorkingPath, change.Path, StringComparison.Ordinal);

    private bool IsCommitFileSelected(GitCommit commit, GitCommitFile file) =>
        string.Equals(selectedCommit, commit.Hash, StringComparison.Ordinal)
        && string.Equals(selectedCommitPath, file.Path, StringComparison.Ordinal);

    private void ClearDiff()
    {
        selectedWorkingPath = null;
        selectedCommit = null;
        selectedCommitPath = null;
        diffContent = null;
    }

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;

    // --- operator write actions (direct GitWorkspaceService, never MCP) ----
    private async Task OnBranchSelected(ChangeEventArgs args)
    {
        string? target = args.Value?.ToString();
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, status?.Branch, StringComparison.Ordinal))
        {
            return;
        }

        if (!await ConfirmAsync($"Switch to branch '{target}'? This changes the working-tree files."))
        {
            return; // Re-render restores the select to the current branch.
        }

        await RunAsync(() => Git.SwitchBranchAsync(target), $"Switched to {target}.");
    }

    private async Task CreateBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(newBranchName))
        {
            return;
        }

        string name = newBranchName.Trim();
        bool created = await RunAsync(() => Git.CreateBranchAsync(name), $"Created branch {name}.");
        if (created)
        {
            newBranchName = string.Empty;
            newBranchOpen = false;
        }
    }

    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            return;
        }

        // The message box is the subject; the (optional) file-list box becomes the body, one blank
        // line below it -- the standard git subject/body split.
        string message = commitMessage.Trim();
        string body = commitBody.Trim();
        if (body.Length > 0)
        {
            message = $"{message}\n\n{body}";
        }

        bool committed = await RunAsync(() => Git.CommitAsync(message), "Committed.");
        if (committed)
        {
            commitMessage = string.Empty;
            commitBody = string.Empty;
            commitOpen = false;
            ClearDiff();
        }
    }

    private void OpenCommit()
    {
        commitOpen = true;
        if (status is null || !status.HasChanges)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            commitMessage = GitWorkspaceService.DraftCommitMessage(status);
        }

        if (string.IsNullOrWhiteSpace(commitBody))
        {
            commitBody = GitWorkspaceService.DraftFileList(status);
        }
    }

    private async Task PushAsync()
    {
        if (!await ConfirmAsync("Push the current branch's commits to the remote?"))
        {
            return;
        }

        await RunAsync(() => Git.PushAsync(), "Pushed to the remote.");
    }

    private Task FetchAsync() => RunAsync(() => Git.FetchAsync(), "Fetched from the remote.");

    private async Task PullAsync()
    {
        if (!await ConfirmAsync("Pull (fast-forward only) from the remote?"))
        {
            return;
        }

        await RunAsync(() => Git.PullAsync(), "Pulled from the remote.");
    }

    private async Task MergeToMainAsync()
    {
        string current = status?.Branch ?? "this branch";
        if (!await ConfirmAsync($"Merge '{current}' into {MainBranch}? A clean working tree is required; on conflict the merge is aborted and you stay on '{current}'."))
        {
            return;
        }

        await RunAsync(() => Git.MergeCurrentIntoMainAsync(MainBranch), $"Merged into {MainBranch}.");
    }

    private Task InitAsync() => RunAsync(() => Git.InitAsync(), "Initialized git repository.");

    private async Task<bool> ConfirmAsync(string message) =>
        await JS.InvokeAsync<bool>("confirm", message);

    private async Task<bool> RunAsync(Func<Task<GitResult>> action, string successMessage)
    {
        busy = true;
        resultMessage = null;
        StateHasChanged();

        bool ok;
        try
        {
            GitResult result = await action();
            ok = result.Ok;
            resultIsError = !ok;
            resultMessage = ok
                ? Combine(successMessage, result.StdOut)
                : FirstNonEmpty(result.StdErr, result.StdOut, "git command failed.");
        }
        catch (Exception exception)
        {
            ok = false;
            resultIsError = true;
            resultMessage = exception.Message;
        }
        finally
        {
            await ReloadAsync();
        }

        return ok;
    }

    private static string Combine(string message, string detail)
    {
        string clean = CleanDetail(detail);
        return clean.Length == 0 ? message : $"{message}\n{clean}";
    }

    // Git tails a commit's output with a "create mode 100644 <path>" (or delete/rename/copy/mode
    // change) line per file, which turns a many-file commit into a wall of text. Keep the useful
    // summary -- the "[branch hash] subject" line and the "N files changed" shortstat -- and drop the
    // per-file mode noise. Rendered with white-space:pre-wrap so the remaining lines actually break.
    private static string CleanDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        IEnumerable<string> lines = detail
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0
                && !line.StartsWith("create mode ", StringComparison.Ordinal)
                && !line.StartsWith("delete mode ", StringComparison.Ordinal)
                && !line.StartsWith("rename ", StringComparison.Ordinal)
                && !line.StartsWith("copy ", StringComparison.Ordinal)
                && !line.StartsWith("mode change ", StringComparison.Ordinal));

        return string.Join("\n", lines);
    }

    private static string FirstNonEmpty(params string[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (resizeModule is not null)
        {
            try
            {
                await resizeModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
