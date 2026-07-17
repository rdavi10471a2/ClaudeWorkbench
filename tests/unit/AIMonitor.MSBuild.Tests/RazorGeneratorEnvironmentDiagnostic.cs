using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Linq;
using Xunit.Abstractions;

namespace AIMonitor.MSBuild.Tests;

// Diagnostic-only (gated on RAZORGEN_DIAG=1): opens a hermetic net10.0 Razor fixture through the SAME MSBuildWorkspace
// path the loader uses, and dumps what the workspace actually resolved — analyzer references (is the Razor source
// generator loaded?), additional documents (are the .razor files supplied to the generator?), workspace diagnostics,
// and the source-generated-document count. Purpose: prove WHY razor-generated:* rows are absent on this machine
// (MSBuildWorkspace surfaces 0 source-generated docs) and pin the exact SDK requirement that fixes it.
public sealed class RazorGeneratorEnvironmentDiagnostic
{
    private readonly ITestOutputHelper output;

    public RazorGeneratorEnvironmentDiagnostic(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Suite", "Diagnostic")]
    public async Task Dump_workspace_razor_generator_state()
    {
        if (Environment.GetEnvironmentVariable("RAZORGEN_DIAG") != "1")
        {
            return;
        }

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        VisualStudioInstance[] instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
        foreach (VisualStudioInstance instance in instances)
        {
            output.WriteLine($"MSBuild instance: {instance.Name} {instance.Version} -> {instance.MSBuildPath}");
        }

        string root = Path.Combine(Path.GetTempPath(), "AIMonitorRazorGenDiag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Razor.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "_Imports.razor"), "@using Microsoft.AspNetCore.Components\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Greeter.cs"), """
            namespace DiagRazor;
            public class Greeter { public string Title => "hi"; public void Greet() { } }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "Counter.razor"), """
            <h3>@Model.Title</h3>
            @code {
                private Greeter Model { get; } = new Greeter();
                private void Go() => Model.Greet();
            }
            """);

        using (MSBuildWorkspace workspace = MSBuildWorkspace.Create())
        {
            Project project = await workspace.OpenProjectAsync(Path.Combine(root, "Razor.csproj"));

            output.WriteLine($"=== workspace diagnostics ({workspace.Diagnostics.Count}) ===");
            foreach (WorkspaceDiagnostic diagnostic in workspace.Diagnostics)
            {
                output.WriteLine($"  [{diagnostic.Kind}] {diagnostic.Message}");
            }

            output.WriteLine($"=== analyzer references ({project.AnalyzerReferences.Count}) ===");
            foreach (object analyzerReference in (System.Collections.IEnumerable)project.AnalyzerReferences)
            {
                dynamic typed = analyzerReference;
                output.WriteLine($"  {typed.Display} | {typed.FullPath}");
            }

            string[] additionalDocs = project.AdditionalDocuments.Select(document => document.FilePath ?? document.Name).ToArray();
            output.WriteLine($"=== additional documents ({additionalDocs.Length}) ===");
            foreach (string additionalDoc in additionalDocs)
            {
                output.WriteLine($"  {additionalDoc}");
            }

            output.WriteLine($"=== runtime Roslyn version: {typeof(Compilation).Assembly.GetName().Version} ({typeof(Compilation).Assembly.Location}) ===");

            IEnumerable<SourceGeneratedDocument> generated = await project.GetSourceGeneratedDocumentsAsync();
            SourceGeneratedDocument[] generatedArray = generated.ToArray();
            output.WriteLine($"=== source-generated documents ({generatedArray.Length}) ===");
            foreach (SourceGeneratedDocument document in generatedArray.Take(20))
            {
                output.WriteLine($"  {document.HintName} | {document.FilePath}");
            }

            Compilation? compilation = await project.GetCompilationAsync();
            output.WriteLine($"=== compilation syntax trees: {compilation?.SyntaxTrees.Count() ?? -1} ===");
            if (compilation is not null)
            {
                Diagnostic[] notable = compilation.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning
                        || diagnostic.Id.StartsWith("CS85", StringComparison.Ordinal)
                        || diagnostic.Id.StartsWith("CS90", StringComparison.Ordinal)
                        || diagnostic.Descriptor.Category.Contains("Generator", StringComparison.OrdinalIgnoreCase)
                        || diagnostic.Descriptor.Category.Contains("Analyzer", StringComparison.OrdinalIgnoreCase))
                    .Take(40)
                    .ToArray();
                output.WriteLine($"=== notable compilation diagnostics ({notable.Length}) ===");
                foreach (Diagnostic diagnostic in notable)
                {
                    output.WriteLine($"  [{diagnostic.Severity}] {diagnostic.Id} ({diagnostic.Descriptor.Category}): {diagnostic.GetMessage()}");
                }
            }
        }
    }
}
