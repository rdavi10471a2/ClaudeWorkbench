using AIMonitor.Core;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Security.Cryptography;
using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace AIMonitor.MSBuild;

public sealed class MSBuildWorkspaceLoader
{
    private static readonly object RegistrationGate = new();
    private static readonly HashSet<string> IgnoredDocumentDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "Working",
        "archive",
        "Archive",
        "MonitorWorkspace",
        "SourceBackups"
    };

    private static bool registrationAttempted;

    public MSBuildWorkspaceLoader()
    {
    }

    public async Task<MSBuildSolutionSnapshot> OpenSolutionAsync(
        string solutionPath,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        EnsureMSBuildRegistered();
        using (MSBuildWorkspace workspace = MSBuildWorkspace.Create())
        {
            Solution solution = await MeasureAsync(
                "msbuild.open-solution",
                timingSink,
                new Dictionary<string, string>
                {
                    ["inputPath"] = Path.GetFullPath(solutionPath)
                },
                () => workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken));
            return await CreateSnapshotAsync(solutionPath, solution, workspace.Diagnostics, cancellationToken, timingSink);
        }
    }

    public async Task<MSBuildSolutionSnapshot> OpenProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        EnsureMSBuildRegistered();
        using (MSBuildWorkspace workspace = MSBuildWorkspace.Create())
        {
            Microsoft.CodeAnalysis.Project project = await MeasureAsync(
                "msbuild.open-project",
                timingSink,
                new Dictionary<string, string>
                {
                    ["projectPath"] = Path.GetFullPath(projectPath)
                },
                () => workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken));
            return await CreateSnapshotAsync(projectPath, project.Solution, workspace.Diagnostics, cancellationToken, timingSink);
        }
    }

    public async Task<MSBuildProjectFileSnapshot> OpenProjectFilesAsync(
        string projectPath,
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> existingSymbolsByIdentity,
        CancellationToken cancellationToken = default,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        EnsureMSBuildRegistered();

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        string[] normalizedFilePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFilePaths.Length == 0)
        {
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));
        }

        using (MSBuildWorkspace workspace = MSBuildWorkspace.Create())
        {
            Microsoft.CodeAnalysis.Project project = await MeasureAsync(
                "msbuild.open-project",
                timingSink,
                new Dictionary<string, string>
                {
                    ["projectPath"] = normalizedProjectPath
                },
                () => workspace.OpenProjectAsync(normalizedProjectPath, cancellationToken: cancellationToken));
            HashSet<string> fileSet = normalizedFilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Solution refreshedSolution = await RefreshProjectDocumentsFromDiskAsync(
                project.Solution,
                project,
                fileSet,
                cancellationToken);
            project = refreshedSolution.GetProject(project.Id)
                ?? throw new InvalidOperationException("MSBuild project disappeared after refreshing source text: " + normalizedProjectPath);
            return await CreateProjectFileSnapshotAsync(
                normalizedProjectPath,
                project,
                workspace.Diagnostics,
                normalizedFilePaths,
                existingSymbolsByIdentity,
                cancellationToken,
                timingSink);
        }
    }

    private static async Task<Solution> RefreshProjectDocumentsFromDiskAsync(
        Solution solution,
        Microsoft.CodeAnalysis.Project project,
        IReadOnlySet<string>? includedFilePaths,
        CancellationToken cancellationToken)
    {
        foreach (Document document in project.Documents.Where(IsIndexableDocument).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? filePath = document.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            string normalizedFilePath = Path.GetFullPath(filePath);
            if (includedFilePaths is not null && !includedFilePaths.Contains(normalizedFilePath))
            {
                continue;
            }

            string text = await File.ReadAllTextAsync(normalizedFilePath, cancellationToken);
            solution = solution.WithDocumentText(document.Id, SourceText.From(text), PreservationMode.PreserveValue);
        }

        return solution;
    }

    private static void EnsureMSBuildRegistered()
    {
        lock (RegistrationGate)
        {
            if (registrationAttempted)
            {
                return;
            }

            registrationAttempted = true;

            // Before the first workspace exists, so Roslyn's BuildHost child is born inside the job
            // and cannot outlive this process. See WorkspaceChildProcessJob for why that matters.
            WorkspaceChildProcessJob.EnsureCurrentProcessIsJobbed();

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private static async Task<MSBuildSolutionSnapshot> CreateSnapshotAsync(
        string inputPath,
        Solution solution,
        IEnumerable<WorkspaceDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        Microsoft.CodeAnalysis.Project[] orderedRoslynProjects = solution.Projects
            .OrderBy(project => project.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<ProjectId, Compilation?> compilations = [];
        Dictionary<ProjectId, MSBuildEvaluatedProject> evaluatedProjects = [];
        Dictionary<ProjectId, ProjectSymbolIndex> symbolIndexes = [];
        Dictionary<ProjectId, IReadOnlyList<RazorDocumentIndex>> razorDocumentsByProject = [];
        Dictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity = new(StringComparer.Ordinal);

        foreach (Microsoft.CodeAnalysis.Project project in orderedRoslynProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, string> projectTimingProperties = CreateProjectTimingProperties(project);
            Compilation? compilation = await MeasureAsync(
                "msbuild.solution.get-compilation-in-memory",
                timingSink,
                projectTimingProperties,
                () => project.GetCompilationAsync(cancellationToken));
            compilations[project.Id] = compilation;

            MSBuildEvaluatedProject evaluatedProject = MSBuildEvaluatedProject.Empty;
            if (!string.IsNullOrWhiteSpace(project.FilePath) && File.Exists(project.FilePath))
            {
                evaluatedProject = Measure(
                    "msbuild.solution.evaluate-project",
                    timingSink,
                    projectTimingProperties,
                    () => MSBuildEvaluatedProject.Load(project.FilePath));
            }

            evaluatedProjects[project.Id] = evaluatedProject;
            ProjectSymbolIndex symbolIndex = compilation is null
                ? ProjectSymbolIndex.Empty
                : await MeasureAsync(
                    "msbuild.solution.build-declarations",
                    timingSink,
                    projectTimingProperties,
                    () => ProjectSymbolIndex.BuildDeclarationsAsync(project, compilation, cancellationToken));
            IReadOnlyList<RazorDocumentIndex> razorDocuments = RazorDocumentIndex.BuildForProject(
                project,
                evaluatedProject.RazorLikeFiles);
            razorDocumentsByProject[project.Id] = razorDocuments;
            if (compilation is not null && razorDocuments.Count > 0)
            {
                symbolIndex = await MeasureAsync(
                    "msbuild.solution.build-razor-declarations",
                    timingSink,
                    projectTimingProperties,
                    () => ProjectSymbolIndex.BuildRazorDeclarationsAsync(
                        project,
                        compilation,
                        symbolIndex,
                        razorDocuments,
                        cancellationToken));
            }

            symbolIndexes[project.Id] = symbolIndex;
            foreach ((string identity, MSBuildSymbolSnapshot symbol) in symbolIndex.SymbolsByIdentity)
            {
                solutionSymbolsByIdentity.TryAdd(identity, symbol);
            }
        }

        foreach (Microsoft.CodeAnalysis.Project project in orderedRoslynProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compilations[project.Id] is not { } compilation)
            {
                continue;
            }

            symbolIndexes[project.Id] = await MeasureAsync(
                "msbuild.solution.build-references",
                timingSink,
                CreateProjectTimingProperties(project),
                () => ProjectSymbolIndex.BuildReferencesAsync(
                    project,
                    compilation,
                    symbolIndexes[project.Id],
                    razorDocumentsByProject[project.Id],
                    solutionSymbolsByIdentity,
                    cancellationToken));
        }

        List<MSBuildProjectSnapshot> projects = [];
        foreach (Microsoft.CodeAnalysis.Project project in orderedRoslynProjects)
        {
            MSBuildEvaluatedProject evaluatedProject = evaluatedProjects[project.Id];
            ProjectSymbolIndex symbolIndex = symbolIndexes[project.Id];
            string stableProjectKey = StableIdentifier.FromParts(
                "project",
                project.FilePath ?? string.Empty,
                project.Language,
                evaluatedProject.TargetFramework,
                evaluatedProject.TargetFrameworks);

            MSBuildDocumentSnapshot[] documents = project.Documents
                .Where(IsIndexableDocument)
                .Select(document => new MSBuildDocumentSnapshot(
                    StableIdentifier.FromParts("document", stableProjectKey, document.FilePath ?? string.Empty),
                    document.Name,
                    document.FilePath ?? string.Empty,
                    document.Folders.ToArray(),
                    ComputeFileHash(document.FilePath)))
                .Concat(razorDocumentsByProject[project.Id].Select(document => new MSBuildDocumentSnapshot(
                    StableIdentifier.FromParts("document", stableProjectKey, document.FilePath),
                    Path.GetFileName(document.FilePath),
                    document.FilePath,
                    GetDocumentFolders(project.FilePath, document.FilePath),
                    ComputeFileHash(document.FilePath))))
                .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            projects.Add(new MSBuildProjectSnapshot(
                stableProjectKey,
                project.Name,
                project.FilePath ?? string.Empty,
                project.Language,
                evaluatedProject.TargetFramework,
                evaluatedProject.TargetFrameworks,
                evaluatedProject.OutputType,
                evaluatedProject.Sdk,
                evaluatedProject.AssemblyName,
                evaluatedProject.RootNamespace,
                evaluatedProject.Nullable,
                evaluatedProject.ImplicitUsings,
                evaluatedProject.LangVersion,
                documents,
                symbolIndex.Symbols,
                symbolIndex.References,
                project.ProjectReferences
                    .Select(reference => reference.ProjectId.Id.ToString())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                evaluatedProject.ProjectReferences,
                evaluatedProject.PackageReferences,
                evaluatedProject.FrameworkReferences,
                evaluatedProject.GlobalUsings,
                project.ParseOptions?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray() ?? []));
        }

        MSBuildProjectSnapshot[] orderedProjects = projects
            .OrderBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] diagnosticMessages = diagnostics
            .Select(diagnostic => diagnostic.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        return new MSBuildSolutionSnapshot(
            Path.GetFullPath(inputPath),
            orderedProjects,
            diagnosticMessages);
    }

    private static async Task<MSBuildProjectFileSnapshot> CreateProjectFileSnapshotAsync(
        string projectPath,
        Microsoft.CodeAnalysis.Project project,
        IEnumerable<WorkspaceDiagnostic> diagnostics,
        IReadOnlyList<string> normalizedFilePaths,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> existingSymbolsByIdentity,
        CancellationToken cancellationToken,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        Dictionary<string, string> timingProperties = CreateProjectFileTimingProperties(project, normalizedFilePaths);
        Compilation? compilation = await MeasureAsync(
            "msbuild.file.get-compilation-in-memory",
            timingSink,
            timingProperties,
            () => project.GetCompilationAsync(cancellationToken));
        if (compilation is null)
        {
            throw new InvalidOperationException("MSBuild did not return a compilation for project: " + projectPath);
        }

        MSBuildEvaluatedProject evaluatedProject = MSBuildEvaluatedProject.Empty;
        if (!string.IsNullOrWhiteSpace(project.FilePath) && File.Exists(project.FilePath))
        {
            evaluatedProject = Measure(
                "msbuild.file.evaluate-project",
                timingSink,
                timingProperties,
                () => MSBuildEvaluatedProject.Load(project.FilePath));
        }

        IReadOnlyList<RazorDocumentIndex> razorDocuments = RazorDocumentIndex.BuildForProject(
            project,
            evaluatedProject.RazorLikeFiles);
        ProjectSymbolIndex declarations = await MeasureAsync(
            "msbuild.file.build-declarations",
            timingSink,
            timingProperties,
            () => ProjectSymbolIndex.BuildDeclarationsAsync(
                project,
                compilation,
                cancellationToken));
        if (razorDocuments.Count > 0)
        {
            declarations = await MeasureAsync(
                "msbuild.file.build-razor-declarations",
                timingSink,
                timingProperties,
                () => ProjectSymbolIndex.BuildRazorDeclarationsAsync(
                    project,
                    compilation,
                    declarations,
                    razorDocuments,
                    cancellationToken));
        }

        Dictionary<string, MSBuildSymbolSnapshot> symbolsByIdentity = new(existingSymbolsByIdentity, StringComparer.Ordinal);
        foreach ((string identity, MSBuildSymbolSnapshot symbol) in declarations.SymbolsByIdentity)
        {
            symbolsByIdentity[identity] = symbol;
        }

        ProjectSymbolIndex index = await MeasureAsync(
            "msbuild.file.build-references",
            timingSink,
            timingProperties,
            () => ProjectSymbolIndex.BuildReferencesAsync(
                project,
                compilation,
                declarations,
                razorDocuments,
                symbolsByIdentity,
                cancellationToken));
        string stableProjectKey = StableIdentifier.FromParts(
            "project",
            project.FilePath ?? string.Empty,
            project.Language,
            evaluatedProject.TargetFramework,
            evaluatedProject.TargetFrameworks);
        MSBuildDocumentSnapshot[] documents = project.Documents
            .Where(IsIndexableDocument)
            .Select(document => new MSBuildDocumentSnapshot(
                StableIdentifier.FromParts("document", stableProjectKey, document.FilePath ?? string.Empty),
                document.Name,
                document.FilePath ?? string.Empty,
                document.Folders.ToArray(),
                ComputeFileHash(document.FilePath)))
            .Concat(razorDocuments.Select(document => new MSBuildDocumentSnapshot(
                StableIdentifier.FromParts("document", stableProjectKey, document.FilePath),
                Path.GetFileName(document.FilePath),
                document.FilePath,
                GetDocumentFolders(project.FilePath, document.FilePath),
                ComputeFileHash(document.FilePath))))
            .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] diagnosticMessages = diagnostics
            .Select(diagnostic => diagnostic.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
        return new MSBuildProjectFileSnapshot(
            Path.GetFullPath(project.FilePath ?? projectPath),
            documents,
            index.Symbols,
            index.References,
            diagnosticMessages);
    }

    private static async Task<T> MeasureAsync<T>(
        string phase,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink,
        IReadOnlyDictionary<string, string> properties,
        Func<Task<T>> action)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        T result = await action();
        stopwatch.Stop();
        timingSink?.Invoke(phase, stopwatch.ElapsedMilliseconds, properties);
        return result;
    }

    private static T Measure<T>(
        string phase,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink,
        IReadOnlyDictionary<string, string> properties,
        Func<T> action)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        T result = action();
        stopwatch.Stop();
        timingSink?.Invoke(phase, stopwatch.ElapsedMilliseconds, properties);
        return result;
    }

    private static Dictionary<string, string> CreateProjectTimingProperties(Microsoft.CodeAnalysis.Project project)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectPath"] = project.FilePath ?? string.Empty,
            ["projectName"] = project.Name
        };
    }

    private static Dictionary<string, string> CreateProjectFileTimingProperties(
        Microsoft.CodeAnalysis.Project project,
        IReadOnlyList<string> normalizedFilePaths)
    {
        Dictionary<string, string> properties = CreateProjectTimingProperties(project);
        properties["fileCount"] = normalizedFilePaths.Count.ToString();
        properties["filePaths"] = string.Join(";", normalizedFilePaths);
        return properties;
    }

    internal static bool IsIndexableDocument(Document document)
    {
        if (document.SourceCodeKind != SourceCodeKind.Regular)
        {
            return false;
        }

        string? filePath = document.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (RazorDocumentIndex.IsHybridRazorFile(filePath))
        {
            return false;
        }

        return !PathContainsIgnoredDirectory(filePath);
    }

    private static bool PathContainsIgnoredDirectory(string filePath)
    {
        foreach (string segment in filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IgnoredDocumentDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool PathHasIgnoredDirectory(string filePath)
    {
        return PathContainsIgnoredDirectory(filePath);
    }

    private static IReadOnlyList<string> GetDocumentFolders(string? projectPath, string documentPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath ?? string.Empty) ?? string.Empty;
        string relativePath = string.IsNullOrWhiteSpace(projectDirectory)
            ? Path.GetFileName(documentPath)
            : Path.GetRelativePath(projectDirectory, documentPath);
        string? relativeDirectory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrWhiteSpace(relativeDirectory)
            ? []
            : relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();
    }

    private static string ComputeFileHash(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return string.Empty;
        }

        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record MSBuildSolutionSnapshot(
    string InputPath,
    IReadOnlyList<MSBuildProjectSnapshot> Projects,
    IReadOnlyList<string> Diagnostics);

public sealed record MSBuildProjectSnapshot(
    string StableProjectKey,
    string Name,
    string ProjectPath,
    string Language,
    string TargetFramework,
    string TargetFrameworks,
    string OutputType,
    string Sdk,
    string AssemblyName,
    string RootNamespace,
    string Nullable,
    string ImplicitUsings,
    string LangVersion,
    IReadOnlyList<MSBuildDocumentSnapshot> Documents,
    IReadOnlyList<MSBuildSymbolSnapshot> Symbols,
    IReadOnlyList<MSBuildReferenceSnapshot> References,
    IReadOnlyList<string> RoslynProjectReferenceIds,
    IReadOnlyList<MSBuildProjectReferenceSnapshot> ProjectReferences,
    IReadOnlyList<MSBuildPackageReferenceSnapshot> PackageReferences,
    IReadOnlyList<MSBuildFrameworkReferenceSnapshot> FrameworkReferences,
    IReadOnlyList<MSBuildGlobalUsingSnapshot> GlobalUsings,
    IReadOnlyList<string> PreprocessorSymbols);

public sealed record MSBuildProjectFileSnapshot(
    string ProjectPath,
    IReadOnlyList<MSBuildDocumentSnapshot> Documents,
    IReadOnlyList<MSBuildSymbolSnapshot> Symbols,
    IReadOnlyList<MSBuildReferenceSnapshot> References,
    IReadOnlyList<string> Diagnostics);

public sealed record MSBuildDocumentSnapshot(
    string StableDocumentKey,
    string Name,
    string FilePath,
    IReadOnlyList<string> Folders,
    string ContentHash = "");

public sealed record MSBuildSymbolSnapshot(
    string StableKey,
    string Name,
    string Kind,
    string Namespace,
    string ContainingType,
    string FilePath,
    int StartLine,
    int EndLine,
    string Signature,
    string Accessibility = "",
    bool IsStatic = false,
    bool IsAbstract = false,
    bool IsSealed = false,
    bool IsVirtual = false,
    bool IsOverride = false,
    string MethodKind = "");

public sealed record MSBuildReferenceSnapshot(
    string TargetStableKey,
    string FilePath,
    int Line,
    int Column,
    string ReferenceKind,
    string Snippet);

public sealed record MSBuildProjectReferenceSnapshot(
    string Include,
    string FullPath);

public sealed record MSBuildPackageReferenceSnapshot(
    string Include,
    string Version);

public sealed record MSBuildFrameworkReferenceSnapshot(
    string Include);

public sealed record MSBuildGlobalUsingSnapshot(
    string Include,
    string Static,
    string Alias);

internal sealed record MSBuildEvaluatedProject(
    string TargetFramework,
    string TargetFrameworks,
    string OutputType,
    string Sdk,
    string AssemblyName,
    string RootNamespace,
    string Nullable,
    string ImplicitUsings,
    string LangVersion,
    IReadOnlyList<string> RazorLikeFiles,
    IReadOnlyList<MSBuildProjectReferenceSnapshot> ProjectReferences,
    IReadOnlyList<MSBuildPackageReferenceSnapshot> PackageReferences,
    IReadOnlyList<MSBuildFrameworkReferenceSnapshot> FrameworkReferences,
    IReadOnlyList<MSBuildGlobalUsingSnapshot> GlobalUsings)
{
    public static MSBuildEvaluatedProject Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        [],
        [],
        [],
        []);

    public static MSBuildEvaluatedProject Load(string projectPath)
    {
        ProjectCollection collection = new();
        try
        {
            MSBuildProject project = collection.LoadProject(projectPath);
            return new MSBuildEvaluatedProject(
                GetProperty(project, "TargetFramework"),
                GetProperty(project, "TargetFrameworks"),
                GetProperty(project, "OutputType"),
                project.Xml.Sdk ?? string.Empty,
                GetProperty(project, "AssemblyName"),
                GetProperty(project, "RootNamespace"),
                GetProperty(project, "Nullable"),
                GetProperty(project, "ImplicitUsings"),
                GetProperty(project, "LangVersion"),
                GetRazorLikeFiles(project, projectPath),
                GetProjectReferences(project),
                GetPackageReferences(project),
                GetFrameworkReferences(project),
                GetGlobalUsings(project));
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    private static string GetProperty(MSBuildProject project, string propertyName)
    {
        return project.GetPropertyValue(propertyName) ?? string.Empty;
    }

    private static IReadOnlyList<MSBuildProjectReferenceSnapshot> GetProjectReferences(MSBuildProject project)
    {
        return project.GetItems("ProjectReference")
            .Select(item => new MSBuildProjectReferenceSnapshot(
                item.EvaluatedInclude,
                item.GetMetadataValue("FullPath")))
            .OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> GetRazorLikeFiles(MSBuildProject project, string projectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return [];
        }

        return project.AllEvaluatedItems
            .Select(item => ResolveProjectItemPath(projectDirectory, item))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path))
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !MSBuildWorkspaceLoader.PathHasIgnoredDirectory(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveProjectItemPath(string projectDirectory, ProjectItem item)
    {
        string fullPath = item.GetMetadataValue("FullPath");
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            return fullPath;
        }

        string evaluatedInclude = item.EvaluatedInclude;
        if (string.IsNullOrWhiteSpace(evaluatedInclude))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(evaluatedInclude)
            ? evaluatedInclude
            : Path.Combine(projectDirectory, evaluatedInclude);
    }

    private static IReadOnlyList<MSBuildPackageReferenceSnapshot> GetPackageReferences(MSBuildProject project)
    {
        return project.GetItems("PackageReference")
            .Select(item => new MSBuildPackageReferenceSnapshot(
                item.EvaluatedInclude,
                item.GetMetadataValue("Version")))
            .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MSBuildFrameworkReferenceSnapshot> GetFrameworkReferences(MSBuildProject project)
    {
        return project.GetItems("FrameworkReference")
            .Select(item => new MSBuildFrameworkReferenceSnapshot(item.EvaluatedInclude))
            .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MSBuildGlobalUsingSnapshot> GetGlobalUsings(MSBuildProject project)
    {
        return project.GetItems("Using")
            .Select(item => new MSBuildGlobalUsingSnapshot(
                item.EvaluatedInclude,
                item.GetMetadataValue("Static"),
                item.GetMetadataValue("Alias")))
            .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class ProjectSymbolIndex
{
    private ProjectSymbolIndex(
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        IReadOnlyList<MSBuildReferenceSnapshot> references,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> symbolsByIdentity,
        IReadOnlyDictionary<string, ISymbol> declaredSymbolsByIdentity)
    {
        Symbols = symbols;
        References = references;
        SymbolsByIdentity = symbolsByIdentity;
        DeclaredSymbolsByIdentity = declaredSymbolsByIdentity;
    }

    public static ProjectSymbolIndex Empty { get; } = new(
        [],
        [],
        new Dictionary<string, MSBuildSymbolSnapshot>(),
        new Dictionary<string, ISymbol>());

    public IReadOnlyList<MSBuildSymbolSnapshot> Symbols { get; }

    public IReadOnlyList<MSBuildReferenceSnapshot> References { get; }

    public IReadOnlyDictionary<string, MSBuildSymbolSnapshot> SymbolsByIdentity { get; }

    public IReadOnlyDictionary<string, ISymbol> DeclaredSymbolsByIdentity { get; }

    public static async Task<ProjectSymbolIndex> BuildDeclarationsAsync(
        Microsoft.CodeAnalysis.Project project,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        Dictionary<ISymbol, MSBuildSymbolSnapshot> declared = new(SymbolEqualityComparer.Default);
        Dictionary<string, MSBuildSymbolSnapshot> declaredByIdentity = new(StringComparer.Ordinal);
        Dictionary<string, ISymbol> declaredSymbolsByIdentity = new(StringComparer.Ordinal);
        List<MSBuildSymbolSnapshot> symbolSnapshots = [];
        HashSet<string> stableSymbolKeys = new(StringComparer.Ordinal);
        foreach (Document document in GetIndexableDocuments(project, includedFilePaths: null))
        {
            SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null)
            {
                continue;
            }

            SemanticModel model = compilation.GetSemanticModel(tree);
            SyntaxNode root = await tree.GetRootAsync(cancellationToken);
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                foreach (ISymbol symbol in GetDeclaredSymbols(model, node, cancellationToken))
                {
                    MSBuildSymbolSnapshot snapshot = CreateSymbolSnapshot(symbol, node, document.FilePath ?? string.Empty, tree);
                    if (stableSymbolKeys.Add(snapshot.StableKey))
                    {
                        symbolSnapshots.Add(snapshot);
                    }

                    if (!declared.ContainsKey(symbol))
                    {
                        declared[symbol] = snapshot;
                        string identity = CreateSymbolIdentity(symbol);
                        declaredByIdentity.TryAdd(identity, snapshot);
                        declaredSymbolsByIdentity.TryAdd(identity, symbol);
                    }
                }
            }
        }

        return new ProjectSymbolIndex(
            symbolSnapshots.OrderBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            [],
            declaredByIdentity,
            declaredSymbolsByIdentity);
    }

    public static async Task<ProjectSymbolIndex> BuildRazorDeclarationsAsync(
        Microsoft.CodeAnalysis.Project project,
        Compilation compilation,
        ProjectSymbolIndex declarations,
        IReadOnlyList<RazorDocumentIndex> razorDocuments,
        CancellationToken cancellationToken)
    {
        if (razorDocuments.Count == 0)
        {
            return declarations;
        }

        List<MSBuildSymbolSnapshot> symbolSnapshots = declarations.Symbols.ToList();
        Dictionary<string, MSBuildSymbolSnapshot> declaredByIdentity = new(declarations.SymbolsByIdentity, StringComparer.Ordinal);
        Dictionary<string, ISymbol> declaredSymbolsByIdentity = new(declarations.DeclaredSymbolsByIdentity, StringComparer.Ordinal);
        HashSet<string> stableSymbolKeys = symbolSnapshots.Select(symbol => symbol.StableKey).ToHashSet(StringComparer.Ordinal);

        Compilation razorCompilation = compilation.AddSyntaxTrees(razorDocuments.Select(document => document.GeneratedTree));
        foreach (RazorDocumentIndex razorDocument in razorDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticModel model = razorCompilation.GetSemanticModel(razorDocument.GeneratedTree);
            SyntaxNode root = await razorDocument.GeneratedTree.GetRootAsync(cancellationToken);
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                foreach (ISymbol symbol in GetDeclaredSymbols(model, node, cancellationToken))
                {
                    if (!razorDocument.TryMapGeneratedSpan(node.Span, out FileLinePositionSpan originalSpan))
                    {
                        continue;
                    }

                    MSBuildSymbolSnapshot snapshot = CreateMappedSymbolSnapshot(
                        symbol,
                        razorDocument.FilePath,
                        originalSpan);
                    if (stableSymbolKeys.Add(snapshot.StableKey))
                    {
                        symbolSnapshots.Add(snapshot);
                    }

                    string identity = CreateSymbolIdentity(symbol);
                    declaredByIdentity.TryAdd(identity, snapshot);
                    declaredSymbolsByIdentity.TryAdd(identity, symbol);
                }
            }
        }

        return new ProjectSymbolIndex(
            symbolSnapshots.OrderBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            declarations.References,
            declaredByIdentity,
            declaredSymbolsByIdentity);
    }

    public static async Task<ProjectSymbolIndex> BuildReferencesAsync(
        Microsoft.CodeAnalysis.Project project,
        Compilation compilation,
        ProjectSymbolIndex declarations,
        IReadOnlyList<RazorDocumentIndex> razorDocuments,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity,
        CancellationToken cancellationToken)
    {
        List<MSBuildReferenceSnapshot> references = [];
        HashSet<string> referenceIdentities = new(StringComparer.Ordinal);
        AddRelationshipReferences(declarations, solutionSymbolsByIdentity, references, referenceIdentities);
        foreach (Document document in project.Documents.Where(MSBuildWorkspaceLoader.IsIndexableDocument))
        {
            SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null)
            {
                continue;
            }

            SemanticModel model = compilation.GetSemanticModel(tree);
            SyntaxNode root = await tree.GetRootAsync(cancellationToken);
            foreach (SyntaxNode node in root.DescendantNodes().Where(node => IsReferenceCandidate(node) && !IsNestedDuplicateReferenceCandidate(node)))
            {
                ISymbol? target = GetReferencedSymbol(model, node, cancellationToken);
                if (target is null || !TryGetSourceSymbol(target, solutionSymbolsByIdentity, out MSBuildSymbolSnapshot? targetSnapshot))
                {
                    continue;
                }

                FileLinePositionSpan span = tree.GetLineSpan(GetReferenceSpan(node), cancellationToken);
                AddReference(
                    references,
                    referenceIdentities,
                    targetSnapshot!,
                    document.FilePath ?? string.Empty,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    GetReferenceKind(node, target),
                    node.ToString());
            }
        }

        AddRazorReferences(
            compilation,
            razorDocuments,
            solutionSymbolsByIdentity,
            references,
            referenceIdentities,
            cancellationToken);
        if (razorDocuments.Count > 0)
        {
            await AddSourceGeneratedRazorReferences(
                project,
                compilation,
                solutionSymbolsByIdentity,
                references,
                referenceIdentities,
                cancellationToken);
        }

        return new ProjectSymbolIndex(
            declarations.Symbols,
            references.OrderBy(reference => reference.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.Line)
                .ThenBy(reference => reference.Column)
                .ToArray(),
            declarations.SymbolsByIdentity,
            declarations.DeclaredSymbolsByIdentity);
    }

    private static IEnumerable<Document> GetIndexableDocuments(
        Microsoft.CodeAnalysis.Project project,
        IReadOnlySet<string>? includedFilePaths)
    {
        IEnumerable<Document> documents = project.Documents.Where(MSBuildWorkspaceLoader.IsIndexableDocument);
        if (includedFilePaths is null)
        {
            return documents;
        }

        return documents.Where(document =>
            !string.IsNullOrWhiteSpace(document.FilePath)
            && includedFilePaths.Contains(Path.GetFullPath(document.FilePath)));
    }

    private static void AddRazorReferences(
        Compilation compilation,
        IReadOnlyList<RazorDocumentIndex> razorDocuments,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity,
        List<MSBuildReferenceSnapshot> references,
        HashSet<string> referenceIdentities,
        CancellationToken cancellationToken)
    {
        if (razorDocuments.Count == 0)
        {
            return;
        }

        Compilation razorCompilation = compilation.AddSyntaxTrees(razorDocuments.Select(document => document.GeneratedTree));
        foreach (RazorDocumentIndex razorDocument in razorDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticModel model = razorCompilation.GetSemanticModel(razorDocument.GeneratedTree);
            SyntaxNode root = razorDocument.GeneratedTree.GetRoot(cancellationToken);
            foreach (SyntaxNode node in root.DescendantNodes().Where(node => IsReferenceCandidate(node) && !IsNestedDuplicateReferenceCandidate(node)))
            {
                TextSpan referenceSpan = GetReferenceSpan(node);
                if (!razorDocument.TryMapGeneratedSpan(referenceSpan, out FileLinePositionSpan originalSpan))
                {
                    continue;
                }

                ISymbol? target = GetReferencedSymbol(model, node, cancellationToken);
                if (target is null || !TryGetSourceSymbol(target, solutionSymbolsByIdentity, out MSBuildSymbolSnapshot? targetSnapshot))
                {
                    continue;
                }

                AddReference(
                    references,
                    referenceIdentities,
                    targetSnapshot!,
                    razorDocument.FilePath,
                    originalSpan.StartLinePosition.Line + 1,
                    originalSpan.StartLinePosition.Character + 1,
                    $"razor:{node.Kind()}",
                    razorDocument.GetOriginalSnippet(originalSpan.StartLinePosition.Line));
            }
        }
    }

    private static async Task AddSourceGeneratedRazorReferences(
        Microsoft.CodeAnalysis.Project project,
        Compilation compilation,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity,
        List<MSBuildReferenceSnapshot> references,
        HashSet<string> referenceIdentities,
        CancellationToken cancellationToken)
    {
        IEnumerable<SourceGeneratedDocument> generatedDocuments =
            await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
        List<SyntaxTree> generatedRazorTrees = [];
        HashSet<string> generatedRazorTreePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceGeneratedDocument document in generatedDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is not null
                && IsSourceGeneratedRazorTree(tree)
                && generatedRazorTreePaths.Add(tree.FilePath))
            {
                generatedRazorTrees.Add(tree);
            }
        }

        if (generatedRazorTrees.Count == 0)
        {
            return;
        }

        HashSet<string> existingTreePaths = compilation.SyntaxTrees
            .Where(tree => !string.IsNullOrWhiteSpace(tree.FilePath))
            .Select(tree => tree.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SyntaxTree[] missingTrees = generatedRazorTrees
            .Where(tree => !existingTreePaths.Contains(tree.FilePath))
            .ToArray();
        Compilation generatedCompilation = missingTrees.Length == 0
            ? compilation
            : compilation.AddSyntaxTrees(missingTrees);
        foreach (SyntaxTree tree in generatedRazorTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticModel model = generatedCompilation.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot(cancellationToken);
            foreach (SyntaxNode node in root.DescendantNodes().Where(node => IsReferenceCandidate(node) && !IsNestedDuplicateReferenceCandidate(node)))
            {
                TextSpan referenceSpan = GetReferenceSpan(node);
                FileLinePositionSpan mappedSpan = tree.GetMappedLineSpan(referenceSpan, cancellationToken);
                if (!IsUserRazorMappedSpan(mappedSpan))
                {
                    continue;
                }

                ISymbol? target = GetReferencedSymbol(model, node, cancellationToken);
                if (target is null || !TryGetSourceSymbol(target, solutionSymbolsByIdentity, out MSBuildSymbolSnapshot? targetSnapshot))
                {
                    continue;
                }

                string referenceKindPrefix = IsRazorCodeBlockMappedLine(mappedSpan.Path, mappedSpan.StartLinePosition.Line)
                    ? "razor"
                    : "razor-generated";

                AddReference(
                    references,
                    referenceIdentities,
                    targetSnapshot!,
                    mappedSpan.Path,
                    mappedSpan.StartLinePosition.Line + 1,
                    mappedSpan.StartLinePosition.Character + 1,
                    $"{referenceKindPrefix}:{node.Kind()}",
                    GetFileLineSnippet(mappedSpan.Path, mappedSpan.StartLinePosition.Line));
            }
        }
    }

    private static void AddRelationshipReferences(
        ProjectSymbolIndex declarations,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity,
        List<MSBuildReferenceSnapshot> references,
        HashSet<string> referenceIdentities)
    {
        foreach ((string identity, ISymbol symbol) in declarations.DeclaredSymbolsByIdentity)
        {
            if (!declarations.SymbolsByIdentity.TryGetValue(identity, out MSBuildSymbolSnapshot? sourceSnapshot))
            {
                continue;
            }

            if (symbol is not IMethodSymbol method)
            {
                continue;
            }

            foreach (IMethodSymbol explicitInterfaceMember in method.ExplicitInterfaceImplementations)
            {
                AddRelationshipReference("implements_interface_member", method, explicitInterfaceMember, sourceSnapshot);
            }

            if (method.OverriddenMethod is not null)
            {
                AddRelationshipReference("overrides", method, method.OverriddenMethod, sourceSnapshot);
            }

            if (method.ContainingType is not null)
            {
                foreach (INamedTypeSymbol interfaceType in method.ContainingType.AllInterfaces)
                {
                    foreach (ISymbol interfaceMember in interfaceType.GetMembers(method.Name))
                    {
                        ISymbol? implementation = method.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
                        if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, method))
                        {
                            AddRelationshipReference("implements_interface_member", method, interfaceMember, sourceSnapshot);
                        }
                    }
                }
            }
        }

        foreach (List<MSBuildSymbolSnapshot> partialSnapshots in declarations.Symbols
            .Where(symbol => symbol.Kind.Equals("NamedType", StringComparison.Ordinal))
            .GroupBy(symbol => symbol.Signature, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToList()))
        {
            foreach (MSBuildSymbolSnapshot targetPartial in partialSnapshots)
            {
                foreach (MSBuildSymbolSnapshot sourcePartial in partialSnapshots.Where(partial => partial.StableKey != targetPartial.StableKey))
                {
                    AddReference(
                        references,
                        referenceIdentities,
                        targetPartial,
                        sourcePartial.FilePath,
                        sourcePartial.StartLine,
                        1,
                        "partial_declaration",
                        sourcePartial.Name);
                }
            }
        }

        void AddRelationshipReference(
            string relationshipKind,
            ISymbol sourceSymbol,
            ISymbol targetSymbol,
            MSBuildSymbolSnapshot sourceSnapshot)
        {
            string targetIdentity = CreateSymbolIdentity(targetSymbol);
            if (!solutionSymbolsByIdentity.TryGetValue(targetIdentity, out MSBuildSymbolSnapshot? targetSnapshot))
            {
                return;
            }

            Location? location = sourceSymbol.Locations.FirstOrDefault(location => location.IsInSource);
            FileLinePositionSpan span = location?.GetLineSpan() ?? default;
            AddReference(
                references,
                referenceIdentities,
                targetSnapshot,
                sourceSnapshot.FilePath,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                relationshipKind,
                sourceSnapshot.Signature);
        }
    }

    private static string GetReferenceKind(SyntaxNode node, ISymbol target)
    {
        if (target is INamedTypeSymbol
            && node.Ancestors().Any(ancestor => ancestor is BaseListSyntax))
        {
            return "inherits_from";
        }

        return node.Kind().ToString();
    }

    private static void AddReference(
        List<MSBuildReferenceSnapshot> references,
        HashSet<string> referenceIdentities,
        MSBuildSymbolSnapshot targetSnapshot,
        string filePath,
        int line,
        int column,
        string referenceKind,
        string snippet)
    {
        string referenceIdentity = StableIdentifier.FromParts(
            "reference",
            targetSnapshot.StableKey,
            filePath,
            line.ToString(),
            column.ToString());
        if (!referenceIdentities.Add(referenceIdentity))
        {
            return;
        }

        references.Add(new MSBuildReferenceSnapshot(
            targetSnapshot.StableKey,
            filePath,
            line,
            column,
            referenceKind,
            snippet));
    }

    private static IEnumerable<ISymbol> GetDeclaredSymbols(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        ISymbol? declaredSymbol = node switch
        {
            BaseTypeDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            ConversionOperatorDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            OperatorDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            BaseMethodDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            PropertyDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            IndexerDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            EventDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            EnumMemberDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            LocalFunctionStatementSyntax declaration => model.GetDeclaredSymbol(declaration, cancellationToken),
            EventFieldDeclarationSyntax declaration => declaration.Declaration.Variables.Count == 1
                ? model.GetDeclaredSymbol(declaration.Declaration.Variables[0], cancellationToken)
                : null,
            FieldDeclarationSyntax declaration => declaration.Declaration.Variables.Count == 1
                ? model.GetDeclaredSymbol(declaration.Declaration.Variables[0], cancellationToken)
                : null,
            _ => null
        };

        if (declaredSymbol is not null)
        {
            yield return declaredSymbol;
        }

        if (node is TypeDeclarationSyntax { ParameterList: not null } typeDeclaration
            && model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is INamedTypeSymbol typeSymbol)
        {
            int parameterCount = typeDeclaration.ParameterList.Parameters.Count;
            foreach (IMethodSymbol constructor in typeSymbol.InstanceConstructors)
            {
                bool hasSourceLocation = constructor.Locations.Any(location =>
                    location.IsInSource
                    && location.SourceTree == typeDeclaration.SyntaxTree
                    && typeDeclaration.Span.Contains(location.SourceSpan.Start));
                if (constructor.MethodKind == MethodKind.Constructor
                    && constructor.Parameters.Length == parameterCount
                    && hasSourceLocation)
                {
                    yield return constructor;
                }
            }
        }
    }

    private static bool IsReferenceCandidate(SyntaxNode node)
    {
        return node is IdentifierNameSyntax
            or GenericNameSyntax
            or AttributeSyntax
            or InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax
            or ElementAccessExpressionSyntax
            or BinaryExpressionSyntax
            or CastExpressionSyntax
            or ThisExpressionSyntax
            or LocalDeclarationStatementSyntax;
    }

    private static ISymbol? GetReferencedSymbol(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        if (node is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } localDeclaration)
        {
            TypeInfo typeInfo = model.GetTypeInfo(localDeclaration.Declaration.Type, cancellationToken);
            return typeInfo.Type?
                .GetMembers("DisposeAsync")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.Parameters.Length == 0);
        }

        if (node is AttributeSyntax attribute)
        {
            SymbolInfo attributeSymbolInfo = model.GetSymbolInfo(attribute, cancellationToken);
            ISymbol? attributeSymbol = attributeSymbolInfo.Symbol ?? attributeSymbolInfo.CandidateSymbols.FirstOrDefault();
            return attributeSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } attributeConstructor
                ? attributeConstructor.ContainingType
                : attributeSymbol;
        }

        if (node is BinaryExpressionSyntax binaryExpression)
        {
            return model.GetSymbolInfo(binaryExpression, cancellationToken).Symbol;
        }

        if (node is ElementAccessExpressionSyntax elementAccessExpression)
        {
            return model.GetSymbolInfo(elementAccessExpression, cancellationToken).Symbol;
        }

        if (node is InvocationExpressionSyntax invocationExpression)
        {
            SymbolInfo invocationSymbolInfo = model.GetSymbolInfo(invocationExpression, cancellationToken);
            ISymbol? invocationSymbol = invocationSymbolInfo.Symbol ?? invocationSymbolInfo.CandidateSymbols.FirstOrDefault();
            if (invocationSymbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } invocationReducedExtension
                && invocationReducedExtension.ReducedFrom is not null)
            {
                return invocationReducedExtension.ReducedFrom;
            }

            return invocationSymbol;
        }

        if (node is ObjectCreationExpressionSyntax objectCreation)
        {
            return GetConstructionTarget(model.GetSymbolInfo(objectCreation, cancellationToken));
        }

        if (node is ImplicitObjectCreationExpressionSyntax implicitObjectCreation)
        {
            return GetConstructionTarget(model.GetSymbolInfo(implicitObjectCreation, cancellationToken));
        }

        if (node is CastExpressionSyntax castExpression)
        {
            return model.GetConversion(castExpression.Expression).MethodSymbol;
        }

        if (node is ThisExpressionSyntax thisExpression)
        {
            return model.GetConversion(thisExpression).MethodSymbol;
        }

        SymbolInfo symbolInfo = model.GetSymbolInfo(node, cancellationToken);
        ISymbol? symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        if (symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reducedExtension
            && reducedExtension.ReducedFrom is not null)
        {
            return reducedExtension.ReducedFrom;
        }

        return symbol;
    }

    private static ISymbol? GetConstructionTarget(SymbolInfo symbolInfo)
    {
        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    private static bool IsNestedDuplicateReferenceCandidate(SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax or GenericNameSyntax
                when node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } objectCreation
                    && objectCreation.Type.Span.Contains(node.Span) => true,
            IdentifierNameSyntax or GenericNameSyntax
                when node.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute
                    && attribute.Name.Span.Contains(node.Span) => true,
            ThisExpressionSyntax
                when node.Parent is ElementAccessExpressionSyntax elementAccess
                    && elementAccess.Expression.Span.Contains(node.Span) => true,
            _ => false
        };
    }

    private static TextSpan GetReferenceSpan(SyntaxNode node)
    {
        return node switch
        {
            AttributeSyntax attribute => attribute.Name.Span,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => memberAccess.Name.Span,
            InvocationExpressionSyntax invocation => invocation.Expression.Span,
            ObjectCreationExpressionSyntax objectCreation => objectCreation.Type.Span,
            ImplicitObjectCreationExpressionSyntax implicitObjectCreation => implicitObjectCreation.NewKeyword.Span,
            ElementAccessExpressionSyntax elementAccess => elementAccess.Expression.Span,
            BinaryExpressionSyntax binaryExpression => binaryExpression.OperatorToken.Span,
            CastExpressionSyntax castExpression => castExpression.Expression.Span,
            LocalDeclarationStatementSyntax localDeclaration => localDeclaration.AwaitKeyword.Span,
            _ => node.Span
        };
    }

    private static bool IsSourceGeneratedRazorTree(SyntaxTree tree)
    {
        return tree.FilePath.Contains("RazorSourceGenerator", StringComparison.OrdinalIgnoreCase)
            && tree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserRazorMappedSpan(FileLinePositionSpan mappedSpan)
    {
        return !string.IsNullOrWhiteSpace(mappedSpan.Path)
            && (mappedSpan.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || mappedSpan.Path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
            && !MSBuildWorkspaceLoader.PathHasIgnoredDirectory(mappedSpan.Path);
    }

    private static bool IsRazorCodeBlockMappedLine(string filePath, int zeroBasedLine)
    {
        if (filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
        {
            return false;
        }

        if (zeroBasedLine < 0 || zeroBasedLine >= lines.Length)
        {
            return false;
        }

        bool inCodeBlock = false;
        int braceDepth = 0;
        for (int lineIndex = 0; lineIndex <= zeroBasedLine; lineIndex++)
        {
            string line = lines[lineIndex];
            int scanStart = 0;
            if (!inCodeBlock)
            {
                int codeIndex = line.IndexOf("@code", StringComparison.Ordinal);
                if (codeIndex < 0)
                {
                    continue;
                }

                inCodeBlock = true;
                scanStart = codeIndex + "@code".Length;
            }

            for (int charIndex = scanStart; charIndex < line.Length; charIndex++)
            {
                if (line[charIndex] == '{')
                {
                    braceDepth++;
                }
                else if (line[charIndex] == '}')
                {
                    braceDepth--;
                    if (braceDepth <= 0)
                    {
                        if (lineIndex == zeroBasedLine)
                        {
                            return true;
                        }

                        inCodeBlock = false;
                        braceDepth = 0;
                        break;
                    }
                }
            }

            if (lineIndex == zeroBasedLine)
            {
                return inCodeBlock;
            }
        }

        return false;
    }

    private static string GetFileLineSnippet(string filePath, int zeroBasedLine)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        try
        {
            using StreamReader reader = File.OpenText(filePath);
            for (int line = 0; line <= zeroBasedLine; line++)
            {
                string? text = reader.ReadLine();
                if (text is null)
                {
                    return string.Empty;
                }

                if (line == zeroBasedLine)
                {
                    return text.Trim();
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static bool TryGetSourceSymbol(
        ISymbol symbol,
        IReadOnlyDictionary<string, MSBuildSymbolSnapshot> solutionSymbolsByIdentity,
        out MSBuildSymbolSnapshot? targetSnapshot)
    {
        string identity = CreateSymbolIdentity(symbol);
        if (solutionSymbolsByIdentity.TryGetValue(identity, out targetSnapshot))
        {
            return true;
        }

        if (symbol.OriginalDefinition is not null
            && !SymbolEqualityComparer.Default.Equals(symbol, symbol.OriginalDefinition)
            && solutionSymbolsByIdentity.TryGetValue(CreateSymbolIdentity(symbol.OriginalDefinition), out targetSnapshot))
        {
            return true;
        }

        targetSnapshot = null;
        return false;
    }

    private static string CreateSymbolIdentity(ISymbol symbol)
    {
        Location? sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        FileLinePositionSpan mappedSpan = sourceLocation?.GetMappedLineSpan() ?? default;
        string filePath = !string.IsNullOrWhiteSpace(mappedSpan.Path)
            ? mappedSpan.Path
            : sourceLocation?.SourceTree?.FilePath ?? string.Empty;
        string startLine = sourceLocation is null
            ? string.Empty
            : (mappedSpan.StartLinePosition.Line + 1).ToString();
        string signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return StableIdentifier.FromParts("symbol-identity", filePath, symbol.Kind.ToString(), signature, startLine);
    }

    private static MSBuildSymbolSnapshot CreateSymbolSnapshot(
        ISymbol symbol,
        SyntaxNode node,
        string filePath,
        SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(node.Span);
        string containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty;
        string namespaceName = symbol.ContainingNamespace?.IsGlobalNamespace == false
            ? symbol.ContainingNamespace.ToDisplayString()
            : string.Empty;
        string signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        string stableKey = StableIdentifier.FromParts("symbol", filePath, symbol.Kind.ToString(), signature, (span.StartLinePosition.Line + 1).ToString());

        return new MSBuildSymbolSnapshot(
            stableKey,
            symbol.Name,
            symbol.Kind.ToString(),
            namespaceName,
            containingType,
            filePath,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            signature,
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic,
            symbol.IsAbstract,
            symbol.IsSealed,
            symbol.IsVirtual,
            symbol.IsOverride,
            GetMethodKind(symbol));
    }

    private static MSBuildSymbolSnapshot CreateMappedSymbolSnapshot(
        ISymbol symbol,
        string filePath,
        FileLinePositionSpan span)
    {
        string containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty;
        string namespaceName = symbol.ContainingNamespace?.IsGlobalNamespace == false
            ? symbol.ContainingNamespace.ToDisplayString()
            : string.Empty;
        string signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        string stableKey = StableIdentifier.FromParts("symbol", filePath, symbol.Kind.ToString(), signature, (span.StartLinePosition.Line + 1).ToString());

        return new MSBuildSymbolSnapshot(
            stableKey,
            symbol.Name,
            symbol.Kind.ToString(),
            namespaceName,
            containingType,
            filePath,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            signature,
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic,
            symbol.IsAbstract,
            symbol.IsSealed,
            symbol.IsVirtual,
            symbol.IsOverride,
            GetMethodKind(symbol));
    }

    private static string GetMethodKind(ISymbol symbol)
    {
        return symbol is IMethodSymbol method
            ? method.MethodKind.ToString()
            : string.Empty;
    }
}

internal sealed class RazorDocumentIndex
{
    private RazorDocumentIndex(
        string filePath,
        SourceText sourceText,
        SyntaxTree generatedTree,
        IReadOnlyList<SourceMapping> mappings)
    {
        FilePath = filePath;
        SourceText = sourceText;
        GeneratedTree = generatedTree;
        Mappings = mappings;
    }

    public string FilePath { get; }

    public SourceText SourceText { get; }

    public SyntaxTree GeneratedTree { get; }

    private IReadOnlyList<SourceMapping> Mappings { get; }

    public static IReadOnlyList<RazorDocumentIndex> BuildForProject(
        Microsoft.CodeAnalysis.Project project,
        IReadOnlyList<string> razorLikeFiles)
    {
        string? projectDirectory = Path.GetDirectoryName(project.FilePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return [];
        }

        RazorProjectFileSystem fileSystem = RazorProjectFileSystem.Create(projectDirectory);
        string rootNamespace = project.DefaultNamespace
            ?? project.AssemblyName
            ?? Path.GetFileNameWithoutExtension(project.FilePath ?? string.Empty)
            ?? string.Empty;
        RazorProjectEngine engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            builder => builder.SetRootNamespace(rootNamespace));

        List<RazorDocumentIndex> documents = [];
        CSharpParseOptions parseOptions = project.ParseOptions as CSharpParseOptions
            ?? CSharpParseOptions.Default;
        foreach (string filePath in EnumerateRazorLikeFiles(razorLikeFiles))
        {
            if (TryCreate(projectDirectory, filePath, fileSystem, engine, parseOptions, out RazorDocumentIndex? document)
                && document is not null)
            {
                documents.Add(document);
            }
        }

        return documents
            .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsHybridRazorFile(string filePath)
    {
        if (!filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return false;
        }

        return LooksLikeHybridRazor(File.ReadAllText(filePath));
    }

    public bool TryMapGeneratedSpan(TextSpan generatedSpan, out FileLinePositionSpan originalSpan)
    {
        foreach (SourceMapping mapping in Mappings)
        {
            int generatedStart = mapping.GeneratedSpan.AbsoluteIndex;
            int generatedEnd = generatedStart + mapping.GeneratedSpan.Length;
            if (generatedSpan.Start < generatedStart || generatedSpan.Start >= generatedEnd)
            {
                continue;
            }

            int offset = generatedSpan.Start - generatedStart;
            int originalStart = mapping.OriginalSpan.AbsoluteIndex + offset;
            int originalLength = Math.Min(generatedSpan.Length, mapping.GeneratedSpan.Length - offset);
            if (originalStart < 0 || originalStart >= SourceText.Length)
            {
                break;
            }

            int originalEnd = Math.Min(originalStart + Math.Max(originalLength, 1), SourceText.Length);
            LinePosition start = SourceText.Lines.GetLinePosition(originalStart);
            LinePosition end = SourceText.Lines.GetLinePosition(originalEnd);
            originalSpan = new FileLinePositionSpan(FilePath, start, end);
            return true;
        }

        originalSpan = default;
        return false;
    }

    public string GetOriginalSnippet(int zeroBasedLine)
    {
        if (zeroBasedLine < 0 || zeroBasedLine >= SourceText.Lines.Count)
        {
            return string.Empty;
        }

        return SourceText.Lines[zeroBasedLine].ToString().Trim();
    }

    private static IEnumerable<string> EnumerateRazorLikeFiles(IReadOnlyList<string> razorLikeFiles)
    {
        foreach (string filePath in razorLikeFiles)
        {
            if (MSBuildWorkspaceLoader.PathHasIgnoredDirectory(filePath))
            {
                continue;
            }

            if (filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || IsHybridRazorFile(filePath))
            {
                yield return filePath;
            }
        }
    }

    private static bool TryCreate(
        string projectDirectory,
        string filePath,
        RazorProjectFileSystem fileSystem,
        RazorProjectEngine engine,
        CSharpParseOptions parseOptions,
        out RazorDocumentIndex? document)
    {
        try
        {
            string text = File.ReadAllText(filePath);
            if (!LooksLikeRazor(filePath, text))
            {
                document = null;
                return false;
            }

            string relativePath = "/" + Path.GetRelativePath(projectDirectory, filePath).Replace('\\', '/');
            RazorProjectItem projectItem = fileSystem.GetItem(relativePath, FileKinds.Component);
            RazorCodeDocument codeDocument = engine.Process(projectItem);
            RazorCSharpDocument csharpDocument = codeDocument.GetCSharpDocument();
            if (filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)
                && csharpDocument.SourceMappings.Count == 0)
            {
                document = null;
                return false;
            }

            SourceText sourceText = SourceText.From(text);
            SyntaxTree generatedTree = CSharpSyntaxTree.ParseText(
                csharpDocument.GeneratedCode,
                parseOptions,
                path: filePath);
            document = new RazorDocumentIndex(
                filePath,
                sourceText,
                generatedTree,
                csharpDocument.SourceMappings);
            return true;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    private static bool LooksLikeRazor(string filePath, string text)
    {
        if (filePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
        {
            return LooksLikeHybridRazor(text);
        }

        return filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHybridRazor(string text)
    {
        return text.Contains("@code", StringComparison.Ordinal)
            || text.Contains("@page", StringComparison.Ordinal)
            || text.Contains("@using", StringComparison.Ordinal)
            || text.Contains("@inherits", StringComparison.Ordinal)
            || text.Contains("@inject", StringComparison.Ordinal)
            || text
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line => line.TrimStart())
                .Any(line => line.StartsWith("<", StringComparison.Ordinal)
                    && !line.StartsWith("///", StringComparison.Ordinal)
                    && !line.StartsWith("<!--", StringComparison.Ordinal));
    }
}
