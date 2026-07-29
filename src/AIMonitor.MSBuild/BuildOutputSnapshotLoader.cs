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
