using System.Text.Json;
using System.Text.Json.Serialization;
using AIMonitor.Core;
using AIMonitor.MSBuild;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AIMonitor.Data.Tests;

/// <summary>
/// Language corpus oracle: every corpus case is bound with a raw Roslyn <see cref="CSharpCompilation"/>
/// and the AIMonitor index is asserted to agree on symbol identity, reference count, caller count,
/// relationship kinds and the exact line/column of the bind marker.
/// Ported from <c>tests/smoke/AIMonitor.LanguageCorpusSmokeTests</c>; assertion is unconditional here.
/// </summary>
public sealed class LanguageCorpusTests : IClassFixture<LanguageCorpusFixture>
{
    private const string InformationalSkipReason =
        "Harness limitation: the corpus is synthesised as a single project, so multi-project, "
        + "project-system and framework-metadata cases cannot be asserted. Tracked as a known gap.";

    private readonly LanguageCorpusFixture _fixture;

    public LanguageCorpusTests(LanguageCorpusFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string> AssertedCases()
    {
        TheoryData<string> data = [];
        foreach (LanguageCorpusCase item in LanguageCorpusFixture.LoadCorpusCases().Where(item => !item.Informational))
        {
            data.Add(item.CaseId);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AssertedCases))]
    public void Corpus_case_matches_roslyn_oracle(string caseId)
    {
        LanguageCorpusEvaluation result = _fixture.GetEvaluation(caseId);
        LanguageCorpusCase item = result.Case;

        Assert.True(
            result.Roslyn.TargetResolved,
            $"[{caseId}] Roslyn did not bind the marked span to any symbol.");
        Assert.Equal(item.ExpectedRoslynDisplay, result.Roslyn.TargetDisplay);

        if (item.ExpectedMonitorTarget)
        {
            Assert.True(
                result.IndexedTarget is not null,
                $"[{caseId}] AIMonitor index has no {item.ExpectedKind} named '{item.ExpectedName}' in '{item.ExpectedTargetRelativePath}'.");
        }
        else
        {
            Assert.True(
                result.IndexedTarget is null,
                $"[{caseId}] AIMonitor index unexpectedly contains a target row ('{result.IndexedTarget?.StableKey}').");
        }

        Assert.Equal(item.ExpectedReferenceCount, result.References.Count);

        if (item.ExpectedCallerCount is int expectedCallerCount)
        {
            Assert.Equal(expectedCallerCount, result.Callers.Count);
        }

        foreach (string expectedRelationshipKind in item.ExpectedRelationshipKinds)
        {
            Assert.True(
                result.RelationshipKinds.Contains(expectedRelationshipKind, StringComparer.Ordinal),
                $"[{caseId}] expected relationship kind '{expectedRelationshipKind}'; index produced [{string.Join(", ", result.RelationshipKinds)}].");
        }

        Assert.True(
            result.MonitorReferenceAtBind,
            $"[{caseId}] no indexed reference at the bind marker position {item.RelativePath}({result.BindLine},{result.BindColumn}).");
    }

    [Fact(Skip = InformationalSkipReason)]
    public void Informational_multi_project_classlib_to_app_call()
    {
        _fixture.GetEvaluation("multi-project/classlib-to-app-call");
    }

    [Fact(Skip = InformationalSkipReason)]
    public void Informational_multi_project_shared_interface_app_implementation()
    {
        _fixture.GetEvaluation("multi-project/shared-interface-app-implementation");
    }

    [Fact(Skip = InformationalSkipReason)]
    public void Informational_project_system_global_using_file()
    {
        _fixture.GetEvaluation("project-system/global-using-file");
    }

    [Fact(Skip = InformationalSkipReason)]
    public void Informational_metadata_framework_method_call()
    {
        _fixture.GetEvaluation("metadata/framework-method-call");
    }

    [Fact(Skip = InformationalSkipReason)]
    public void Informational_metadata_framework_type_reference()
    {
        _fixture.GetEvaluation("metadata/framework-type-reference");
    }
}

/// <summary>
/// Synthesises the corpus project, indexes it, and binds every case with Roslyn exactly once
/// for the whole test class.
/// </summary>
public sealed class LanguageCorpusFixture : IAsyncLifetime
{
    private const string BindStart = "/*<bind>*/";
    private const string BindEnd = "/*</bind>*/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private Dictionary<string, LanguageCorpusEvaluation> _evaluations = [];

