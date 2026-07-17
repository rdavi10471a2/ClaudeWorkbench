using System.Diagnostics;
using System.Xml.Linq;
using AIMonitor.Core;

namespace AIMonitor.Workflow;

public sealed class PreMergeValidationService
{
    public PreMergeValidationResult ValidateStagedOverlay(
        StagedEditRecord record,
        IReadOnlyList<StagedEditRecord> stagedOverlayRecords)
    {
        StagedEditRecord[] overlayRecords = NormalizeOverlayRecords(record, stagedOverlayRecords);
        foreach (StagedEditRecord overlayRecord in overlayRecords)
        {
            PreMergeValidationResult? hashFailure = ValidateStagedRecordHash(overlayRecord);
            if (hashFailure is not null)
            {
                return hashFailure;
            }
        }

        return new PreMergeValidationResult
        {
            Status = overlayRecords.Length <= 1 ? "staged-file-ready" : "planned-staged-overlay-ready",
            IsError = false,
            DiagnosticCount = 0,
            Diagnostics = [],
            Message = overlayRecords.Length <= 1
                ? "Staged file is ready for WinMerge review. Build/index validation is deferred until accept."
                : $"Planned staged overlay is ready for WinMerge review with {overlayRecords.Length} staged files. Build/index validation is deferred until the planned files are accepted."
        };
    }

    public PreMergeValidationResult Validate(MonitorSettings settings, StagedEditRecord record)
    {
        return Validate(settings, record, [record]);
    }

