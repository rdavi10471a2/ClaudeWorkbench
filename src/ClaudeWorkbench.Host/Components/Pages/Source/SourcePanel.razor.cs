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

    // Operator Build — real output into bin/<config>; toast the outcome.
    private async Task OnBuildAsync(string configuration)
    {
        SolutionBuildService.BuildResult result = await Workspace.BuildAsync(configuration);
        Notifications.Notify(new NotificationMessage
        {
            Severity = result.IsError ? NotificationSeverity.Error : NotificationSeverity.Success,
            Summary = result.IsError ? "Build failed" : "Build succeeded",
            Detail = result.IsError && result.Diagnostics.Count > 0 ? result.Diagnostics[0] : result.Message,
            Duration = result.IsError ? 8000 : 4000,
        });
    }

    // Operator Run — build then launch the picked executable project; toast the outcome.
    private async Task OnRunAsync(SourceRunRequest request)
    {
        SolutionRunService.RunResult result = await Workspace.RunAsync(request.Configuration, request.ProjectPath);
        Notifications.Notify(new NotificationMessage
        {
            Severity = result.IsError ? NotificationSeverity.Warning : NotificationSeverity.Success,
            Summary = result.IsError ? "Run" : "Launched",
            Detail = result.Message,
            Duration = 5000,
        });
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
