using AIMonitor.Core;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Operator file attachments. Files land in the workspace runtime's uploads/ folder
// (outside the watched tree); the sidecar grants the agent read access there via
// additionalDirectories so it can Read them. The saved path is referenced in the
// prompt so the agent knows to open it.
public sealed class UploadService
{
    private readonly WorkspaceManager workspace;

    public UploadService(WorkspaceManager workspace)
    {
        this.workspace = workspace;
    }

    public bool Available => workspace.HasWorkspace;

    public string? UploadsDirectory
    {
        get
        {
            return workspace.HasWorkspace
                ? Path.Combine(MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(workspace.Settings), "uploads")
                : null;
        }
    }

    public async Task<string> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        string? directory = UploadsDirectory;
        if (directory is null)
        {
            throw new InvalidOperationException("No watched workspace is selected; cannot accept uploads.");
        }

        Directory.CreateDirectory(directory);
        string target = UniquePath(directory, SanitizeName(fileName));
        using (FileStream file = File.Create(target))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        return target;
    }

    private static string SanitizeName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "upload";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private static string UniquePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; ; index++)
        {
            string next = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }
}
