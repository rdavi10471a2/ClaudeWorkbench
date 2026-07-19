using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

// Operator Git panel for the watched solution (VS Code-style source control): branch
// switch/create, fetch/pull/push, staged/unstaged groups with per-file stage/unstage/
// discard, a unified-diff viewer, and recent history. Talks only to
// GitWorkspaceService; the same backend the gated GitMcpTools use.
public partial class GitTab : IAsyncDisposable
{
    [Inject]
    private GitWorkspaceService Git { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    // Collapsible / resizable left panel (changes + commit) vs the diff on the right.
    private ElementReference gitBody;
    private ElementReference gitLeft;
    private ElementReference gitRight;
    private ElementReference gitSplitter;
    private IJSObjectReference? resizeModule;
    private bool leftCollapsed;
    private bool splitterAttached;

    private GitStatus? status;
    private IReadOnlyList<string> branches = [];
    private IReadOnlyList<GitCommit> history = [];
    private string commitMessage = string.Empty;
    private bool busy;
    private string? resultMessage;
    private bool resultIsError;
    private bool showHistory;

    // Diff viewer state.
    private string? selectedPath;
    private bool selectedStaged;
    private GitDiffContent? diffContent;

    // Inline new-branch form.
    private bool newBranchOpen;
    private string newBranchName = string.Empty;

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The split layout only exists once a repository is shown.
        if (status is null || !status.IsRepository || leftCollapsed)
        {
            return;
        }

        resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
        if (!splitterAttached)
        {
            await resizeModule.InvokeVoidAsync("attachGitSplitter", gitBody, gitLeft, gitRight, gitSplitter);
            splitterAttached = true;
        }
    }

    private void ToggleLeft()
    {
        leftCollapsed = !leftCollapsed;
        splitterAttached = false; // re-attach the splitter when the panel returns
    }

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
                history = await Git.LogAsync(15);
                if (string.IsNullOrWhiteSpace(commitMessage) && status.HasChanges)
                {
                    commitMessage = GitWorkspaceService.DraftCommitMessage(status);
                }

                // Keep an open diff current, or drop it if the file is no longer changed.
                if (selectedPath is not null)
                {
                    GitChange? still = status.Changes.FirstOrDefault(change =>
                        string.Equals(change.Path, selectedPath, StringComparison.Ordinal));
                    if (still is null)
                    {
                        selectedPath = null;
                        diffContent = null;
                    }
                    else
                    {
                        diffContent = await Git.GetDiffContentAsync(still.Path, selectedStaged, still.IsUntracked);
                    }
                }
            }
            else
            {
                branches = [];
                history = [];
            }
        }
        finally
        {
            busy = false;
            StateHasChanged();
        }
    }

    private async Task SelectFileAsync(GitChange change, bool staged)
    {
        selectedPath = change.Path;
        selectedStaged = staged;
        diffContent = await Git.GetDiffContentAsync(change.Path, staged, change.IsUntracked);
        StateHasChanged();
    }

    private Task StageFile(GitChange change) => RunAsync(() => Git.StageAsync(change.Path), $"Staged {change.Path}.");

    private Task UnstageFile(GitChange change) => RunAsync(() => Git.UnstageAsync(change.Path), $"Unstaged {change.Path}.");

    private async Task DiscardFile(GitChange change)
    {
        bool confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Discard changes to {change.Path}? This cannot be undone.");
        if (!confirmed)
        {
            return;
        }

        await RunAsync(() => Git.DiscardAsync(change.Path, change.IsUntracked), $"Discarded {change.Path}.");
        if (string.Equals(selectedPath, change.Path, StringComparison.Ordinal))
        {
            selectedPath = null;
            diffContent = null;
        }
    }

    private Task StageAllAsync() => RunAsync(() => Git.StageAllAsync(), "Staged all changes.");

    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            return;
        }

        string message = commitMessage;
        bool committed = await RunAsync(() => Git.CommitAsync(message), "Committed.");
        if (committed)
        {
            commitMessage = string.Empty;
            selectedPath = null;
            diffContent = null;
        }
    }

    private Task PushAsync() => RunAsync(() => Git.PushAsync(), "Pushed to the remote.");

    private Task FetchAsync() => RunAsync(() => Git.FetchAsync(), "Fetched from the remote.");

    private Task PullAsync() => RunAsync(() => Git.PullAsync(), "Pulled from the remote.");

    private Task InitAsync() => RunAsync(() => Git.InitAsync(), "Initialized git repository.");

    private async Task OnBranchSelected(ChangeEventArgs args)
    {
        string? target = args.Value?.ToString();
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, status?.Branch, StringComparison.Ordinal))
        {
            return;
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
        => string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}";

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
