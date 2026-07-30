using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ClaudeWorkbench.Host.Components.Dialogs;

// Per-solution workspace configuration editor (.claudeworkbench.json + the solution's .gitignore).
// Operator-driven and host-side: edits are written straight to disk, NOT through the agent/merge-review
// workflow. Both files live beside the solution and are meant to be committed, so a save shows up as an
// ordinary Git-page change.
public partial class WorkspaceConfigDialog
{
    // Signals the caller whether anything was written (so it can refresh the Git view).
    public sealed record Saved(bool GitignoreChanged);

    private WorkspaceConfig config = new();
    private string gitignoreText = string.Empty;
    private string originalGitignore = string.Empty;
    private string excludeDirectoriesText = string.Empty;
    private string defaultBranch = string.Empty;

    private bool busy;
    private string? statusMessage;
    private bool statusIsError;

    protected override void OnInitialized()
    {
        config = Config.Load();
        originalGitignore = Git.ReadGitignoreOrTemplate();
        gitignoreText = originalGitignore;
        excludeDirectoriesText = string.Join('\n', config.FilesTree.ExcludeDirectories);
        defaultBranch = config.Git.DefaultBranch ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        if (busy)
        {
            return;
        }

        busy = true;
        statusMessage = null;
        StateHasChanged();

        // .claudeworkbench.json — parse the exclude list (newline/comma separated), trim, dedupe.
        config.FilesTree.ExcludeDirectories = excludeDirectoriesText
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.Git.DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? null : defaultBranch.Trim();

        bool configOk = Config.Save(config);

        // .gitignore — only write when it actually changed (avoids a spurious diff/mtime).
        bool gitignoreChanged = !string.Equals(gitignoreText, originalGitignore, StringComparison.Ordinal);
        bool gitignoreOk = true;
        if (gitignoreChanged)
        {
            gitignoreOk = Git.WriteGitignore(gitignoreText);
        }

        busy = false;

        if (!configOk || !gitignoreOk)
        {
            statusIsError = true;
            statusMessage = !configOk
                ? "Couldn't write .claudeworkbench.json (no workspace, or the file is read-only)."
                : "Saved config, but couldn't write .gitignore.";
            StateHasChanged();
            return;
        }

        DialogService.Close(new Saved(gitignoreChanged));
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && !busy)
        {
            DialogService.Close(null);
        }
    }
}
