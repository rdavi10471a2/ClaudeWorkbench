using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages.Source;

// Thin view over the singleton SourceWorkspace: renders its retained state and
// forwards events. State lives in the service, so it survives tab switches,
// component re-creation, and browser refresh within a host session.
public partial class SourcePanel : IDisposable
{
    [Inject]
    private ClaudeWorkbench.Host.Source.SourceWorkspace Workspace { get; set; } = default!;

    protected override void OnInitialized()
    {
        Workspace.Changed += OnChanged;
        Workspace.EnsureLoaded();
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
