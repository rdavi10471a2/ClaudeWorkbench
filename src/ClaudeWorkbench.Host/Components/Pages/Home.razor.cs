using AIMonitor.McpServer;
using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

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
    private IJSRuntime JS { get; set; } = default!;

    private bool settingsOpen;
    private bool workspacePickerOpen;
    private IJSObjectReference? unloadModule;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
        Workspace.Changed += OnChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            unloadModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
            await unloadModule.InvokeVoidAsync(
                "setBeforeUnloadGuard",
                true,
                "Leaving or refreshing will reset the current Claude Workbench session.");
        }
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
