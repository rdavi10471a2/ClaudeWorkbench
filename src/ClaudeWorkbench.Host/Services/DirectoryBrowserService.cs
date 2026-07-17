namespace ClaudeWorkbench.Host.Services;

// Filesystem navigation for the workspace picker (ported from CodexAppServerDemo),
// plus enumeration of the solution files in the current directory so the operator
// can pick a watched solution.
public sealed class DirectoryBrowserService
{
    public DirectoryBrowserSnapshot GetSnapshot(string? requestedPath)
    {
        string resolvedPath = ResolveRequestedPath(requestedPath);
        string? errorMessage = null;
        if (!Directory.Exists(resolvedPath))
        {
            errorMessage = "Directory does not exist.";
            resolvedPath = Directory.GetCurrentDirectory();
        }

        DirectoryEntryViewModel[] drives = GetDrives();
        DirectoryEntryViewModel[] children = [];
        DirectoryEntryViewModel[] solutions = [];
        try
        {
            children = Directory.EnumerateDirectories(resolvedPath)
                .Select(path => new DirectoryInfo(path))
                .Where(info => !IsHiddenOrSystem(info.Attributes))
                .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(info => new DirectoryEntryViewModel(info.Name, info.FullName))
                .ToArray();

            solutions = Directory.EnumerateFiles(resolvedPath)
                .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new DirectoryEntryViewModel(Path.GetFileName(path), Path.GetFullPath(path)))
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            errorMessage = ex.Message;
        }

        string? parentPath = Directory.GetParent(resolvedPath)?.FullName;
        return new DirectoryBrowserSnapshot(
            Path.GetFullPath(resolvedPath),
            parentPath,
            drives,
            children,
            solutions,
            errorMessage);
    }

    private static string ResolveRequestedPath(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Directory.GetCurrentDirectory();
        }

        string expanded = Environment.ExpandEnvironmentVariables(requestedPath.Trim().Trim('"'));
        if (File.Exists(expanded))
        {
            return Path.GetDirectoryName(Path.GetFullPath(expanded)) ?? Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(expanded);
    }

    private static DirectoryEntryViewModel[] GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .Select(drive => new DirectoryEntryViewModel(drive.Name, drive.RootDirectory.FullName))
            .ToArray();
    }

    private static bool IsHiddenOrSystem(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
    }
}

public sealed record DirectoryBrowserSnapshot(
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<DirectoryEntryViewModel> Drives,
    IReadOnlyList<DirectoryEntryViewModel> Children,
    IReadOnlyList<DirectoryEntryViewModel> Solutions,
    string? ErrorMessage);

public sealed record DirectoryEntryViewModel(string Name, string FullPath);