    public async Task InitializeAsync()
    {
        string repositoryRoot = ResolveRepositoryRoot(AppContext.BaseDirectory);
        string runRoot = Path.Combine(
            repositoryRoot,
            "runtime",
            "tests",
            "language-corpus",
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(runRoot);

        LanguageCorpusCase[] cases = LoadCorpusCases();
        string observedRoot = Path.Combine(runRoot, "CorpusProject");
        string projectPath = Path.Combine(observedRoot, "CorpusProject.csproj");
        string solutionPath = Path.Combine(observedRoot, "CorpusProject.slnx");
        Directory.CreateDirectory(observedRoot);

        Dictionary<LanguageCorpusCase, CorpusMarkup> corpus = [];
        foreach (LanguageCorpusCase item in cases)
        {
            CorpusMarkup markup = StripBindMarkers(item);
            corpus[item] = markup;
            WriteCorpusSource(observedRoot, item.RelativePath, markup.Source);
            foreach (CorpusSourceFile additionalSource in item.AdditionalSources)
            {
                WriteCorpusSource(observedRoot, additionalSource.RelativePath, additionalSource.Source);
            }
        }

        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(solutionPath, """
            <Solution>
              <Project Path="CorpusProject.csproj" />
            </Solution>
            """);

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, solutionPath, Path.Combine(runRoot, "MonitorRuntime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        SolutionIndexBuilder builder = new(new MSBuildWorkspaceLoader(), store);
        await builder.RebuildAsync(settings);

        IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        IReadOnlyList<IndexedReferenceRow> references = store.ListReferences();
        IReadOnlyDictionary<string, RoslynAnswer> roslynAnswers = BuildRoslynAnswers(observedRoot, corpus);

        Dictionary<string, LanguageCorpusEvaluation> evaluations = [];
        foreach (LanguageCorpusCase item in cases)
        {
            CorpusMarkup markup = corpus[item];
            RoslynAnswer answer = roslynAnswers[item.CaseId];
            IndexedSymbolRow? target = FindIndexedTarget(symbols, item);
            IReadOnlyList<IndexedReferenceRow> targetReferences = target is null
                ? []
                : references.Where(reference => reference.TargetStableKey == target.StableKey).ToArray();
            IReadOnlyList<IndexedSymbolRow> callers = target is null || item.ExpectedCallerCount is null
                ? []
                : FindCallers(symbols, targetReferences, target);
            IReadOnlyList<string> relationshipKinds = target is null || item.ExpectedRelationshipKinds.Count == 0
                ? []
                : ExpandRelationshipKinds(targetReferences.Select(reference => reference.ReferenceKind))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            FileLinePositionSpan bindSpan = CSharpSyntaxTree.ParseText(markup.Source, path: item.RelativePath)
                .GetLineSpan(new TextSpan(markup.BindStart, markup.BindLength));
            int bindLine = bindSpan.StartLinePosition.Line + 1;
            int bindColumn = bindSpan.StartLinePosition.Character + 1;
            bool monitorReferenceAtBind = targetReferences.Any(reference =>
                PathMatchesRelativePath(reference.FilePath, item.RelativePath)
                && reference.Line == bindLine
                && reference.Column == bindColumn);
            if (!item.ExpectedMonitorTarget)
            {
                monitorReferenceAtBind = true;
            }

            evaluations[item.CaseId] = new LanguageCorpusEvaluation(
                item,
                answer,
                target,
                targetReferences,
                callers,
                relationshipKinds,
                monitorReferenceAtBind,
                bindLine,
                bindColumn);
        }

        _evaluations = evaluations;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public LanguageCorpusEvaluation GetEvaluation(string caseId)
    {
        return _evaluations.TryGetValue(caseId, out LanguageCorpusEvaluation? evaluation)
            ? evaluation
            : throw new InvalidOperationException($"Unknown corpus case '{caseId}'.");
    }

    public static LanguageCorpusCase[] LoadCorpusCases()
    {
        string repositoryRoot = ResolveRepositoryRoot(AppContext.BaseDirectory);
        // The corpus moved here from tests/smoke/AIMonitor.LanguageCorpusSmokeTests when that
        // console runner was deleted (Phase 5). The fixture data is the valuable part and is
        // unchanged; only its owner changed.
        string corpusRoot = Path.Combine(repositoryRoot, "tests", "unit", "AIMonitor.Data.Tests", "Corpus");
        List<LanguageCorpusCase> cases = [];
        foreach (string expectedPath in Directory.EnumerateFiles(corpusRoot, "expected.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string caseRoot = Path.GetDirectoryName(expectedPath)!;
            CorpusExpected expected = JsonSerializer.Deserialize<CorpusExpected>(File.ReadAllText(expectedPath), JsonOptions)
                ?? throw new InvalidOperationException($"Unable to read corpus expected file: {expectedPath}");
            string sourcePath = Path.Combine(caseRoot, expected.SourceFile);
            cases.Add(new LanguageCorpusCase(
                NormalizePath(Path.GetRelativePath(corpusRoot, caseRoot)),
                expected.Name,
                expected.RelativePath,
                expected.ExpectedTargetRelativePath ?? expected.RelativePath,
                File.ReadAllText(sourcePath),
                expected.ExpectedKind,
                expected.ExpectedName,
                expected.ExpectedRoslynDisplay,
                expected.ExpectedMonitorTarget ?? true,
                expected.Informational ?? false,
                expected.ExpectedReferenceCount,
                expected.ExpectedCallerCount,
                expected.ExpectedRelationshipKinds ?? [],
                LoadAdditionalSources(caseRoot, expected.AdditionalSources)));
        }

        return cases.ToArray();
    }

    private static void WriteCorpusSource(string observedRoot, string relativePath, string source)
    {
        string targetPath = Path.Combine(observedRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, source);
    }

    private static IReadOnlyList<CorpusSourceFile> LoadAdditionalSources(
        string caseRoot,
        IReadOnlyList<CorpusExpectedSource>? additionalSources)
    {
        if (additionalSources is null || additionalSources.Count == 0)
        {
            return [];
        }

        return additionalSources
            .Select(source => new CorpusSourceFile(
                source.RelativePath,
                File.ReadAllText(Path.Combine(caseRoot, source.SourceFile))))
            .ToArray();
    }

    private static CorpusMarkup StripBindMarkers(LanguageCorpusCase item)
    {
        int startMarker = item.Source.IndexOf(BindStart, StringComparison.Ordinal);
        int endMarker = item.Source.IndexOf(BindEnd, StringComparison.Ordinal);
        if (startMarker < 0 || endMarker < 0 || endMarker < startMarker)
        {
            throw new InvalidOperationException($"Corpus case '{item.Name}' is missing bind markers.");
        }

        string withoutStart = item.Source.Remove(startMarker, BindStart.Length);
        int adjustedEnd = endMarker - BindStart.Length;
        string source = withoutStart.Remove(adjustedEnd, BindEnd.Length);
        return new CorpusMarkup(source, startMarker, adjustedEnd - startMarker);
    }

    private static IReadOnlyDictionary<string, RoslynAnswer> BuildRoslynAnswers(
        string observedRoot,
        IReadOnlyDictionary<LanguageCorpusCase, CorpusMarkup> corpus)
    {
        SyntaxTree[] trees = corpus
            .SelectMany(item => item.Key.AllSources(item.Value.Source))
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => CSharpSyntaxTree.ParseText(
                group.First().Source,
                path: Path.Combine(observedRoot, group.Key)))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "AIMonitorLanguageCorpus",
            trees,
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Dictionary<string, RoslynAnswer> answers = [];
        foreach ((LanguageCorpusCase item, CorpusMarkup markup) in corpus)
        {
            SyntaxTree tree = trees.Single(candidate =>
                Path.GetFileName(candidate.FilePath).Equals(Path.GetFileName(item.RelativePath), StringComparison.OrdinalIgnoreCase));
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            SyntaxNode root = tree.GetRoot();
            SyntaxNode node = root.FindNode(new TextSpan(markup.BindStart, markup.BindLength), getInnermostNodeForTie: true);
            SyntaxToken token = root.FindToken(markup.BindStart);
            ISymbol? symbol = ResolveBoundSymbol(model, node, token);
            string display = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty;
            answers[item.CaseId] = new RoslynAnswer(symbol is not null, display);
        }

        return answers;
    }

    private static ISymbol? ResolveBoundSymbol(SemanticModel model, SyntaxNode node, SyntaxToken token)
    {
        if (node.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute
            && attribute.Name.Span.Contains(node.Span))
        {
            ISymbol? attributeSymbol = GetBestSymbol(model.GetSymbolInfo(attribute));
            return attributeSymbol is IMethodSymbol method ? method.ContainingType : attributeSymbol;
        }

        if (token.IsKind(SyntaxKind.AwaitKeyword)
            && token.Parent?.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is { AwaitKeyword.RawKind: not 0 } awaitUsingDeclaration)
        {
            TypeInfo typeInfo = model.GetTypeInfo(awaitUsingDeclaration.Declaration.Type);
            return typeInfo.Type?
                .GetMembers("DisposeAsync")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.Parameters.Length == 0);
        }

        SyntaxNode target = node;
        if (node is BinaryExpressionSyntax binary)
        {
            target = binary;
        }
        else if (node.Parent is BinaryExpressionSyntax parentBinary
            && parentBinary.OperatorToken.Span.Contains(node.Span))
        {
            target = parentBinary;
        }
        else if (node.Parent is ElementAccessExpressionSyntax elementAccess)
        {
            target = elementAccess;
        }
        else if (node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } creation
            && creation.Type.Span.Contains(node.Span))
        {
            target = creation;
        }
        else if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { } invocation
            && invocation.Expression.Span.Contains(node.Span))
        {
            target = invocation;
        }

        ISymbol? conversionSymbol = target is ExpressionSyntax expression
            ? model.GetConversion(expression).MethodSymbol
            : null;
        if (conversionSymbol is IMethodSymbol { MethodKind: MethodKind.Conversion })
        {
            return conversionSymbol;
        }

        return GetBestSymbol(model.GetSymbolInfo(target));
    }

