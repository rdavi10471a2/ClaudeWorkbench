using System.Diagnostics;
using AIMonitor.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AIMonitor.MSBuild;

// ADR-0007 convergence: build the index's solution snapshot from a real build's OUTPUTS instead of an
// MSBuildWorkspace evaluation. It assembles an in-memory Roslyn project (AdhocWorkspace) from the hand-written
// source + the MSBuild-generated helpers (GlobalUsings / AssemblyInfo) + the build's resolved reference set,
// then reuses the EXISTING extraction (MSBuildWorkspaceLoader.CreateSnapshotAsync) with razor documents sourced
// from the build's accurate generated .g.cs (RazorDocumentIndex.BuildFromGeneratedFiles). No MSBuildWorkspace,
// no BuildHost, no standalone razor engine. Side-by-side with the existing loader; nothing here is wired live.
//
// This is the "assemble from build inputs" core — orchestrating the build + reference harvest is a separate
// concern (a later increment); this turns already-produced outputs into a snapshot so it is deterministically
// testable.
public sealed class BuildOutputSnapshotLoader
{

    // ADR-0007 whole-solution read: one incremental `dotnet build` of the solution emits every project's
    // generated .g.cs and dumps each project's resolved references into its OWN obj (no clobber), then assembles
    // ALL projects into one Roslyn Solution — wired with ProjectReferences so cross-project symbols resolve to
    // source — and snapshots them together. Reuses the exact single-project machinery (BuildFromGeneratedFiles,
    // CreateSnapshotAsync) per project; the build is one pass for N projects (MSBuild skips up-to-date ones).
    public async Task<BuildOutputSnapshotResult> OpenSolutionFromBuildAsync(
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        string fullSolutionPath = Path.GetFullPath(solutionPath);
        string solutionDirectory = Path.GetDirectoryName(fullSolutionPath) ?? string.Empty;

        // One build for all N projects: emit every project's generated .g.cs and dump each project's refs into
        // its own obj (the shared Core target — AfterTargets=ResolveReferences so it runs even when a project is
        // up-to-date). Then READ that output. The accept path runs the equivalent build itself (build-after-accept)
        // and calls ReadSolutionSnapshotAsync directly, so there is one read path for 1..N projects.
        string targetsFile = IndexRidesBuild.WritePerProjectRefsTargetsFile();
        try
        {
            (int exitCode, string output) = RunDotnet(
                [
                    "build", fullSolutionPath, "-c", configuration, "--nologo", "-v:quiet", "-nodeReuse:false",
                    "-p:EmitCompilerGeneratedFiles=true",
                    $"-p:CustomAfterMicrosoftCommonTargets={targetsFile}"
                ],
                solutionDirectory);

            MSBuildSolutionSnapshot snapshot = await ReadSolutionSnapshotAsync(
                fullSolutionPath, projectPaths, configuration, cancellationToken);
            return new BuildOutputSnapshotResult(snapshot, exitCode == 0, exitCode, output);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(targetsFile)!, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Read an ALREADY-built solution's output into a snapshot — NO build. The build-after-accept (or a prior
    // OpenSolutionFromBuildAsync) already emitted every project's generated .g.cs + per-project refs; this
    // gathers those from disk for all N projects and assembles the one multi-project snapshot. One read path
    // for 1..N projects — no single-project and no single-file special case.
    public async Task<MSBuildSolutionSnapshot> ReadSolutionSnapshotAsync(
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        string solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty;
        List<ProjectBuildInputs> inputs = [];
        foreach (string projectPath in projectPaths)
        {
            string fullProjectPath = Path.GetFullPath(projectPath);
            string projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? solutionDirectory;
            inputs.Add(new ProjectBuildInputs(
                fullProjectPath,
                FindGeneratedRoot(projectDirectory, configuration),
                ReadPerProjectReferences(projectDirectory, configuration),
                ParseProjectReferences(fullProjectPath)));
        }

        return await BuildSolutionSnapshotAsync(inputs, cancellationToken);
    }

    // Assemble ALL projects into one AdhocWorkspace Solution from their build outputs, then snapshot together.
    // Each project becomes a ProjectInfo (its source + refs) with ProjectReferences to the others it depends on,
    // and its razor documents come from its own generated .g.cs. CreateSnapshotAsync already loops every project
    // in the Solution and resolves references across the whole set, so cross-project symbols land correctly.
    public async Task<MSBuildSolutionSnapshot> BuildSolutionSnapshotAsync(
        IReadOnlyList<ProjectBuildInputs> inputs,
        CancellationToken cancellationToken = default)
    {
        MSBuildWorkspaceLoader.EnsureMSBuildRegistered();
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);

        // Assign every project an id up front so ProjectReferences can point at siblings not yet built below.
        Dictionary<string, ProjectId> projectIdByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectBuildInputs input in inputs)
        {
            projectIdByPath[input.ProjectPath] = ProjectId.CreateNewId();
        }

        Dictionary<string, string> generatedRootByProjectPath = new(StringComparer.OrdinalIgnoreCase);
        List<ProjectInfo> projectInfos = [];
        foreach (ProjectBuildInputs input in inputs)
        {
            ProjectId projectId = projectIdByPath[input.ProjectPath];
            string projectDirectory = Path.GetDirectoryName(input.ProjectPath) ?? string.Empty;
            string assemblyName = Path.GetFileNameWithoutExtension(input.ProjectPath);
            string rootNamespace = ReadRootNamespace(input.ProjectPath) ?? assemblyName;
            generatedRootByProjectPath[input.ProjectPath] = input.GeneratedRoot;

            IEnumerable<DocumentInfo> documents = CollectSourceFiles(projectDirectory)
                .Select(file => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(file),
                    filePath: file,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(File.ReadAllText(file)),
                        VersionStamp.Default,
                        file))));

            IEnumerable<ProjectReference> projectReferences = input.ProjectReferences
                .Select(reference => projectIdByPath.TryGetValue(Path.GetFullPath(reference), out ProjectId? referencedId)
                    ? new ProjectReference(referencedId)
                    : null)
                .Where(reference => reference is not null)
                .Select(reference => reference!)
                .Distinct();

            projectInfos.Add(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Default,
                    name: assemblyName,
                    assemblyName: assemblyName,
                    language: LanguageNames.CSharp,
                    filePath: input.ProjectPath)
                .WithDefaultNamespace(rootNamespace)
                .WithParseOptions(parseOptions)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true))
                .WithDocuments(documents)
                .WithMetadataReferences(input.References
                    .Where(reference => !string.IsNullOrWhiteSpace(reference) && File.Exists(reference))
                    .Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)))
                .WithProjectReferences(projectReferences));
        }

        using AdhocWorkspace workspace = new();
        // Add every project in one solution so ProjectReferences between them resolve.
        SolutionInfo solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, projects: projectInfos);
        Solution solution = workspace.AddSolution(solutionInfo);

        return await MSBuildWorkspaceLoader.CreateSnapshotAsync(
            inputs[0].ProjectPath,
            solution,
            [],
            cancellationToken,
            timingSink: null,
            razorDocumentsProvider: project =>
            {
                string key = Path.GetFullPath(project.FilePath ?? string.Empty);
                return generatedRootByProjectPath.TryGetValue(key, out string? generatedRoot) && !string.IsNullOrEmpty(generatedRoot)
                    ? RazorDocumentIndex.BuildFromGeneratedFiles(generatedRoot, parseOptions)
                    : [];
            });
    }

    // Each project's resolved references, written by the per-project dumprefs target into its own obj. First
    // match under obj/<config> (single-TFM; multi-targeted picks one TFM — a later refinement). Empty if absent.
    private static IReadOnlyList<string> ReadPerProjectReferences(string projectDirectory, string configuration)
    {
        string objConfig = Path.Combine(projectDirectory, "obj", configuration);
        if (!Directory.Exists(objConfig))
        {
            return [];
        }

        string? refsFile = Directory
            .GetFiles(objConfig, IndexRidesBuild.PerProjectRefsFileName, SearchOption.AllDirectories)
            .FirstOrDefault();
        return refsFile is not null && File.Exists(refsFile) ? File.ReadAllLines(refsFile) : [];
    }

    // The other projects this .csproj references (<ProjectReference Include="..\Other\Other.csproj" />),
    // resolved to absolute paths so they can be matched to sibling ProjectIds.
    private static IReadOnlyList<string> ParseProjectReferences(string projectPath)
    {
        try
        {
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
            return System.Text.RegularExpressions.Regex
                .Matches(
                    File.ReadAllText(projectPath),
                    "<ProjectReference\\s+Include=\"([^\"]+\\.csproj)\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(match => Path.GetFullPath(Path.Combine(projectDirectory, match.Groups[1].Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    // obj/<config>/<tfm>/generated. First match (multi-TFM is a later refinement).
    private static string FindGeneratedRoot(string projectDirectory, string configuration)
    {
        string objConfig = Path.Combine(projectDirectory, "obj", configuration);
        if (!Directory.Exists(objConfig))
        {
            return string.Empty;
        }

        return Directory.GetDirectories(objConfig, "generated", SearchOption.AllDirectories)
            .FirstOrDefault() ?? string.Empty;
    }

    private static (int ExitCode, string Output) RunDotnet(IReadOnlyList<string> args, string workingDirectory)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        // Drain both pipes CONCURRENTLY: a sequential ReadToEnd on stdout can deadlock if the child fills its
        // stderr buffer (a big `dotnet build` easily does), because the child blocks on stderr while we wait on
        // stdout and it never exits. And on timeout, kill the whole tree — otherwise a wedged build keeps pinning
        // obj/bin (the file-locking hazard). Mirrors SolutionBuildService.RunProcess.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process finished between the timeout and the kill.
            }
        }

        string output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        return (exited ? process.ExitCode : -1, output);
    }

    // Hand-written source, PLUS the MSBuild-generated helpers under obj (GlobalUsings / AssemblyInfo — needed
    // for the compilation to bind). NOT the source-generator output (razor .g.cs under generated\): those are
    // added to the compilation by BuildRazorDeclarationsAsync, so adding them here too would double-declare.
    private static IEnumerable<string> CollectSourceFiles(string projectDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('/', '\\');
            if (normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                    && !normalized.Contains("\\generated\\", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }

                continue;
            }

            yield return file;
        }
    }

    private static string? ReadRootNamespace(string projectPath)
    {
        try
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(projectPath),
                "<RootNamespace>([^<]+)</RootNamespace>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record BuildOutputSnapshotResult(
    MSBuildSolutionSnapshot Snapshot,
    bool BuildSucceeded,
    int BuildExitCode,
    string BuildOutput);

// One project's build outputs, gathered for the whole-solution read: its .csproj, the obj/ generated-files
// root, its resolved reference set, and the sibling projects it references (for cross-project ProjectReferences).
public sealed record ProjectBuildInputs(
    string ProjectPath,
    string GeneratedRoot,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ProjectReferences);
