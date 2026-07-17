using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

public partial class ApprovalsTab : IDisposable
{
    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    protected override void OnInitialized()
    {
        Approvals.Changed += OnChanged;
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async Task ResolveAsync(string approvalId, bool approve)
    {
        await Approvals.ResolveApprovalAsync(approvalId, approve);
    }

    public void Dispose()
    {
        Approvals.Changed -= OnChanged;
    }
}
