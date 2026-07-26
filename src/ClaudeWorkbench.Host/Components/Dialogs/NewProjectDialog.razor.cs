using AIMonitor.Workflow;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ClaudeWorkbench.Host.Components.Dialogs;

// Add-project popup for the Source tab. Enumerates the installed SDK's templates/frameworks on open,
// then scaffolds (dotnet new + sln add + restore) and reindexes on Create. All work is host-side and
// out-of-process; the agent is not involved. See ProjectScaffoldService for the mechanism.
public partial class NewProjectDialog
{
    // The dialog result on success — carried back so the caller can toast it.
    public sealed record Created(string Message, string ProjectPath);

    private bool loading = true;
    private bool busy;
    private IReadOnlyList<ProjectScaffoldService.ProjectKind> kinds = [];
    private IReadOnlyList<string> frameworks = [];

    private string kind = string.Empty;
    private string projectName = string.Empty;
    private string framework = string.Empty;
    private string subfolder = string.Empty;

    private string? errorMessage;
    private IReadOnlyList<string> diagnostics = [];

    private string? validationError => Validate();

    private bool CanCreate => validationError is null && kind.Length > 0;

    protected override async Task OnInitializedAsync()
    {
        // Enumerating templates shells out to `dotnet new list` (~1-2s) — do it off the UI thread so the
        // dialog paints its loading state immediately.
        (IReadOnlyList<ProjectScaffoldService.ProjectKind> loadedKinds, IReadOnlyList<string> loadedFrameworks) =
            await Task.Run(() => (Scaffolder.ListProjectKinds(), Scaffolder.ListTargetFrameworks()));

        kinds = loadedKinds;
        frameworks = loadedFrameworks;

        // Default to a class library (the most common building block) if present, else the first kind.
        kind = kinds.Any(candidate => candidate.ShortName == "classlib")
            ? "classlib"
            : kinds.FirstOrDefault()?.ShortName ?? string.Empty;
        framework = frameworks.FirstOrDefault() ?? string.Empty;

        loading = false;
    }

    // The path the project will be created at, shown live so the operator sees where it lands (and that
    // it stays inside the solution folder).
    private string PreviewPath
    {
        get
        {
            string root = SolutionRoot();
            string leaf = string.IsNullOrWhiteSpace(projectName) ? "<name>" : projectName.Trim();
            string relative = string.IsNullOrWhiteSpace(subfolder)
                ? leaf
                : Path.Combine(subfolder.Trim(), leaf);
            return Path.Combine(root, relative);
        }
    }

    private string SolutionRoot()
    {
        string? solution = Workspace.WatchedSolutionPath;
        return string.IsNullOrWhiteSpace(solution)
            ? string.Empty
            : Path.GetDirectoryName(Path.GetFullPath(solution)) ?? string.Empty;
    }

    private string? Validate()
    {
        string trimmed = projectName.Trim();
        if (trimmed.Length == 0)
        {
            return null; // empty is "incomplete", not an error to nag about before typing
        }

        if (!char.IsLetter(trimmed[0]) && trimmed[0] != '_')
        {
            return "The name must start with a letter or underscore.";
        }

        foreach (char character in trimmed)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character != '-')
            {
                return "Use only letters, digits, '.', '_' or '-'.";
            }
        }

        return null;
    }

    private async Task CreateAsync()
    {
        if (busy || !CanCreate)
        {
            return;
        }

        busy = true;
        errorMessage = null;
        diagnostics = [];
        StateHasChanged();

        ProjectScaffoldService.ScaffoldRequest request = new(
            kind,
            projectName.Trim(),
            string.IsNullOrWhiteSpace(framework) ? null : framework,
            string.IsNullOrWhiteSpace(subfolder) ? null : subfolder.Trim());

        // Scaffolding shells the SDK; keep it off the UI thread.
        ProjectScaffoldService.ScaffoldResult result =
            await Task.Run(() => Scaffolder.Create(Workspace.Settings, request));

        if (result.IsError)
        {
            errorMessage = result.Message;
            diagnostics = result.Diagnostics;
            busy = false;
            StateHasChanged();
            return;
        }

        // Success: reindex so the new project shows up in the Source tree (fires WorkspaceManager.Changed,
        // which the SourceWorkspace listens for and refreshes from).
        await Workspace.ProvisionAsync();
        DialogService.Close(new Created(result.Message, result.ProjectPath));
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && !busy)
        {
            DialogService.Close(null);
        }
    }
}
