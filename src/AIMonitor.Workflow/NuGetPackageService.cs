using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

// Host-run NuGet package management for the watched solution — the operator-driven counterpart to
// SolutionRestoreService / ProjectScaffoldService. Like those, it drives the real .NET SDK
// out-of-process (dotnet package search / list / add / remove) from the solution root so NuGet.config,
// Directory.Build.props and private feeds are honoured exactly as a normal `dotnet` invocation would.
//
// It is NOT part of the agent surface: the agent never shells and edits a <PackageReference> as a
// governed staged edit instead. The host invokes this from the Source tab's "Packages" dialog.
//
// Reads (search / outdated) are cheap-ish shell-outs; writes (install / uninstall / update) mutate the
// real .csproj and then restore. The caller reindexes afterwards (the index rides the build). Every
// process uses -nodeReuse:false so no MSBuild worker node is left pinning files (the file-locking lesson).
public sealed class NuGetPackageService
{
    // A hit from `dotnet package search`. The CLI's JSON is intentionally minimal (no description), so
    // this carries only what it returns, aggregated across the configured sources.
    public sealed record PackageSearchHit(
        string Id,
        string LatestVersion,
        long? TotalDownloads,
        string? Owners,
        string SourceName);

    // A package that has a newer version than the one resolved in a project (from `dotnet list package
    // --outdated`). Drives the "Updates" tab and the "latest" column of the Installed view.
    public sealed record OutdatedPackage(
        string ProjectPath,
        string PackageId,
        string ResolvedVersion,
        string LatestVersion);

    public sealed record PackageMutationResult(
        bool IsError,
        string Message,
        IReadOnlyList<string> Diagnostics);

    // -------- reads --------

    public IReadOnlyList<PackageSearchHit> Search(string query, bool includePrerelease, int take)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        List<string> args = ["package", "search", query.Trim(), "--format", "json", "--take", take.ToString()];
        if (includePrerelease)
        {
            args.Add("--prerelease");
        }

        ProcessResult result = RunProcess("dotnet", args, Environment.CurrentDirectory, TimeSpan.FromSeconds(45));
        if (result.LaunchFailed || result.TimedOut || result.ExitCode != 0)
        {
            return [];
        }

