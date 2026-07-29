using System.Diagnostics;
using AIMonitor.MSBuild;

namespace AIMonitor.Integration.Tests;

// ADR-0007, increment 2: prove BuildOutputSnapshotLoader produces a real solution snapshot from a build's
// OUTPUTS (source + generated .g.cs + harvested refs) via an AdhocWorkspace, reusing the existing extraction
// (CreateSnapshotAsync) — and that the razor comes through ACCURATE and mapped to the .razor. No
// MSBuildWorkspace, no BuildHost, no standalone razor engine.
public sealed class IndexBuildOutputSnapshotTests
{
    [Fact]
    public async Task Snapshot_from_build_outputs_carries_symbols_incl_razor_mapped_to_the_razor()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorBuildSnapshot", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);

        // One real build: emit the generated .g.cs AND dump the resolved reference set (the convergence's inputs).
        string targets = Path.Combine(work, "dumprefs.targets");
        string refsFile = Path.Combine(work, "refs.txt");
        File.WriteAllText(targets, """
            <Project>
              <Target Name="DumpRefs" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(RefsDumpPath)" Lines="@(ReferencePathWithRefAssemblies)" Overwrite="true" />
              </Target>
            </Project>
            """);
        string projectPath = Path.Combine(projDir, "BlazorSample.csproj");
        (int exit, string output) = Run("dotnet",
            ["build", projectPath, "-c", "Debug", "--nologo", "-v:quiet", "-nodeReuse:false", "-t:Rebuild",
             "-p:EmitCompilerGeneratedFiles=true",
             $"-p:CustomAfterMicrosoftCommonTargets={targets}",
             $"-p:RefsDumpPath={refsFile}"],
            projDir);
        Assert.True(exit == 0, $"build failed:\n{output}");

        string generatedRoot = Path.Combine(projDir, "obj", "Debug", "net10.0", "generated");
        string[] references = File.ReadAllLines(refsFile);

        MSBuildSolutionSnapshot snapshot = await new BuildOutputSnapshotLoader()
            .BuildProjectSnapshotAsync(projectPath, generatedRoot, references);

        Assert.Single(snapshot.Projects);
        MSBuildProjectSnapshot project = snapshot.Projects[0];

        // A plain source symbol came through.
        Assert.Contains(project.Symbols, symbol => symbol.Name == "Customer");

        // The disciplined component's TYPE resolves accurately (markup partial from the build's .g.cs merged
        // with the code-behind partial) — its declaring location is the code-behind, which is correct.
        Assert.Contains(project.Symbols, symbol =>
            symbol.Name == "CustomerCard"
            && symbol.Kind == "NamedType"
            && symbol.Signature == "BlazorSample.Components.CustomerCard");

        // The whole convergence, end to end: a member declared in a .razor @code block comes through mapped
        // to the .razor via the build's #line directives (not the generated .g.cs path).
        Assert.Contains(project.Symbols, symbol =>
            symbol.Name == "LoadAsync"
            && symbol.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
    }

    // Increment 3: the self-contained entry point — give it a project path, it runs the ONE build (emit
    // generated + harvest refs) and produces the snapshot. "index rides the build," start to finish, one call.
    [Fact]
    public async Task Self_contained_loader_builds_then_snapshots_from_just_a_project_path()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        string work = Path.Combine(Path.GetTempPath(), "AIMonitorBuildSnapshotSC", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);
        string projectPath = Path.Combine(projDir, "BlazorSample.csproj");

        BuildOutputSnapshotResult result = await new BuildOutputSnapshotLoader()
            .OpenProjectFromBuildAsync(projectPath);

        Assert.True(result.BuildSucceeded, $"build failed (exit {result.BuildExitCode}):\n{result.BuildOutput}");
        MSBuildProjectSnapshot project = Assert.Single(result.Snapshot.Projects);
        Assert.Contains(project.Symbols, symbol => symbol.Name == "Customer");
        Assert.Contains(project.Symbols, symbol =>
            symbol.Name == "LoadAsync"
            && symbol.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));
    }

    private static (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(fileName)
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
        process.WaitForExit((int)TimeSpan.FromMinutes(3).TotalMilliseconds);
        return (process.HasExited ? process.ExitCode : -1, stdout + stderr);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj" or "test-prompts"))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClaudeWorkbench.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (ClaudeWorkbench.slnx).");
    }
}
