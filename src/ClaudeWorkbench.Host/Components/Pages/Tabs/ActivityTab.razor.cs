using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

public partial class ActivityTab : IDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

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