    public PreMergeValidationResult Validate(
        MonitorSettings settings,
        StagedEditRecord record,
        IReadOnlyList<StagedEditRecord> stagedOverlayRecords)
    {
        StagedEditRecord[] overlayRecords = NormalizeOverlayRecords(record, stagedOverlayRecords);
        foreach (StagedEditRecord overlayRecord in overlayRecords)
        {
            PreMergeValidationResult? hashFailure = ValidateStagedRecordHash(overlayRecord);
            if (hashFailure is not null)
            {
                return hashFailure;
            }
        }

        if (!File.Exists(settings.WatchedSolutionPath))
        {
            return new PreMergeValidationResult
            {
                Status = "missing-solution",
                IsError = true,
                Message = "Pre-merge validation failed because the watched solution file is missing."
            };
        }

        string validationWorkspaceRoot = Path.Combine(
            MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings),
            "validation",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..42]);
        string sourceRoot = settings.WatchedProjectFolder;
        IReadOnlyList<string> excludedRoots = [settings.RuntimeRoot, validationWorkspaceRoot];
        ExternalValidationInputs externalInputs = CollectExternalValidationInputs(sourceRoot, excludedRoots);
        string validationSourceRoot = Path.Combine(
            validationWorkspaceRoot,
            Path.GetRelativePath(externalInputs.CommonRoot, sourceRoot));
        string validationSolutionPath = Path.Combine(validationSourceRoot, Path.GetRelativePath(sourceRoot, settings.WatchedSolutionPath));
        try
        {
            CopyDirectoryForValidation(sourceRoot, validationSourceRoot, excludedRoots);
            CopyExternalValidationInputs(externalInputs, validationWorkspaceRoot, excludedRoots);

            foreach (StagedEditRecord overlayRecord in overlayRecords)
            {
                string validationCandidatePath = Path.Combine(validationSourceRoot, overlayRecord.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(validationCandidatePath) ?? validationSourceRoot);
                File.Copy(overlayRecord.StagedFilePath, validationCandidatePath, overwrite: true);
            }

            ProcessResult build = RunProcess(
                "dotnet",
                ["build", validationSolutionPath, "--nologo", "-v:minimal"],
                validationWorkspaceRoot,
                TimeSpan.FromMinutes(3));
            string output = string.Join(Environment.NewLine, [build.StandardOutput, build.StandardError]);
            string[] errorDiagnostics = ExtractBuildErrors(output);
            bool failed = build.TimedOut || build.ExitCode != 0;
            if (failed && errorDiagnostics.Length == 0)
            {
                errorDiagnostics = [$"dotnet build exited with code {build.ExitCode} but did not emit parseable error diagnostics."];
            }

            return new PreMergeValidationResult
            {
                Status = build.TimedOut ? "timeout" : failed ? "failed" : "passed",
                IsError = failed,
                DiagnosticCount = errorDiagnostics.Length,
                Diagnostics = errorDiagnostics,
                ValidationWorkspacePath = validationWorkspaceRoot,
                Message = build.TimedOut
                    ? CreateValidationMessage("timed out", overlayRecords.Length)
                    : failed
                        ? CreateValidationMessage("failed", overlayRecords.Length)
                        : CreateValidationMessage("passed", overlayRecords.Length)
            };
        }
        catch (Exception ex)
        {
            return new PreMergeValidationResult
            {
                Status = "failed",
                IsError = true,
                DiagnosticCount = 1,
                Diagnostics = [ex.Message],
                ValidationWorkspacePath = validationWorkspaceRoot,
                Message = CreateValidationMessage("failed", overlayRecords.Length)
            };
        }
    }

    private static StagedEditRecord[] NormalizeOverlayRecords(
        StagedEditRecord currentRecord,
        IReadOnlyList<StagedEditRecord> stagedOverlayRecords)
    {
        Dictionary<string, StagedEditRecord> recordsByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (StagedEditRecord overlayRecord in stagedOverlayRecords)
        {
            if (!string.IsNullOrWhiteSpace(overlayRecord.WatchedFilePath))
            {
                recordsByPath[Path.GetFullPath(overlayRecord.WatchedFilePath)] = overlayRecord;
            }
        }

        recordsByPath[Path.GetFullPath(currentRecord.WatchedFilePath)] = currentRecord;
        return recordsByPath.Values
            .OrderBy(overlayRecord => overlayRecord.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PreMergeValidationResult? ValidateStagedRecordHash(StagedEditRecord record)
    {
        if (!File.Exists(record.StagedFilePath))
        {
            return new PreMergeValidationResult
            {
                Status = "missing-staged-file",
                IsError = true,
                Message = "Validation failed because a staged overlay file is missing."
            };
        }

        string currentStagedHash = FileHash.Compute(record.StagedFilePath);
        if (!currentStagedHash.Equals(record.StagedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new PreMergeValidationResult
            {
                Status = "staged-hash-mismatch",
                IsError = true,
                DiagnosticCount = 1,
                Diagnostics = ["A staged overlay file changed after staging. Edit the Working file and run edit stage again."],
                Message = "Validation failed because a staged overlay file no longer matches its recorded hash."
            };
        }

        return null;
    }

    private static string CreateValidationMessage(string outcome, int overlayFileCount)
    {
        if (overlayFileCount <= 1)
        {
            return $"Pre-merge single-file full solution build {outcome}.";
        }

        return $"Pre-merge session overlay full solution build {outcome} with {overlayFileCount} staged files.";
    }

    private static ExternalValidationInputs CollectExternalValidationInputs(string sourceRoot, IReadOnlyList<string> excludedRoots)
    {
        HashSet<string> projectFiles = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pendingProjects = new();
        foreach (string projectFile in EnumerateProjectFiles(sourceRoot, excludedRoots))
        {
            projectFiles.Add(Path.GetFullPath(projectFile));
            pendingProjects.Enqueue(Path.GetFullPath(projectFile));
        }

        HashSet<string> externalDirectories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> externalFiles = new(StringComparer.OrdinalIgnoreCase);
        AddBuildFilesFromDirectoryAndParents(sourceRoot, sourceRoot, externalFiles);

        while (pendingProjects.Count > 0)
        {
            string projectFile = pendingProjects.Dequeue();
            string projectDirectory = Path.GetDirectoryName(projectFile) ?? sourceRoot;
            AddBuildFilesFromDirectoryAndParents(projectDirectory, sourceRoot, externalFiles);

            foreach (string referencedProject in ReadProjectReferences(projectFile))
            {
                if (!File.Exists(referencedProject))
                {
                    continue;
                }

                string fullReferencedProject = Path.GetFullPath(referencedProject);
                if (projectFiles.Add(fullReferencedProject))
                {
                    pendingProjects.Enqueue(fullReferencedProject);
                }

                string referencedDirectory = Path.GetDirectoryName(fullReferencedProject) ?? projectDirectory;
                if (!IsPathUnderAny(referencedDirectory, [sourceRoot]))
                {
                    externalDirectories.Add(referencedDirectory);
                }
            }

            foreach (string importFile in ReadImportFiles(projectFile))
            {
                if (File.Exists(importFile) && !IsPathUnderAny(importFile, [sourceRoot]))
                {
                    externalFiles.Add(Path.GetFullPath(importFile));
                }
            }
        }

        string commonRoot = GetCommonRoot(
            [sourceRoot, .. externalDirectories, .. externalFiles.Select(path => Path.GetDirectoryName(path) ?? sourceRoot)]);
        return new ExternalValidationInputs(commonRoot, externalDirectories.ToArray(), externalFiles.ToArray());
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root, IReadOnlyList<string> excludedRoots)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories))
        {
            if (IsPathUnderAny(file, excludedRoots)
                || file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsSkippedValidationDirectory))
            {
                continue;
            }

            yield return file;
        }
    }

    private static void AddBuildFilesFromDirectoryAndParents(string startDirectory, string sourceRoot, HashSet<string> externalFiles)
    {
        string[] fileNames =
        [
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "NuGet.config",
            "global.json"
        ];
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            foreach (string fileName in fileNames)
            {
                string path = Path.Combine(current.FullName, fileName);
                if (File.Exists(path) && !IsPathUnderAny(path, [sourceRoot]))
                {
                    externalFiles.Add(Path.GetFullPath(path));
                }
            }

            current = current.Parent;
        }
    }

    private static IEnumerable<string> ReadProjectReferences(string projectFile)
    {
        foreach (XElement item in ReadProjectItems(projectFile, "ProjectReference"))
        {
            string? include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile) ?? ".", include));
        }
    }

    private static IEnumerable<string> ReadImportFiles(string projectFile)
    {
        foreach (XElement item in ReadProjectItems(projectFile, "Import"))
        {
            string? include = item.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(include)
                || include.Contains('$', StringComparison.Ordinal)
                || include.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }

            yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile) ?? ".", include));
        }
    }

    private static IEnumerable<XElement> ReadProjectItems(string projectFile, string localName)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch
        {
            yield break;
        }

        foreach (XElement element in document.Descendants().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)))
        {
            yield return element;
        }
    }

    private static void CopyExternalValidationInputs(
        ExternalValidationInputs inputs,
        string validationWorkspaceRoot,
        IReadOnlyList<string> excludedRoots)
    {
        foreach (string directory in inputs.Directories)
        {
            string destination = MapValidationPath(inputs.CommonRoot, validationWorkspaceRoot, directory);
            CopyDirectoryForValidation(directory, destination, [.. excludedRoots, validationWorkspaceRoot]);
        }

        foreach (string file in inputs.Files)
        {
            string destination = MapValidationPath(inputs.CommonRoot, validationWorkspaceRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? validationWorkspaceRoot);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string MapValidationPath(string commonRoot, string validationWorkspaceRoot, string sourcePath)
    {
        return Path.GetFullPath(Path.Combine(validationWorkspaceRoot, Path.GetRelativePath(commonRoot, sourcePath)));
    }

    private static string GetCommonRoot(IReadOnlyList<string> paths)
    {
        string common = Path.GetFullPath(paths[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string path in paths.Skip(1))
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!fullPath.Equals(common, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(common + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                common = Path.GetDirectoryName(common) ?? common;
            }
        }

        return common;
    }

    private static void CopyDirectoryForValidation(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<string> excludedRoots)
    {
        Directory.CreateDirectory(destinationRoot);
        CopyDirectoryContentsForValidation(sourceRoot, sourceRoot, destinationRoot, excludedRoots);
    }

    private static void CopyDirectoryContentsForValidation(
        string sourceRoot,
        string currentRoot,
        string destinationRoot,
        IReadOnlyList<string> excludedRoots)
    {
        foreach (string directoryPath in Directory.EnumerateDirectories(currentRoot))
        {
            if (IsPathUnderAny(directoryPath, excludedRoots))
            {
                continue;
            }

            string directoryName = Path.GetFileName(directoryPath);
            if (IsSkippedValidationDirectory(directoryName))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(sourceRoot, directoryPath);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
            CopyDirectoryContentsForValidation(sourceRoot, directoryPath, destinationRoot, excludedRoots);
        }

        foreach (string filePath in Directory.EnumerateFiles(currentRoot))
        {
            if (IsPathUnderAny(filePath, excludedRoots))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(sourceRoot, filePath);
            if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsSkippedValidationDirectory))
            {
                continue;
            }

            string destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationRoot);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private static bool IsPathUnderAny(string path, IReadOnlyList<string> roots)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullRoot = Path.GetFullPath(root);
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSkippedValidationDirectory(string directoryName)
    {
        return directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || directoryName.Equals("packages", StringComparison.OrdinalIgnoreCase);
    }

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
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
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
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static string[] ExtractBuildErrors(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
    }

    private sealed record ProcessResult(
        int ExitCode,
        bool TimedOut,
        string StandardOutput,
        string StandardError);

    private sealed record ExternalValidationInputs(
        string CommonRoot,
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> Files);
}
