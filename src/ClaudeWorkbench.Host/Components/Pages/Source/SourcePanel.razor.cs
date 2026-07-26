using ClaudeWorkbench.Host.Components.Dialogs;
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

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Workspace.Changed -= OnChanged;
    }
}
