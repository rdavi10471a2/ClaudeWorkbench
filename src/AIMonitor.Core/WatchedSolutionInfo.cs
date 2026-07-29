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
    /// solution — <c>.slnx</c> or legacy <c>.sln</c> — that contains exactly one project. Returns null for
    /// anything else (no project, or more than one — multi-project is a later increment), so callers fall back
    /// to the existing loader/whole-solution build.
    /// </summary>
    public static string? ResolveSingleProject(string solutionOrProjectPath)
    {
        string extension = Path.GetExtension(solutionOrProjectPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(solutionOrProjectPath);
        }

        if (!extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] projects = EnumerateProjects(solutionOrProjectPath);
        return projects.Length == 1 ? projects[0] : null;
    }

    /// <summary>
    /// Every C# project the watched entry contains: a bare <c>.csproj</c> is itself; a <c>.slnx</c> or legacy
    /// <c>.sln</c> yields all of its <c>.csproj</c> projects (solution folders, which carry no <c>.csproj</c>,
    /// are ignored). Empty if none resolve. This is the multi-project counterpart to
    /// <see cref="ResolveSingleProject"/> — the whole-solution build-output read enumerates these.
    /// </summary>
    public static IReadOnlyList<string> ResolveAllProjects(string solutionOrProjectPath)
    {
        string extension = Path.GetExtension(solutionOrProjectPath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return [Path.GetFullPath(solutionOrProjectPath)];
        }

        return EnumerateProjects(solutionOrProjectPath);
    }

    // The .csproj set referenced by a .slnx or .sln. Both formats reference each C# project as a quoted path
    // ending in .csproj:
    //   .slnx  <Project Path="..\Foo\Foo.csproj" />
    //   .sln   Project("{FAE04EC0-...}") = "Foo", "Foo\Foo.csproj", "{...}"
    // A quoted-.csproj match captures the project path in either; solution folders carry no .csproj so they are
    // ignored. Returns empty on any error (missing file, unreadable) so callers fall back cleanly.
    private static string[] EnumerateProjects(string solutionPath)
    {
        string extension = Path.GetExtension(solutionPath);
        if (!extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty;
            return Regex
                .Matches(File.ReadAllText(solutionPath), "\"([^\"]+\\.csproj)\"", RegexOptions.IgnoreCase)
                .Select(match => Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
