using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Integration.Tests;

// Agent-in-the-loop tests over the NEW validation workflow: they drive the real WorkflowEditService loop
// (refresh/new_file -> submit_file -> the plan-complete REAL build via ValidatePlannedOverlayBuild) against
// the committed samples, realizing the copy-paste scenarios in samples/watched-solutions/*/test-prompts.
//
// Each scenario writes its OUTPUT to a STABLE, un-cleaned directory so the results can be reviewed after the
// run — BOTH the code (the working candidate source the loop produced) AND the workflow files (the edit
// manifests, staged records, and the RESULT.md build summary with the real compiler diagnostics):
//
//     %TEMP%\ClaudeWorkbenchAgentLoop\<scenario>\
//         src\        the copied sample, watched source (unchanged - edits live in runtime\working)
//         runtime\    the workflow files: working\ candidates, workflow\edits\ manifests, workflow\staged\
//         RESULT.md   scenario intent + build status + real diagnostics + each candidate's content
//
// These do REAL dotnet builds, so they live in the integration suite. Trait "AgentLoop".
[Trait("Suite", "AgentLoop")]
public sealed class AgentLoopSampleWorkflowTests
{
    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Calculator_new_file_cross_referencing_existing_type_builds_clean()
    {
        AgentLoop loop = AgentLoop.ForSample("CalculatorSample", "CalculatorSample.slnx", "calc-01-new-file");

        string cubesPath = loop.WatchedFile("Cubes.cs");
        loop.NewFile(cubesPath);
        loop.Submit(cubesPath, """
            namespace CalculatorSample;

            // New file that leans on the EXISTING Calculator type in another file - a cross-file reference the
            // real plan-complete build must resolve.
            public sealed class Cubes
            {
                private readonly Calculator calculator = new();

                public double Cube(double value) => calculator.Multiply(calculator.Multiply(value, value), value);
            }
            """);

        PreMergeValidationResult build = loop.PlanCompleteBuild(cubesPath);
        loop.WriteResult("New file Cubes.cs cross-referencing Calculator (prompt 02 shape).", build);

        Assert.Equal("passed", build.Status);
        Assert.False(build.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Calculator_semantic_add_method_builds_clean()
    {
        AgentLoop loop = AgentLoop.ForSample("CalculatorSample", "CalculatorSample.slnx", "calc-03-semantic-add-method");

        string calcPath = loop.WatchedFile("Calculator.cs");
        loop.Refresh(calcPath);
        // A SEMANTIC edit via the Roslyn add_method tool (NOT a whole-file submit) — prompt 01 shape. It flows
        // through the same syntax-only per-edit path and is validated by the real plan-complete build.
        loop.AddMethod(calcPath, "Calculator", "public double Modulo(double a, double b) => a % b;");

        PreMergeValidationResult build = loop.PlanCompleteBuild(calcPath);
        loop.WriteResult("Semantic add_method (Roslyn typed edit) adds Modulo to Calculator (prompt 01).", build);

        Assert.Equal("passed", build.Status);
        Assert.False(build.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Calculator_broken_cross_file_reference_fails_then_fixes()
    {
        AgentLoop loop = AgentLoop.ForSample("CalculatorSample", "CalculatorSample.slnx", "calc-02-build-fail-fix");
        string advancedPath = loop.WatchedFile("AdvancedCalculations.cs");
        loop.Refresh(advancedPath);

        // BROKEN: add a CubeOf method that calls calculator.Cube — which does NOT exist on the Calculator type
        // in the OTHER file (CS1061). Power/SquareRoot are kept intact so Program.cs still compiles; only the
        // new cross-file reference is broken.
        loop.Submit(advancedPath, """
            namespace CalculatorSample;

            public sealed class AdvancedCalculations
            {
                private readonly Calculator calculator = new();

                public double Power(double value, int exponent)
                {
                    double result = 1;
                    for (int i = 0; i < exponent; i++)
                    {
                        result = calculator.Multiply(result, value);
                    }

                    return result;
                }

                public double SquareRoot(double value)
                {
                    if (value < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value), "Cannot take the square root of a negative number.");
                    }

                    return Math.Sqrt(value);
                }

                public double CubeOf(double value) => calculator.Cube(value);
            }
            """);
        PreMergeValidationResult broken = loop.PlanCompleteBuild(advancedPath);
        loop.WriteResult("Broken cross-file reference (Calculator.Cube does not exist) — plan-complete build must FAIL with the real error.", broken, phase: "1-broken");

        Assert.Equal("failed", broken.Status);
        Assert.True(broken.IsError);
        Assert.True(broken.DiagnosticCount > 0);
        Assert.Contains(broken.Diagnostics, d => d.Contains(": error ", StringComparison.OrdinalIgnoreCase));

        // FIX (the agent's retry): CubeOf now uses the real Calculator.Multiply; Power/SquareRoot still present.
        loop.Submit(advancedPath, """
            namespace CalculatorSample;

            public sealed class AdvancedCalculations
            {
                private readonly Calculator calculator = new();

                public double Power(double value, int exponent)
                {
                    double result = 1;
                    for (int i = 0; i < exponent; i++)
                    {
                        result = calculator.Multiply(result, value);
                    }

                    return result;
                }

                public double SquareRoot(double value)
                {
                    if (value < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value), "Cannot take the square root of a negative number.");
                    }

                    return Math.Sqrt(value);
                }

                public double CubeOf(double value) => calculator.Multiply(calculator.Multiply(value, value), value);
            }
            """);
        PreMergeValidationResult repaired = loop.PlanCompleteBuild(advancedPath);
        loop.WriteResult("After the fix (Calculator.Multiply) — plan-complete build must PASS.", repaired, phase: "2-fixed");

        Assert.Equal("passed", repaired.Status);
        Assert.False(repaired.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Blazor_new_component_builds_clean_through_the_razor_generator()
    {
        AgentLoop loop = AgentLoop.ForSample("BlazorSample", "BlazorSample.slnx", "blazor-01-new-component");

        string cardPath = loop.WatchedFile(Path.Combine("Components", "CustomerCard.razor"));
        loop.NewFile(cardPath);
        // A .razor that references the EXISTING Customer model - only a real build (running the Razor source
        // generator) resolves this; the removed flat overlay skipped .razor entirely.
        loop.Submit(cardPath, """
            @using BlazorSample.Model

            <h4>@Customer.Name</h4>
            <span>@Customer.Id</span>

            @code {
                [Parameter]
                public Customer Customer { get; set; } = new();
            }
            """);

        PreMergeValidationResult build = loop.PlanCompleteBuild(cardPath);
        loop.WriteResult("New Razor component CustomerCard.razor using the Customer model (prompt 02).", build);

        Assert.Equal("passed", build.Status);
        Assert.False(build.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Blazor_cross_file_model_and_component_build_together()
    {
        AgentLoop loop = AgentLoop.ForSample("BlazorSample", "BlazorSample.slnx", "blazor-03-cross-file");

        // Add Email to the model AND a new component that renders it, in ONE plan. The component's markup
        // references Customer.Email (a member added in the OTHER, plain-C# file) — only a real build over the
        // whole set resolves the new member through the generated Razor code.
        string customerPath = loop.WatchedFile(Path.Combine("Model", "Customer.cs"));
        loop.Refresh(customerPath);
        loop.Submit(customerPath, """
            namespace BlazorSample.Model;

            public sealed class Customer
            {
                public Customer() { }
                public int Id { get; set; }
                public string Name { get; set; } = "";
                public string Email { get; set; } = "";
            }
            """);

        string cardPath = loop.WatchedFile(Path.Combine("Components", "CustomerEmailCard.razor"));
        loop.NewFile(cardPath);
        loop.Submit(cardPath, """
            @using BlazorSample.Model

            <h4>@Customer.Name</h4>
            <p>@Customer.Email</p>

            @code {
                [Parameter]
                public Customer Customer { get; set; } = new();
            }
            """);

        PreMergeValidationResult build = loop.PlanCompleteBuild(customerPath, cardPath);
        loop.WriteResult("Cross-file: Customer.Email (model) + CustomerEmailCard.razor using it, one build (prompt 03).", build);

        Assert.Equal("passed", build.Status);
        Assert.False(build.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Blazor_new_repository_implements_interface_and_rewires_consumer()
    {
        AgentLoop loop = AgentLoop.ForSample("BlazorSample", "BlazorSample.slnx", "blazor-04-repository-di");

        // A new interface implementation + rewiring the consumer, in one plan (the composition-root shape).
        string repoPath = loop.WatchedFile(Path.Combine("Repositories", "InMemoryCustomerRepository.cs"));
        loop.NewFile(repoPath);
        loop.Submit(repoPath, """
            using BlazorSample.Model;

            namespace BlazorSample.Repositories;

            public sealed class InMemoryCustomerRepository : ICustomerRepository
            {
                private readonly List<Customer> customers = new()
                {
                    new Customer { Id = 1, Name = "Ada" },
                    new Customer { Id = 2, Name = "Grace" },
                };

                public Task<Customer?> GetByIdAsync(int id) =>
                    Task.FromResult(customers.FirstOrDefault(customer => customer.Id == id));
            }
            """);

        string servicePath = loop.WatchedFile(Path.Combine("Repositories", "CustomerService.cs"));
        loop.Refresh(servicePath);
        loop.Submit(servicePath, """
            using BlazorSample.Model;

            namespace BlazorSample.Repositories;

            public sealed class CustomerService
            {
                private readonly InMemoryCustomerRepository repository = new InMemoryCustomerRepository();

                public Task<Customer?> LoadAsync(int id)
                {
                    return repository.GetByIdAsync(id);
                }
            }
            """);

        PreMergeValidationResult build = loop.PlanCompleteBuild(repoPath, servicePath);
        loop.WriteResult("New InMemoryCustomerRepository : ICustomerRepository + rewired CustomerService, one build (prompt 05).", build);

        Assert.Equal("passed", build.Status);
        Assert.False(build.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void Blazor_broken_razor_binding_fails_then_fixes()
    {
        AgentLoop loop = AgentLoop.ForSample("BlazorSample", "BlazorSample.slnx", "blazor-02-build-fail-fix");
        string listPath = loop.WatchedFile(Path.Combine("Components", "CustomerList.razor"));
        loop.Refresh(listPath);

        // BROKEN: markup references an undefined Subtitle member. The Razor generator emits C# that references
        // Subtitle -> the real build fails (CS0103 in the generated component). The old flat overlay could NOT
        // see .razor at all, so this class of error slipped through to the operator's Accept.
        loop.Submit(listPath, """
            <h3>Customers</h3>
            <p>@Subtitle</p>

            @code {
                private CustomerService Service { get; } = new CustomerService();
            }
            """);
        PreMergeValidationResult broken = loop.PlanCompleteBuild(listPath);
        loop.WriteResult("Broken .razor markup referencing an undefined Subtitle — plan-complete build must FAIL through the Razor generator (CS0103 in generated code).", broken, phase: "1-broken");

        Assert.Equal("failed", broken.Status);
        Assert.True(broken.IsError);

        // FIX (the agent's retry): add the Subtitle property the markup references.
        loop.Submit(listPath, """
            <h3>Customers</h3>
            <p>@Subtitle</p>

            @code {
                private CustomerService Service { get; } = new CustomerService();

                public string Subtitle { get; set; } = "All customers";
            }
            """);
        PreMergeValidationResult repaired = loop.PlanCompleteBuild(listPath);
        loop.WriteResult("After adding the Subtitle property — plan-complete build must PASS.", repaired, phase: "2-fixed");

        Assert.Equal("passed", repaired.Status);
        Assert.False(repaired.IsError);
    }

    [Fact]
    [Trait("Suite", "AgentLoop")]
    public void MixedTfm_cross_project_add_to_one_dll_and_use_from_another_fails_then_fixes()
    {
        // The original motivation: a change that spans TWO PROJECTS / assemblies (net8 Shared consumed by the
        // net8 ConsoleApp), in ONE session, validated by the real plan-complete build across the project graph.
        AgentLoop loop = AgentLoop.ForSample("MixedTfmSample", "MixedTfmSample.slnx", "mixedtfm-cross-project");
        string sharedPath = loop.WatchedFile(Path.Combine("Shared", "SharedGreeter.cs"));
        string consumerPath = loop.WatchedFile(Path.Combine("ConsoleApp", "Program.cs"));
        loop.Refresh(sharedPath);
        loop.Refresh(consumerPath);

        // BROKEN: the consumer (ConsoleApp.dll) calls SharedGreeter.Farewell, but Shared.dll does not define it
        // yet. Only a real cross-project build catches this — the consumer and the definition live in different
        // assemblies.
        loop.Submit(consumerPath, """
            using Shared;

            namespace ConsoleApp;

            internal static class Program
            {
                private static void Main()
                {
                    System.Console.WriteLine(SharedGreeter.Greet("console (net8.0)"));
                    System.Console.WriteLine(SharedGreeter.Farewell("console (net8.0)"));
                }
            }
            """);
        PreMergeValidationResult broken = loop.PlanCompleteBuild(sharedPath, consumerPath);
        loop.WriteResult("Consumer (ConsoleApp.dll) calls SharedGreeter.Farewell before it exists in Shared.dll — cross-project build must FAIL.", broken, phase: "1-broken");

        Assert.Equal("failed", broken.Status);
        Assert.True(broken.IsError);

        // FIX (the agent's retry): add Farewell to the Shared library (the other dll). Now it resolves.
        loop.Submit(sharedPath, """
            namespace Shared;

            public static class SharedGreeter
            {
                public static string Greet(string name) => $"Hello, {name}, from Shared (net8.0).";

                public static string Farewell(string name) => $"Goodbye, {name}, from Shared (net8.0).";
            }
            """);
        PreMergeValidationResult repairedCross = loop.PlanCompleteBuild(sharedPath, consumerPath);
        loop.WriteResult("After adding Farewell to Shared.dll — the cross-project build must PASS.", repairedCross, phase: "2-fixed");

        Assert.Equal("passed", repairedCross.Status);
        Assert.False(repairedCross.IsError);
    }

    // Drives the real edit loop against a copied sample and preserves code + workflow files for review.
    private sealed class AgentLoop
    {
        private readonly WorkflowEditService service;
        private readonly MonitorSettings settings;
        private readonly List<string> touched = new();

        private AgentLoop(WorkflowEditService service, MonitorSettings settings, string outputRoot, string watchedProjectFolder)
        {
            this.service = service;
            this.settings = settings;
            OutputRoot = outputRoot;
            WatchedProjectFolder = watchedProjectFolder;
        }

        private string OutputRoot { get; }
        private string WatchedProjectFolder { get; }

        public static AgentLoop ForSample(string sampleName, string solutionFileName, string scenario)
        {
            string repoRoot = FindRepositoryRoot();
            string sampleRoot = Path.Combine(repoRoot, "samples", "watched-solutions", sampleName);
            string outputRoot = Path.Combine(Path.GetTempPath(), "ClaudeWorkbenchAgentLoop", scenario);
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            string watchedProjectFolder = Path.Combine(outputRoot, "src");
            CopyTree(sampleRoot, watchedProjectFolder);
            string runtimeRoot = Path.Combine(outputRoot, "runtime");
            string solutionPath = Path.Combine(watchedProjectFolder, solutionFileName);
            MonitorSettings settings = MonitorSettings.Create(repoRoot, solutionPath, runtimeRoot);
            return new AgentLoop(new WorkflowEditService(settings), settings, outputRoot, watchedProjectFolder);
        }

        public string WatchedFile(string relativePath) => Path.Combine(WatchedProjectFolder, relativePath);

        public void Refresh(string watchedFilePath)
        {
            service.Refresh(watchedFilePath);
            Track(watchedFilePath);
        }

        public void NewFile(string watchedFilePath)
        {
            service.NewFile(watchedFilePath);
            Track(watchedFilePath);
        }

        public void Submit(string watchedFilePath, string content)
        {
            service.SubmitFile(watchedFilePath, content);
            Track(watchedFilePath);
        }

        // A SEMANTIC (Roslyn typed) edit — the add_method tool — rather than a whole-file submit. Exercises the
        // RoslynEditService path (which now flows through the same syntax-only per-edit + real plan-complete build).
        public void AddMethod(string watchedFilePath, string containingType, string declaration)
        {
            new RoslynEditService(settings).AddMethod(watchedFilePath, containingType, declaration);
            Track(watchedFilePath);
        }

        public PreMergeValidationResult PlanCompleteBuild(params string[] watchedFilePaths)
        {
            return service.ValidatePlannedOverlayBuild(watchedFilePaths.Select(Path.GetFullPath).ToList());
        }

        private void Track(string watchedFilePath)
        {
            string full = Path.GetFullPath(watchedFilePath);
            if (!touched.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                touched.Add(full);
            }
        }

        // Writes RESULT[-phase].md into the scenario output root: intent, build status, the REAL compiler
        // diagnostics, and each candidate's working-file content. The runtime\ workflow files (manifests,
        // staged records, working candidates) are left in place alongside for review.
        public void WriteResult(string intent, PreMergeValidationResult build, string? phase = null)
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine($"# Agent-loop result{(phase is null ? string.Empty : $" ({phase})")}");
            sb.AppendLine();
            sb.AppendLine($"- Intent: {intent}");
            sb.AppendLine($"- Build status: **{build.Status}** (IsError={build.IsError}, {build.DiagnosticCount} diagnostic(s))");
            sb.AppendLine($"- Validation workspace: `{build.ValidationWorkspacePath}`");
            sb.AppendLine($"- Workflow files: `{Path.Combine(OutputRoot, "runtime")}` (working candidates, workflow\\edits manifests, workflow\\staged)");
            sb.AppendLine();
            if (build.DiagnosticCount > 0)
            {
                sb.AppendLine("## Diagnostics (real compiler output)");
                sb.AppendLine();
                foreach (string diagnostic in build.Diagnostics)
                {
                    sb.AppendLine($"    {diagnostic}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("## Candidate code (working files the loop produced)");
            sb.AppendLine();
            foreach (string file in touched)
            {
                string relative = Path.GetRelativePath(WatchedProjectFolder, file);
                string workingPath = service.GetStatus(file).WorkingFilePath;
                string content = File.Exists(workingPath) ? File.ReadAllText(workingPath) : "(no working candidate)";
                sb.AppendLine($"### {relative}");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            string resultFile = Path.Combine(OutputRoot, phase is null ? "RESULT.md" : $"RESULT-{phase}.md");
            File.WriteAllText(resultFile, sb.ToString());
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dir);
                if (name is "bin" or "obj" or "test-prompts")
                {
                    continue;
                }

                Directory.CreateDirectory(dir.Replace(source, destination));
            }

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
}
