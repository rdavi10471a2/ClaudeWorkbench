using System.Diagnostics;
using AIMonitor.MSBuild;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AIMonitor.Integration.Tests;

// ADR-0007, increment 1: prove the index can build its razor documents from the SDK build's ALREADY-EMITTED
// generated C# (accurate, in-compile) instead of the standalone default engine — AND map generated spans
// back to the .razor via the file's `#line` span directives (GetMappedLineSpan), which is the piece that was
// uncertain. If this holds, the rest of the convergence is mechanical.
public sealed class IndexRazorFromBuildTests
{
    [Fact]
    public void Razor_documents_build_from_the_builds_generated_files_and_map_back_to_the_razor()
    {
        string repoRoot = FindRepositoryRoot();
        string sampleSrc = Path.Combine(repoRoot, "samples", "watched-solutions", "BlazorSample");
        Assert.True(Directory.Exists(sampleSrc), $"BlazorSample not found at {sampleSrc}");

        string work = Path.Combine(Path.GetTempPath(), "AIMonitorRazorFromBuild", Guid.NewGuid().ToString("N"));
        string projDir = Path.Combine(work, "BlazorSample");
        CopyTree(sampleSrc, projDir);

        // A real build that emits the SDK's generated Razor C# to obj (accurate, with the full reference set).
        (int exit, string output) = Run("dotnet",
            ["build", Path.Combine(projDir, "BlazorSample.csproj"), "-c", "Debug", "--nologo", "-v:quiet",
             "-nodeReuse:false", "-t:Rebuild", "-p:EmitCompilerGeneratedFiles=true"],
            projDir);
        Assert.True(exit == 0, $"build failed:\n{output}");

        string generatedRoot = Path.Combine(projDir, "obj", "Debug", "net10.0", "generated");
        Assert.True(Directory.Exists(generatedRoot), "the build emitted no generated files");

        IReadOnlyList<RazorDocumentIndex> documents = RazorDocumentIndex.BuildFromGeneratedFiles(
            generatedRoot,
            new CSharpParseOptions(LanguageVersion.Preview)); // #line span directives are a recent C# feature

        // The disciplined component (markup + code-behind + scoped css) came through as a razor document keyed
        // to its .razor source (not the .g.cs), and the generated C# is the ACCURATE component class.
        RazorDocumentIndex? card = documents.FirstOrDefault(document =>
            document.FilePath.EndsWith("CustomerCard.razor", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(card);
        Assert.Contains("class CustomerCard", card!.GeneratedTree.ToString());

        // The whole point: a generated span maps back to the .razor via the build's #line directives.
        bool mapsBackToRazor = card.GeneratedTree.GetRoot()
            .DescendantTokens()
            .Any(token => card.TryMapGeneratedSpan(token.Span, out FileLinePositionSpan mapped)
                && mapped.Path.EndsWith("CustomerCard.razor", StringComparison.OrdinalIgnoreCase));
        Assert.True(mapsBackToRazor, "no generated span mapped back to CustomerCard.razor via #line");
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
