using System.Text.RegularExpressions;

namespace AIMonitor.Core;

public sealed record WatchedSolutionInfo(
    string SolutionPath,
    string ProjectFolder,
    bool SolutionExists)
{
    public static WatchedSolutionInfo FromSettings(MonitorSettings settings)
    {
        return new WatchedSolutionInfo(
            settings.WatchedSolutionPath,
            settings.WatchedProjectFolder,
            File.Exists(settings.WatchedSolutionPath));
    }

    /// <summary>
    /// The single project the ADR-0007 build-output index path can ride: a <c>.csproj</c> directly, or a
    /// <c>.slnx</c> that contains exactly one project. Returns null for anything else (no project, or more than
    /// one — multi-project is a later increment), so callers fall back to the existing loader/whole-solution build.
    /// </summary>
    public static string? ResolveSingleProject(string solutionOrProjectPath)
    {
        string extension = Path.GetExtension(solutionOrProjectPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(solutionOrProjectPath);
        }

        if (!extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(solutionOrProjectPath)) ?? string.Empty;
            string[] projects = Regex
                .Matches(
                    File.ReadAllText(solutionOrProjectPath),
                    "Path=\"([^\"]+\\.csproj)\"",
                    RegexOptions.IgnoreCase)
                .Select(match => Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return projects.Length == 1 ? projects[0] : null;
        }
        catch
        {
            return null;
        }
    }
}
