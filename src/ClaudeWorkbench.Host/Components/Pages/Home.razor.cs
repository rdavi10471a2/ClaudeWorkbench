using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages;

public partial class Home : IDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    private bool settingsOpen;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
    }
}