    private static ISymbol? GetBestSymbol(SymbolInfo info)
    {
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private static IndexedSymbolRow? FindIndexedTarget(IReadOnlyList<IndexedSymbolRow> symbols, LanguageCorpusCase item)
    {
        return symbols.FirstOrDefault(symbol =>
            PathMatchesRelativePath(symbol.FilePath, item.ExpectedTargetRelativePath)
            && KindMatches(symbol.Kind, item.ExpectedKind)
            && (symbol.Name.Equals(item.ExpectedName, StringComparison.Ordinal)
                || symbol.Signature.Contains(item.ExpectedName, StringComparison.Ordinal)));
    }

    private static IReadOnlyList<IndexedSymbolRow> FindCallers(
        IReadOnlyList<IndexedSymbolRow> symbols,
        IReadOnlyList<IndexedReferenceRow> references,
        IndexedSymbolRow target)
    {
        return references
            .Where(reference => IsCallerReference(reference, target))
            .Select(reference => FindContainingCallable(symbols, reference))
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .DistinctBy(symbol => symbol.StableKey)
            .ToArray();
    }

    private static IndexedSymbolRow? FindContainingCallable(
        IReadOnlyList<IndexedSymbolRow> symbols,
        IndexedReferenceRow reference)
    {
        return symbols
            .Where(symbol => IsCallableKind(symbol.Kind))
            .Where(symbol => Path.GetFullPath(symbol.FilePath).Equals(Path.GetFullPath(reference.FilePath), StringComparison.OrdinalIgnoreCase))
            .Where(symbol => symbol.StartLine <= reference.Line && reference.Line <= symbol.EndLine)
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .ThenByDescending(symbol => symbol.StartLine)
            .FirstOrDefault();
    }

    private static bool IsCallableKind(string kind)
    {
        return kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Constructor", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Property", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Event", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCallerReference(IndexedReferenceRow reference, IndexedSymbolRow target)
    {
        if (IsRelationshipKind(reference.ReferenceKind))
        {
            return false;
        }

        string line = File.Exists(reference.FilePath)
            ? File.ReadLines(reference.FilePath).Skip(reference.Line - 1).FirstOrDefault() ?? string.Empty
            : string.Empty;
        if (line.Contains("nameof(", StringComparison.Ordinal))
        {
            return false;
        }

        bool bareMethodGroup = target.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
            && reference.Snippet.Equals(target.Name, StringComparison.Ordinal)
            && !line.Contains(target.Name + "(", StringComparison.Ordinal);
        return !bareMethodGroup;
    }

    private static bool IsRelationshipKind(string referenceKind)
    {
        return referenceKind is "partial_declaration"
            or "derived_type"
            or "inherits_from"
            or "overridden_by"
            or "overrides"
            or "implemented_by"
            or "implements_interface_member";
    }

    private static IEnumerable<string> ExpandRelationshipKinds(IEnumerable<string> referenceKinds)
    {
        foreach (string referenceKind in referenceKinds.Where(IsRelationshipKind))
        {
            yield return referenceKind;
            if (referenceKind.Equals("inherits_from", StringComparison.Ordinal))
            {
                yield return "derived_type";
            }
            else if (referenceKind.Equals("overrides", StringComparison.Ordinal))
            {
                yield return "overridden_by";
            }
            else if (referenceKind.Equals("implements_interface_member", StringComparison.Ordinal))
            {
                yield return "implemented_by";
            }
        }
    }

    private static bool KindMatches(string actualKind, string expectedKind)
    {
        return expectedKind switch
        {
            "class" or "record" or "interface" => actualKind.Equals("NamedType", StringComparison.OrdinalIgnoreCase),
            "constructor" or "method" or "operator" or "conversion" or "local_function" => actualKind.Equals("Method", StringComparison.OrdinalIgnoreCase),
            "field" or "enum_member" => actualKind.Equals("Field", StringComparison.OrdinalIgnoreCase),
            "property" or "indexer" => actualKind.Equals("Property", StringComparison.OrdinalIgnoreCase),
            "event" => actualKind.Equals("Event", StringComparison.OrdinalIgnoreCase),
            _ => actualKind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<MetadataReference> GetMetadataReferences()
    {
        string? trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrWhiteSpace(trustedAssemblies)
            ? []
            : trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(File.Exists)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool PathMatchesRelativePath(string fullPath, string relativePath)
    {
        string normalizedFullPath = NormalizePath(fullPath);
        string normalizedRelativePath = NormalizePath(relativePath);
        return normalizedFullPath.Equals(normalizedRelativePath, StringComparison.OrdinalIgnoreCase)
            || normalizedFullPath.EndsWith("/" + normalizedRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRepositoryRoot(string start)
    {
        DirectoryInfo? current = new(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClaudeWorkbench.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to resolve AIMonitor repository root.");
    }

    private sealed record CorpusMarkup(string Source, int BindStart, int BindLength);

    private sealed record CorpusExpected(
        string Name,
        string SourceFile,
        string RelativePath,
        string? ExpectedTargetRelativePath,
        string ExpectedKind,
        string ExpectedName,
        string ExpectedRoslynDisplay,
        bool? ExpectedMonitorTarget,
        bool? Informational,
        int ExpectedReferenceCount,
        int? ExpectedCallerCount,
        IReadOnlyList<string>? ExpectedRelationshipKinds = null,
        IReadOnlyList<CorpusExpectedSource>? AdditionalSources = null);

    private sealed record CorpusExpectedSource(string SourceFile, string RelativePath);
}

public sealed record CorpusSourceFile(string RelativePath, string Source);

public sealed record RoslynAnswer(bool TargetResolved, string TargetDisplay);

public sealed record LanguageCorpusCase(
    string CaseId,
    string Name,
    string RelativePath,
    string ExpectedTargetRelativePath,
    string Source,
    string ExpectedKind,
    string ExpectedName,
    string ExpectedRoslynDisplay,
    bool ExpectedMonitorTarget,
    bool Informational,
    int ExpectedReferenceCount,
    int? ExpectedCallerCount,
    IReadOnlyList<string> ExpectedRelationshipKinds,
    IReadOnlyList<CorpusSourceFile> AdditionalSources)
{
    public IEnumerable<CorpusSourceFile> AllSources(string primarySource)
    {
        yield return new CorpusSourceFile(RelativePath, primarySource);
        foreach (CorpusSourceFile source in AdditionalSources)
        {
            yield return source;
        }
    }
}

public sealed record LanguageCorpusEvaluation(
    LanguageCorpusCase Case,
    RoslynAnswer Roslyn,
    IndexedSymbolRow? IndexedTarget,
    IReadOnlyList<IndexedReferenceRow> References,
    IReadOnlyList<IndexedSymbolRow> Callers,
    IReadOnlyList<string> RelationshipKinds,
    bool MonitorReferenceAtBind,
    int BindLine,
    int BindColumn);
