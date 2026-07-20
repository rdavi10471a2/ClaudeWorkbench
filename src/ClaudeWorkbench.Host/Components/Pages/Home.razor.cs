using AIMonitor.McpServer;
using ClaudeWorkbench.Host.Components.Dialogs;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;

namespace ClaudeWorkbench.Host.Components.Pages;

public partial class Home : IDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    [Inject]
    private WorkspaceManager Workspace { get; set; } = default!;

    [Inject]
    private Services.IndexRebuildStatus IndexStatus { get; set; } = default!;

    [Inject]
    private IReviewWorkflow Review { get; set; } = default!;

    [Inject]
    private DialogService Dialogs { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    // Browser tab title: solution name first so it's readable in a narrow tab and each
    // launcher window is distinguishable.
    private string PageTitleText => Workspace.HasWorkspace && !string.IsNullOrWhiteSpace(Workspace.WatchedSolutionPath)
        ? $"{Path.GetFileNameWithoutExtension(Workspace.WatchedSolutionPath)} — ClaudeWorkbench"
        : "ClaudeWorkbench";

    // Command-bar auth cues. The Claude dot carries the sidecar-down message too,
    // because when the sidecar is down its login state is genuinely unknowable — so
    // the root cause ("Sidecar unavailable") is the honest thing to show there.
    private AuthCue ClaudeCue => !Session.Status.Connected
        ? new("down", "Sidecar unavailable", "The Claude sidecar is not reachable — no turns can run until it is back.")
        : Session.Auth.Claude switch
        {
            true => new("up", "Claude available", "Signed in to the Claude CLI."),
            false => new("warn", "Claude signed out", "The Claude CLI is signed out. Use the launcher's Claude button to sign in."),
            _ => new("unknown", "Claude — checking", "Checking Claude login state..."),
        };

    private AuthCue GitHubCue => Session.Auth.GitHub switch
    {
        true => new("up", "GitHub signed in", "Signed in to the GitHub CLI (gh)."),
        false => new("warn", "GitHub signed out", "The GitHub CLI (gh) is signed out. Use the launcher's GitHub button to sign in."),
        _ => new("unknown", "GitHub — checking", "Checking GitHub login state..."),
    };

    private readonly record struct AuthCue(string Css, string Label, string Title);

    private bool settingsOpen;
    private bool workspacePickerOpen;
    private bool reviewDialogOpen;
    private bool aboutOpen;
    private bool helpOpen;
    private IJSObjectReference? unloadModule;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
        Workspace.Changed += OnChanged;
        IndexStatus.Changed += OnChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            unloadModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
            // When a launcher owns this instance, the tab close is intentional (it tears the
            // backend down), so the "leaving will reset your session" guard must NOT fire —
            // it would prompt on close and delay the circuit drop that stops the backend.
            bool launcherOwned = string.Equals(
                Environment.GetEnvironmentVariable("CWB_EXIT_WITH_BROWSER"), "1", StringComparison.Ordinal);
            if (!launcherOwned)
            {
                await unloadModule.InvokeVoidAsync(
                    "setBeforeUnloadGuard",
                    true,
                    "Leaving or refreshing will reset the current Claude Workbench session.");
            }
        }
    }

    private void OnChanged()
    {
        InvokeAsync(async () =>
        {
            StateHasChanged();
            await MaybeOpenReviewAsync();
        });
    }

    // When the agent stages one or more candidates, open the faithful session-flow
    // merge review (resizable Radzen dialog) for that edit session. The guard keeps
    // a single dialog open; it reopens for the next session on the following change.
    private async Task MaybeOpenReviewAsync()
    {
        if (reviewDialogOpen)
        {
            return;
        }

        // Wait until the agent's turn has finished before opening the review, so
        // the whole edit session is staged and the dialog can advance through every
        // file without closing/reopening as each candidate lands mid-turn.
        if (Session.Status.Working)
        {
            return;
        }

        IReadOnlyList<ReviewQueueItem> pending;
        try
        {
            pending = Review.ListPending();
        }
        catch (Exception)
        {
            return;
        }

        if (pending.Count == 0)
        {
            return;
        }

        reviewDialogOpen = true;
        try
        {
            await Dialogs.OpenAsync<MergeReviewDialog>(
                "Merge Review",
                new Dictionary<string, object?>
                {
                    [nameof(MergeReviewDialog.SessionId)] = pending[0].SessionId,
                    [nameof(MergeReviewDialog.AutoCloseWhenComplete)] = true,
                },
                new DialogOptions
                {
                    Width = "88vw",
                    Height = "88vh",
                    CloseDialogOnEsc = false,
                    CloseDialogOnOverlayClick = false,
                    Resizable = true,
                    Draggable = true,
                    ShowClose = false,
                });
        }
        finally
        {
            reviewDialogOpen = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void CloseWorkspacePicker()
    {
        workspacePickerOpen = false;
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
        Workspace.Changed -= OnChanged;
        IndexStatus.Changed -= OnChanged;
    }
}
