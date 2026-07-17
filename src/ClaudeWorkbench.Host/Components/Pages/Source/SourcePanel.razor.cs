using ClaudeWorkbench.Host.Source;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages.Source;

// Owns source-browser state (filter, selection, rebuild) and drives the ported
// presentational SourceTab against the engine-backed SourceWorkspace.
public partial class SourcePanel
{
    [Inject]
    private SourceWorkspace Workspace { get; set; } = default!;

    private SourceWorkspaceSnapshot snapshot = SourceWorkspaceSnapshot.Empty("Loading source index...");
    private string filter = string.Empty;
    private string? selectedPath;
    private int? selectedLine;
    private bool rebuilding;

    protected override void OnInitialized()
    {
        Refresh();
    }

    private void Refresh()
    {
        snapshot = Workspace.BuildSnapshot(selectedPath, selectedLine, filter);
    }

    private void OnFilterChanged(string value)
    {
        filter = value;
    }

    private void ApplyFilter()
    {
        Refresh();
    }

    private void OnSelectFile(SourceSelection selection)
    {
        selectedPath = selection.RelativePath;
        selectedLine = selection.Line;
        Refresh();
    }

    private async Task RebuildAsync()
    {
        rebuilding = true;
        StateHasChanged();
        try
        {
            await Workspace.RebuildIndexAsync();
        }
        finally
        {
            rebuilding = false;
            selectedPath = null;
            selectedLine = null;
            Refresh();
        }
    }
}
