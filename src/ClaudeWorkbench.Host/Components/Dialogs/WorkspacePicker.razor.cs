using AIMonitor.McpServer;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Dialogs;

public partial class WorkspacePicker
{
    [Inject]
    private DirectoryBrowserService Browser { get; set; } = default!;

    [Inject]
    private WorkspaceCoordinator Coordinator { get; set; } = default!;

    [Inject]
    private WorkspaceManager Workspace { get; set; } = default!;

    // Embedded = first-run, full-screen, no cancel. Otherwise a dismissable switch dialog.
    [Parameter]
    public bool Embedded { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    private DirectoryBrowserSnapshot snapshot = new(string.Empty, null, [], [], [], null);
    private bool busy;

    protected override void OnInitialized()
    {
        string? start = Workspace.WatchedSolutionPath is { } path ? Path.GetDirectoryName(path) : null;
        snapshot = Browser.GetSnapshot(start);
    }

    private void Navigate(string path)
    {
        snapshot = Browser.GetSnapshot(path);
    }

    private async Task ChooseAsync(string solutionPath)
    {
        busy = true;
        try
        {
            await Coordinator.SelectAsync(solutionPath);
        }
        finally
        {
            busy = false;
        }

        await OnClose.InvokeAsync();
    }

    private async Task Cancel()
    {
        await OnClose.InvokeAsync();
    }
}
