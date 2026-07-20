using AIMonitor.Core;
using AIMonitor.MSBuild;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AIMonitor.Data.Tests;

// Oracle-based differential test for the reference index, salvaged from the retired
// `AIMonitor.ToolSmokeTests --fixture-index-matrix` console mode (see
// docs/plans/retire-legacy-test-harness.md, Phase 2.1).
//
// A self-contained three-file project is written to a temp folder and indexed with the REAL
// SolutionIndexBuilder + MSBuildWorkspaceLoader. The same sources are then parsed INDEPENDENTLY into a
// raw CSharpCompilation and every IdentifierNameSyntax is bound with SemanticModel.GetSymbolInfo to
// count references from scratch. For each symbol shape the assertion is three-way:
//
//     AIMonitor's reference count == Roslyn's count == a hardcoded expected count
//
// The hardcoded number keeps a shared blind spot (both sides drifting together) from passing, and the
// Roslyn side keeps the hardcoded number from silently going stale. One [Theory] case per shape so a
// failure names the shape that broke.
//
// The fixture also plants a hand-written `.g.cs` compile item, which exercises generated-file policy:
// AIMonitor indexes it like any other Compile item, and its presence must not perturb the counts above.
public sealed class FixtureIndexMatrixTests : IClassFixture<FixtureIndexMatrixTests.MatrixFixture>
{
    private readonly MatrixFixture fixture;

    public FixtureIndexMatrixTests(MatrixFixture fixture)
    {
        this.fixture = fixture;
    }

