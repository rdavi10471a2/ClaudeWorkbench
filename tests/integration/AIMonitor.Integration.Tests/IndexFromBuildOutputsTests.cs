using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AIMonitor.Integration.Tests;

// Proves the convergence hypothesis for the index: a Roslyn compilation — i.e. the index — can be built
// PURELY from a real build's TEXT outputs, with no MSBuildWorkspace / BuildHost and no in-index Razor
// generation. The inputs are exactly:
//   * the hand-written source .cs, PLUS
//   * the generated .g.cs the build emitted to obj (Razor components + global usings), obtained by building
//     with -p:EmitCompilerGeneratedFiles=true, PLUS
//   * the resolved reference set csc used, dumped from the build via @(ReferencePathWithRefAssemblies).
// If Roslyn compiles that set clean AND resolves a type that only exists because the build generated it from
// a .razor file, then "build once -> index over the emitted text" is real, not hypothetical.
public sealed class IndexFromBuildOutputsTests
{
    [Fact]
    public void Index_builds_from_a_real_builds_source_plus_generated_text_plus_refs()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorIndexConvergence", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);

        // One real build that (a) emits the generated .g.cs to disk and (b) dumps the exact reference set csc
        // uses. This is the single build the convergence rides.
        string targets = Path.Combine(work, "dumprefs.targets");
        string refsFile = Path.Combine(work, "refs.txt");
        File.WriteAllText(targets, """
            <Project>
              <Target Name="DumpRefs" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(RefsDumpPath)" Lines="@(ReferencePathWithRefAssemblies)" Overwrite="true" />
              </Target>
            </Project>
            """);
        string proj = Path.Combine(projDir, "BlazorSample.csproj");
        (int exit, string output) = Run("dotnet",
            [
                "build", proj, "-c", "Debug", "--nologo", "-v:quiet", "-nodeReuse:false", "-t:Rebuild",
                "-p:EmitCompilerGeneratedFiles=true",
                $"-p:CustomAfterMicrosoftCommonTargets={targets}",
                $"-p:RefsDumpPath={refsFile}"
            ],
            projDir);
        Assert.True(exit == 0, $"build failed (exit {exit}):\n{output}");
        Assert.True(File.Exists(refsFile), "the build did not dump the reference set");

        // The text sources the build compiled: hand-written source + everything generated under obj
        // (Razor .g.cs, GlobalUsings.g.cs, AssemblyInfo) — i.e. every .cs except the bin output.
        string[] textSources = Directory.GetFiles(projDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('/', '\\').Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(textSources, p => p.Replace('/', '\\').Contains("\\generated\\", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)); // the build's generated Razor is on disk

        MetadataReference[] references = File.ReadAllLines(refsFile)
            .Where(line => line.Length > 0 && File.Exists(line))
            .Select(line => (MetadataReference)MetadataReference.CreateFromFile(line))
            .ToArray();
        Assert.True(references.Length > 50, $"expected the resolved framework reference set, got {references.Length}");

        SyntaxTree[] trees = textSources
            .Select(p => CSharpSyntaxTree.ParseText(
                File.ReadAllText(p),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: p))
            .ToArray();

        // This IS the index: a Roslyn compilation over {source + generated} text with the build's refs.
        CSharpCompilation compilation = CSharpCompilation.Create(
            "BlazorIndexProbe",
            trees,
            references,
            // The Web app has a top-level Program.cs, so mirror the real build: an executable compilation
            // (a library kind trips CS8805 on top-level statements).
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));

        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0,
            "compilation over the build's emitted text should be clean; first errors:\n"
            + string.Join("\n", errors.Take(10).Select(e => e.ToString())));

        // Symbols come out — including a type that exists ONLY because the build generated it from a .razor
        // file. Plain source symbol + generated-from-Razor symbol both resolve => the generated text carries
        // into the index.
        INamedTypeSymbol? customerModel = compilation.GetTypeByMetadataName("BlazorSample.Model.Customer");
        INamedTypeSymbol? razorComponent = compilation.GetTypeByMetadataName("BlazorSample.Components.CustomerList");
        Assert.NotNull(customerModel);
        Assert.NotNull(razorComponent);
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
