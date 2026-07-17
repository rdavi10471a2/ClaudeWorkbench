using AIMonitor.McpServer;
using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages;

public partial class Home : IDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    [Inject]
    private WorkspaceManager Workspace { get; set; } = default!;

    private bool settingsOpen;
    private bool workspacePickerOpen;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
        Workspace.Changed += OnChanged;
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private void CloseWorkspacePicker()
    {
        workspacePickerOpen = false;
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
        Workspace.Changed -= OnChanged;
    }
}