    public static TheoryData<string> ShapeNames
    {
        get
        {
            TheoryData<string> data = [];
            foreach (MatrixCheck check in MatrixFixture.Checks)
            {
                data.Add(check.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Indexed_reference_count_matches_roslyn_and_the_expected_count(string shapeName)
    {
        MatrixCheck check = MatrixFixture.Checks.Single(candidate => candidate.Name == shapeName);

        IndexedSymbolRow? indexedTarget = fixture.Symbols.FirstOrDefault(symbol =>
            symbol.Name.Equals(check.SymbolName, StringComparison.Ordinal)
            && symbol.Kind.Equals(check.IndexKind, StringComparison.OrdinalIgnoreCase)
            && symbol.Signature.Contains(check.SignatureContains, StringComparison.Ordinal));
        Assert.True(
            indexedTarget is not null,
            $"AIMonitor did not index a '{check.IndexKind}' named '{check.SymbolName}' for shape '{shapeName}'.");

        RoslynCounts roslyn = fixture.RoslynCounts[check.Name];
        Assert.True(roslyn.TargetResolved, $"The independent Roslyn pass could not resolve '{check.SymbolName}'.");

        int indexedReferenceCount = fixture.References
            .Count(reference => reference.TargetStableKey == indexedTarget!.StableKey);

        // Three-way: the hardcoded number pins the shape, and Roslyn is the independent oracle.
        Assert.Equal(check.ExpectedReferences, roslyn.ReferenceCount);
        Assert.Equal(check.ExpectedReferences, indexedReferenceCount);
        Assert.Equal(roslyn.ReferenceCount, indexedReferenceCount);
    }

    // Generated-file policy: a hand-written `.g.cs` is a normal Compile item, so it is indexed like any
    // other document. This is the assertion the smoke mode planted the file for but never made.
    [Fact]
    public void Generated_source_file_is_indexed_as_a_compile_item()
    {
        Assert.Contains(
            fixture.Documents,
            document => document.FilePath.EndsWith("Fixture.Generated.g.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            fixture.Symbols,
            symbol => symbol.Name == "GeneratedMethod"
                && symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
                && symbol.FilePath.EndsWith("Fixture.Generated.g.cs", StringComparison.OrdinalIgnoreCase));
    }

    public sealed record MatrixCheck(
        string Name,
        string SymbolName,
        string RoslynKind,
        string IndexKind,
        string SignatureContains,
        int ExpectedReferences);

    public sealed record RoslynCounts(bool TargetResolved, int ReferenceCount);

    public sealed class MatrixFixture : IAsyncLifetime
    {
        private string root = string.Empty;

        public static IReadOnlyList<MatrixCheck> Checks { get; } =
        [
            new("instance method", "Target", "Method", "Method", "Target", 2),
            new("static method", "StaticTarget", "Method", "Method", "StaticTarget", 2),
            new("property", "Value", "Property", "Property", "Value", 3),
            new("field", "Counter", "Field", "Field", "Counter", 3),
            new("event", "Changed", "Event", "Event", "Changed", 2),
            new("base type", "FixtureBase", "NamedType", "NamedType", "FixtureBase", 1),
            new("extension method", "Doubled", "Method", "Method", "Doubled", 1)
        ];

        public IReadOnlyList<IndexedSymbolRow> Symbols { get; private set; } = [];

        public IReadOnlyList<IndexedReferenceRow> References { get; private set; } = [];

        public IReadOnlyList<IndexedDocumentRow> Documents { get; private set; } = [];

        public IReadOnlyDictionary<string, RoslynCounts> RoslynCounts { get; private set; } =
            new Dictionary<string, RoslynCounts>();

        public async Task InitializeAsync()
        {
            root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", "FixtureIndexMatrix", Guid.NewGuid().ToString("N"));
            string projectRoot = Path.Combine(root, "FixtureProject");
            Directory.CreateDirectory(projectRoot);

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "FixtureProject.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "FixtureProject.slnx"), """
                <Solution>
                  <Project Path="FixtureProject.csproj" />
                </Solution>
                """);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Fixture.A.cs"), FixtureSourceA);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Fixture.B.cs"), FixtureSourceB);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Fixture.Generated.g.cs"), FixtureGeneratedSource);

            string solutionPath = Path.Combine(projectRoot, "FixtureProject.slnx");
            MonitorSettings settings = MonitorSettings.Create(root, solutionPath, Path.Combine(root, "runtime"));
            string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
            SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
            SolutionIndexBuilder builder = new(new MSBuildWorkspaceLoader(), store);
            await builder.RebuildAsync(settings);

            Symbols = store.ListSymbols();
            References = store.ListReferences();
            Documents = store.ListDocuments();
            RoslynCounts = BuildRoslynCounts(projectRoot);
        }

        public Task DisposeAsync()
        {
            try
            {
                if (root.Length > 0 && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort: a locked SQLite handle must not fail the run.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Task.CompletedTask;
        }

        // The independent oracle: parse the same sources from scratch, bind every IdentifierNameSyntax,
        // and count the ones whose symbol is the target. Nothing here touches AIMonitor.
        private static Dictionary<string, RoslynCounts> BuildRoslynCounts(string projectRoot)
        {
            SyntaxTree[] trees = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !PathHasSegment(path, "bin"))
                .Where(path => !PathHasSegment(path, "obj"))
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
                .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(
                "AIMonitorFixtureMatrix",
                trees,
                GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Dictionary<string, RoslynCounts> result = [];
            foreach (MatrixCheck check in Checks)
            {
                ISymbol? target = FindRoslynTarget(compilation, check);
                if (target is null)
                {
                    result[check.Name] = new RoslynCounts(false, 0);
                    continue;
                }

                int references = 0;
                foreach (SyntaxTree tree in trees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    foreach (SyntaxNode node in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        SymbolInfo info = model.GetSymbolInfo(node);
                        ISymbol? symbol = NormalizeSymbol(info.Symbol ?? info.CandidateSymbols.FirstOrDefault());
                        if (symbol is not null
                            && SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, target.OriginalDefinition))
                        {
                            references++;
                        }
                    }
                }

                result[check.Name] = new RoslynCounts(true, references);
            }

            return result;
        }

        private static ISymbol? FindRoslynTarget(Compilation compilation, MatrixCheck check)
        {
            return compilation.SyntaxTrees
                .Select(tree => compilation.GetSemanticModel(tree, ignoreAccessibility: true))
                .SelectMany(model => model.SyntaxTree.GetRoot().DescendantNodes()
                    .Select(node => model.GetDeclaredSymbol(node))
                    .Where(symbol => symbol is not null)
                    .Select(symbol => symbol!))
                .FirstOrDefault(symbol => symbol.Name.Equals(check.SymbolName, StringComparison.Ordinal)
                    && symbol.Kind.ToString().Equals(check.RoslynKind, StringComparison.OrdinalIgnoreCase));
        }

        // Extension-method invocations bind to the reduced form; normalize back to the declaration.
        private static ISymbol? NormalizeSymbol(ISymbol? symbol)
        {
            return symbol is IMethodSymbol { ReducedFrom: not null } method ? method.ReducedFrom : symbol;
        }

        private static IReadOnlyList<MetadataReference> GetMetadataReferences()
        {
            string? trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            return string.IsNullOrWhiteSpace(trustedAssemblies)
                ? []
                : trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(File.Exists)
                    .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                    .ToArray();
        }

        private static bool PathHasSegment(string filePath, string segment)
        {
            return filePath
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
        }

        private const string FixtureSourceA =
            """
            using System;

            namespace AIMonitor.IndexMatrixFixture;

            internal abstract class FixtureBase
            {
            }

            internal delegate int FixtureDelegate(int value);

            internal sealed class FixtureTarget : FixtureBase
            {
                public int Counter;
                public int Value { get; set; }
                public event EventHandler? Changed;

                public FixtureTarget()
                {
                }

                public FixtureTarget(int seed)
                {
                    Counter = seed;
                }

                public int Target(int value) => value + 1;
                public static int StaticTarget(int value) => value + 2;
                public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
            }

            internal static class FixtureExtensions
            {
                public static int Doubled(this int value) => value * 2;
            }
            """;

        private const string FixtureSourceB =
            """
            namespace AIMonitor.IndexMatrixFixture;

            internal sealed class FixtureCaller
            {
                private readonly FixtureTarget target = new FixtureTarget(1);
                private readonly FixtureDelegate gate = FixtureTarget.StaticTarget;

                public int CallInstance() => target.Target(1) + target.Target(2);
                public int CallStatic() => FixtureTarget.StaticTarget(3);
                public int ReadWriteMembers()
                {
                    target.Counter = target.Value;
                    target.Value = target.Counter;
                    return target.Value;
                }

                public void WireEvent()
                {
                    target.Changed += (_, _) => { };
                }

                public int InvokeDelegate() => gate(4);
                public int CallExtension() => 5.Doubled();
            }
            """;

        private const string FixtureGeneratedSource =
            """
            // <auto-generated/>
            namespace AIMonitor.IndexMatrixFixture;

            internal sealed class GeneratedShape
            {
                public int GeneratedMethod() => 1;
            }
            """;
    }
}
