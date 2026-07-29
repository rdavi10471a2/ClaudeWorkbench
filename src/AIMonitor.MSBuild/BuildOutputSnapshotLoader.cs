using System.Diagnostics;
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
    public async Task<MSBuildSolutionSnapshot> BuildProjectSnapshotAsync(
        string projectPath,
        string generatedRoot,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken = default)
    {
        // Project metadata (TargetFramework, package refs, …) is still read via MSBuildEvaluatedProject.Load,
        // an in-proc MSBuild evaluation that needs MSBuildLocator registered. This is NOT the out-of-proc
        // workspace BuildHost the convergence drops — just the lightweight csproj metadata read.
        MSBuildWorkspaceLoader.EnsureMSBuildRegistered();

        string fullProjectPath = Path.GetFullPath(projectPath);
        string projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? string.Empty;
        string assemblyName = Path.GetFileNameWithoutExtension(fullProjectPath);
        string rootNamespace = ReadRootNamespace(fullProjectPath) ?? assemblyName;

        // #line span directives in the build's generated razor are a recent C# feature.
        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        ProjectId projectId = ProjectId.CreateNewId();

        IEnumerable<DocumentInfo> documents = CollectSourceFiles(projectDirectory)
            .Select(file => DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(file),
                filePath: file,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(File.ReadAllText(file)),
                    VersionStamp.Default,
                    file))));

        ProjectInfo projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                name: assemblyName,
                assemblyName: assemblyName,
                language: LanguageNames.CSharp,
                filePath: fullProjectPath)
            .WithDefaultNamespace(rootNamespace)
            .WithParseOptions(parseOptions)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true))
            .WithDocuments(documents)
            .WithMetadataReferences(references
                .Where(reference => !string.IsNullOrWhiteSpace(reference) && File.Exists(reference))
                .Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)));

        using AdhocWorkspace workspace = new();
        Solution solution = workspace.AddProject(projectInfo).Solution;

        // Reuse the whole existing pipeline; only the razor source changes — the build's accurate .g.cs.
        return await MSBuildWorkspaceLoader.CreateSnapshotAsync(
            fullProjectPath,
            solution,
            [],
            cancellationToken,
            timingSink: null,
            razorDocumentsProvider: _ => RazorDocumentIndex.BuildFromGeneratedFiles(generatedRoot, parseOptions));
    }

    // Self-contained: run the ONE real build that emits the generated .g.cs AND dumps the resolved reference
    // set, then produce the snapshot from those outputs. This is the "index rides the build" entry point —
    // the build is the only compile; the index is a Roslyn pass over its output.
    public async Task<BuildOutputSnapshotResult> OpenProjectFromBuildAsync(
        string projectPath,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        string fullProjectPath = Path.GetFullPath(projectPath);
        string projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? string.Empty;

        string scratch = Path.Combine(Path.GetTempPath(), "AIMonitorBuildOutput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string targetsFile = Path.Combine(scratch, "dumprefs.targets");
        string refsFile = Path.Combine(scratch, "refs.txt");
        File.WriteAllText(targetsFile, """
            <Project>
              <Target Name="DumpRefsForIndex" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(RefsDumpPath)" Lines="@(ReferencePathWithRefAssemblies)" Overwrite="true" />
              </Target>
            </Project>
            """);

        try
        {
            (int exitCode, string output) = RunDotnet(
                [
                    "build", fullProjectPath, "-c", configuration, "--nologo", "-v:quiet", "-nodeReuse:false",
                    "-p:EmitCompilerGeneratedFiles=true",
                    $"-p:CustomAfterMicrosoftCommonTargets={targetsFile}",
                    $"-p:RefsDumpPath={refsFile}"
                ],
                projectDirectory);

            string generatedRoot = FindGeneratedRoot(projectDirectory, configuration);
            IReadOnlyList<string> references = File.Exists(refsFile) ? File.ReadAllLines(refsFile) : [];

            MSBuildSolutionSnapshot snapshot = await BuildProjectSnapshotAsync(
                fullProjectPath,
                generatedRoot,
                references,
                cancellationToken);

            return new BuildOutputSnapshotResult(snapshot, exitCode == 0, exitCode, output);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch cleanup is best-effort */ }
        }
    }

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

        string scratch = Path.Combine(Path.GetTempPath(), "AIMonitorBuildOutput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string targetsFile = Path.Combine(scratch, "dumprefs-per-project.targets");
        // Per-project refs into each project's own obj (IntermediateOutputPath) — no shared path to clobber.
        // AfterTargets=ResolveReferences so @(ReferencePath) is populated and the dump runs even when a project
        // is up-to-date and CoreCompile is skipped (incremental builds still resolve references).
        File.WriteAllText(targetsFile, """
            <Project>
              <Target Name="DumpRefsForIndexPerProject" AfterTargets="ResolveReferences">
                <WriteLinesToFile File="$(IntermediateOutputPath)aimonitor-index-refs.txt" Lines="@(ReferencePath)" Overwrite="true" />
              </Target>
            </Project>
            """);

        try
        {
            (int exitCode, string output) = RunDotnet(
                [
                    "build", fullSolutionPath, "-c", configuration, "--nologo", "-v:quiet", "-nodeReuse:false",
                    "-p:EmitCompilerGeneratedFiles=true",
                    $"-p:CustomAfterMicrosoftCommonTargets={targetsFile}"
                ],
                solutionDirectory);

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

            MSBuildSolutionSnapshot snapshot = await BuildSolutionSnapshotAsync(inputs, cancellationToken);
            return new BuildOutputSnapshotResult(snapshot, exitCode == 0, exitCode, output);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch cleanup is best-effort */ }
        }
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
            .GetFiles(objConfig, "aimonitor-index-refs.txt", SearchOption.AllDirectories)
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
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        return (process.HasExited ? process.ExitCode : -1, stdout + stderr);
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