        return ParseSearch(result.StandardOutput);
    }

    private static IReadOnlyList<PackageSearchHit> ParseSearch(string json)
    {
        List<PackageSearchHit> hits = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("searchResult", out JsonElement sources))
            {
                return hits;
            }

            foreach (JsonElement source in sources.EnumerateArray())
            {
                string sourceName = source.TryGetProperty("sourceName", out JsonElement name)
                    ? name.GetString() ?? string.Empty
                    : string.Empty;
                if (!source.TryGetProperty("packages", out JsonElement packages))
                {
                    continue;
                }

                foreach (JsonElement package in packages.EnumerateArray())
                {
                    string id = package.TryGetProperty("id", out JsonElement idElement)
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    // Dedupe across sources: the first source listed wins (nuget.org is listed first).
                    if (id.Length == 0 || !seen.Add(id))
                    {
                        continue;
                    }

                    hits.Add(new PackageSearchHit(
                        id,
                        package.TryGetProperty("latestVersion", out JsonElement version) ? version.GetString() ?? string.Empty : string.Empty,
                        package.TryGetProperty("totalDownloads", out JsonElement downloads) && downloads.TryGetInt64(out long count) ? count : null,
                        package.TryGetProperty("owners", out JsonElement owners) ? owners.GetString() : null,
                        sourceName));
                }
            }
        }
        catch (JsonException)
        {
            // Format drift: return whatever parsed rather than throwing into the Blazor circuit.
        }

        return hits;
    }

    // Ask the SDK which top-level packages have a newer version. One call for the whole solution; results
    // are keyed by (projectPath, packageId). Prerelease toggles whether pre-release upgrades count.
    public IReadOnlyList<OutdatedPackage> ListOutdated(MonitorSettings settings, bool includePrerelease)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return [];
        }

        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;
        List<string> args = ["list", solutionPath, "package", "--outdated", "--format", "json"];
        if (includePrerelease)
        {
            args.Add("--include-prerelease");
        }

        ProcessResult result = RunProcess("dotnet", args, solutionRoot, TimeSpan.FromMinutes(3));
        if (result.LaunchFailed || result.TimedOut || result.ExitCode != 0)
        {
            return [];
        }

        return ParseOutdated(result.StandardOutput);
    }

    private static IReadOnlyList<OutdatedPackage> ParseOutdated(string json)
    {
        List<OutdatedPackage> outdated = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("projects", out JsonElement projects))
            {
                return outdated;
            }

            foreach (JsonElement project in projects.EnumerateArray())
            {
                string projectPath = project.TryGetProperty("path", out JsonElement path)
                    ? Normalize(path.GetString())
                    : string.Empty;
                if (projectPath.Length == 0 || !project.TryGetProperty("frameworks", out JsonElement frameworks))
                {
                    continue;
                }

                foreach (JsonElement framework in frameworks.EnumerateArray())
                {
                    if (!framework.TryGetProperty("topLevelPackages", out JsonElement packages))
                    {
                        continue;
                    }

                    foreach (JsonElement package in packages.EnumerateArray())
                    {
                        string id = package.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                        string latest = package.TryGetProperty("latestVersion", out JsonElement latestElement) ? latestElement.GetString() ?? string.Empty : string.Empty;
                        if (id.Length == 0 || latest.Length == 0)
                        {
                            continue;
                        }

                        // One entry per (project, package) — frameworks of a multi-TFM project repeat it.
                        if (!seen.Add($"{projectPath}|{id}"))
                        {
                            continue;
                        }

                        outdated.Add(new OutdatedPackage(
                            projectPath,
                            id,
                            package.TryGetProperty("resolvedVersion", out JsonElement resolved) ? resolved.GetString() ?? string.Empty : string.Empty,
                            latest));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return outdated;
    }

    // -------- writes --------

    public PackageMutationResult Install(
        MonitorSettings settings,
        IReadOnlyList<string> projectPaths,
        string packageId,
        string? version,
        TimeSpan? timeout = null)
    {
        return Mutate(settings, projectPaths, packageId, version, remove: false, timeout);
    }

    public PackageMutationResult Uninstall(
        MonitorSettings settings,
        IReadOnlyList<string> projectPaths,
        string packageId,
        TimeSpan? timeout = null)
    {
        return Mutate(settings, projectPaths, packageId, version: null, remove: true, timeout);
    }

    // Install/Update and Uninstall share the shape: validate, run `dotnet add|remove package` for each
    // target project, then restore ONCE for the batch. A specific version is an Install with -v (that is
    // also how "Update to X" is expressed). Under Central Package Management the SDK routes the version to
    // Directory.Packages.props itself — we don't special-case it here.
    private PackageMutationResult Mutate(
        MonitorSettings settings,
        IReadOnlyList<string> projectPaths,
        string packageId,
        string? version,
        bool remove,
        TimeSpan? timeout)
    {
        string solutionPath = Path.GetFullPath(settings.WatchedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            return Error("The watched solution file is missing.");
        }

        string solutionRoot = Path.GetDirectoryName(solutionPath) ?? settings.WatchedProjectFolder;

        string? idError = ValidatePackageId(packageId);
        if (idError is not null)
        {
            return Error(idError);
        }

        if (projectPaths.Count == 0)
        {
            return Error("Pick at least one project.");
        }

        TimeSpan runTimeout = timeout ?? TimeSpan.FromMinutes(5);
        List<string> diagnostics = [];
        int changed = 0;

        foreach (string rawProjectPath in projectPaths)
        {
            string projectPath = Path.GetFullPath(rawProjectPath);

            // Containment: the target project must live inside the solution root (the greenfield contract).
            string rootWithSeparator = solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!projectPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return Error($"Project is outside the solution folder: {rawProjectPath}");
            }

            if (!File.Exists(projectPath))
            {
                return Error($"Project not found: {rawProjectPath}");
            }

            List<string> args = remove
                ? ["remove", projectPath, "package", packageId.Trim()]
                : ["add", projectPath, "package", packageId.Trim()];
            if (!remove && !string.IsNullOrWhiteSpace(version))
            {
                args.Add("--version");
                args.Add(version.Trim());
            }

            ProcessResult result = RunProcess("dotnet", args, solutionRoot, runTimeout);
            if (result.LaunchFailed)
            {
                return Error(
                    "The .NET SDK ('dotnet') wasn't found on PATH. Install the .NET 10 SDK and make sure "
                        + "'dotnet' runs from a terminal, then retry.");
            }

            if (result.TimedOut || result.ExitCode != 0)
            {
                diagnostics.AddRange(ExtractDiagnostics(result));
                string verb = remove ? "remove" : "install";
                return new PackageMutationResult(
                    true,
                    result.TimedOut
                        ? $"Timed out trying to {verb} {packageId} in {Path.GetFileNameWithoutExtension(projectPath)}."
                        : $"Failed to {verb} {packageId} in {Path.GetFileNameWithoutExtension(projectPath)}.",
                    diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray());
            }

            changed++;
        }

        // One restore for the whole batch so the index's build sees restored assets. Cheap when current.
        ProcessResult restored = RunProcess(
            "dotnet",
            ["restore", solutionPath, "--nologo", "-nodeReuse:false"],
            solutionRoot,
            runTimeout);

        string action = remove ? "Removed" : string.IsNullOrWhiteSpace(version) ? "Installed" : $"Set {packageId} to {version} in";
        string scope = changed == 1 ? "1 project" : $"{changed} projects";
        string summary = remove
            ? $"{action} {packageId} from {scope}."
            : string.IsNullOrWhiteSpace(version)
                ? $"{action} {packageId} in {scope}."
                : $"{action} {scope}.";

        if (restored.TimedOut || restored.ExitCode != 0)
        {
            // Non-fatal: the .csproj changes are applied; restore/reindex can be retried.
            return new PackageMutationResult(
                false,
                $"{summary} Restore reported issues — a rebuild may be needed.",
                ExtractDiagnostics(restored));
        }

        return new PackageMutationResult(false, summary, []);
    }

    private static PackageMutationResult Error(string message) => new(true, message, []);

    // NuGet package id grammar: dot-separated segments of letters/digits/_/-, e.g. Serilog.Sinks.File.
    private static string? ValidatePackageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Enter a package id.";
        }

        foreach (char character in id.Trim())
        {
            if (!char.IsLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
            {
                return "That doesn't look like a valid package id.";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractDiagnostics(ProcessResult result)
    {
        string output = string.Join(Environment.NewLine, [result.StandardOutput, result.StandardError]);
        string[] diagnostics = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("NU1", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        if (diagnostics.Length == 0)
        {
            string fallback = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            diagnostics = fallback
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(6)
                .ToArray();
        }

        return diagnostics;
    }

    private static string Normalize(string? path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ProcessResult(-1, false, true, string.Empty, ex.Message);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeout);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return new ProcessResult(
            exited ? process.ExitCode : -1,
            !exited,
            false,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private sealed record ProcessResult(int ExitCode, bool TimedOut, bool LaunchFailed, string StandardOutput, string StandardError);
}
