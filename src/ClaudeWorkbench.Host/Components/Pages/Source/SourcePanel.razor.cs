using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Components.Dialogs;
using ClaudeWorkbench.Host.Source;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace ClaudeWorkbench.Host.Components.Pages.Source;

// Thin view over the singleton SourceWorkspace: renders its retained state and
// forwards events. State lives in the service, so it survives tab switches,
// component re-creation, and browser refresh within a host session.
public partial class SourcePanel : IDisposable
{
    [Inject]
    private ClaudeWorkbench.Host.Source.SourceWorkspace Workspace { get; set; } = default!;

    [Inject]
    private DialogService Dialogs { get; set; } = default!;

    [Inject]
    private NotificationService Notifications { get; set; } = default!;

    protected override void OnInitialized()
    {
        Workspace.Changed += OnChanged;
        Workspace.EnsureLoaded();
    }

    // Open the Add-project popup. On success the dialog has already scaffolded + reindexed (which fires
    // WorkspaceManager.Changed -> SourceWorkspace refresh, so the tree updates itself); we just toast.
    private async Task OpenAddProjectAsync()
    {
        object? result = await Dialogs.OpenAsync<NewProjectDialog>(
            "Add a project",
            options: new DialogOptions { Width = "560px", Resizable = false, Draggable = false });

        if (result is NewProjectDialog.Created created)
        {
            Notifications.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Success,
                Summary = "Project added",
                Detail = created.Message,
                Duration = 5000,
            });
        }
    }

    // Operator Build — real output into bin/<config>. On a compile error, show the errors dialog (a toast
    // truncates a diagnostics list); on success, toast.
    private async Task OnBuildAsync(string configuration)
    {
        SolutionBuildService.BuildResult result = await Workspace.BuildAsync(configuration);
        if (result.IsError)
        {
            await ShowBuildErrorsAsync($"Build ({result.Configuration}) failed.", result.Diagnostics);
            return;
        }

        Notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = "Build succeeded",
            Detail = result.Message,
            Duration = 4000,
        });
    }

    // Operator Run — build then launch the picked executable project. A build failure opens the errors dialog
    // (nothing is launched); a successful launch toasts.
    private async Task OnRunAsync(SourceRunRequest request)
    {
        SolutionRunService.RunResult result = await Workspace.RunAsync(request.Configuration, request.ProjectPath);
        if (result.IsError && result.BuildDiagnostics is { Count: > 0 })
        {
            await ShowBuildErrorsAsync(result.Message, result.BuildDiagnostics);
            return;
        }

        Notifications.Notify(new NotificationMessage
        {
            Severity = result.IsError ? NotificationSeverity.Warning : NotificationSeverity.Success,
            Summary = result.IsError ? "Run" : "Launched",
            Detail = result.Message,
            Duration = 5000,
        });
    }

    // Operator Rebuild Index — rides the build. On a RED build the reindex is BLOCKED and the last-good index
    // is preserved, so show the compile errors and leave the index where it was; on green, toast.
    private async Task OnRebuildIndexAsync()
    {
        AIMonitor.Data.SolutionIndexSummary? summary = await Workspace.RebuildAsync();
        if (summary is { Built: false })
        {
            string[] lines = (summary.BuildError ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToArray();
            await ShowBuildErrorsAsync(
                "Rebuild Index blocked — the build failed, so the index is unchanged (still the last good build).",
                lines);
            return;
        }

        Notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = "Index rebuilt",
            Detail = "The solution index was refreshed from the build.",
            Duration = 4000,
        });
    }

    // Open the compile-errors dialog with a summary line and the diagnostics list.
    private Task ShowBuildErrorsAsync(string summary, IReadOnlyList<string> diagnostics)
    {
        return Dialogs.OpenAsync<BuildErrorsDialog>(
            "Compile errors",
            new Dictionary<string, object> { ["Summary"] = summary, ["Diagnostics"] = diagnostics },
            new DialogOptions { Width = "780px", Resizable = true, Draggable = true });
    }

    // Operator Stop — stop the app launched from here (releases its exe lock so the next Build/Run works).
    private async Task OnStopAsync()
    {
        SolutionRunService.RunResult result = await Workspace.StopRunAsync();
        Notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Info,
            Summary = "Stop",
            Detail = result.Message,
            Duration = 4000,
        });
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Workspace.Changed -= OnChanged;
    }
}
