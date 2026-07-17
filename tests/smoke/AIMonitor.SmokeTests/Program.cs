using AIMonitor.MSBuild;
using System.Text.Json;

namespace AIMonitor.SmokeTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        _ = args;

        string repositoryRoot = ResolveRepositoryRoot(AppContext.BaseDirectory);
        LocalSampleSmoke[] samples = ResolveLocalSamples(repositoryRoot);
        if (samples.Length == 0)
        {
            Console.WriteLine("AIMonitor smoke runner");
            Console.WriteLine("No local watched-solution smoke samples were found.");
            Console.WriteLine(@"Add roots to config\local-smoke-samples.json or set AIMONITOR_SMOKE_SAMPLE_ROOTS.");
            return 0;
        }

        Console.WriteLine("AIMonitor smoke runner");
        foreach (LocalSampleSmoke sample in samples)
        {
            await RunSampleAsync(sample);
        }

        return 0;
    }

    private static async Task RunSampleAsync(LocalSampleSmoke sample)
    {
        Console.WriteLine($"Sample: {sample.SolutionPath}");
        VerifyGrepAnchors(sample);

        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenSolutionAsync(sample.SolutionPath);
        IReadOnlyList<MSBuildDocumentSnapshot> documents = snapshot.Projects.SelectMany(project => project.Documents).ToArray();
        IReadOnlyList<MSBuildSymbolSnapshot> symbols = snapshot.Projects.SelectMany(project => project.Symbols).ToArray();
        IReadOnlyList<MSBuildReferenceSnapshot> references = snapshot.Projects.SelectMany(project => project.References).ToArray();

        Expect(snapshot.Projects.Count >= sample.MinimumProjectCount, $"Expected at least {sample.MinimumProjectCount} projects, indexed {snapshot.Projects.Count}.");
        Expect(documents.Count >= sample.MinimumDocumentCount, $"Expected at least {sample.MinimumDocumentCount} source documents, indexed {documents.Count}.");
        Expect(symbols.Count >= sample.MinimumSymbolCount, $"Expected at least {sample.MinimumSymbolCount} source symbols, indexed {symbols.Count}.");
        Expect(references.Count >= sample.MinimumReferenceCount, $"Expected at least {sample.MinimumReferenceCount} source references, indexed {references.Count}.");

        ExpectNoBuildOutputDocuments(documents);
        int verifiedRazorCodeBehindCount = VerifyRazorCodeBehindDocuments(sample, documents, symbols);
        foreach (ExpectedProject project in sample.Projects)
        {
            ExpectProject(snapshot, project.Name, project.TargetFramework);
        }

        foreach (ExpectedReference expectedReference in sample.References)
        {
            ExpectFileContains(sample.RootPath, expectedReference.DeclarationAnchor.RelativePath, expectedReference.DeclarationAnchor.ExpectedText);
            ExpectFileContains(sample.RootPath, expectedReference.ReferenceAnchor.RelativePath, expectedReference.ReferenceAnchor.ExpectedText);

            MSBuildSymbolSnapshot target = ExpectSymbol(
                symbols,
                expectedReference.SymbolName,
                expectedReference.SymbolKind,
                expectedReference.SignaturePrefix);
            ExpectReference(references, target, expectedReference.ReferenceAnchor);
        }

        Console.WriteLine($"Projects: {snapshot.Projects.Count}");
        Console.WriteLine($"Documents: {documents.Count}");
        Console.WriteLine($"Symbols: {symbols.Count}");
        Console.WriteLine($"References: {references.Count}");
        Console.WriteLine($"Grep-verified references: {sample.References.Count}");
        if (sample.VerifyAllRazorCodeBehindFiles)
        {
            Console.WriteLine($"Verified .razor.cs files: {verifiedRazorCodeBehindCount}");
        }

        Console.WriteLine($"{sample.Name} smoke passed.");
    }

    private static void VerifyGrepAnchors(LocalSampleSmoke sample)
    {
        foreach (ExpectedGrepAnchor anchor in sample.GrepAnchors)
        {
            ExpectFileContains(sample.RootPath, anchor.RelativePath, anchor.ExpectedText);
        }
    }

    private static void ExpectFileContains(string sampleRoot, string relativePath, string expectedText)
    {
        string path = Path.Combine(sampleRoot, relativePath);
        Expect(File.Exists(path), $"Grep anchor file is missing: {relativePath}");
        string text = File.ReadAllText(path);
        Expect(text.Contains(expectedText, StringComparison.Ordinal), $"Grep anchor text was not found in {relativePath}: {expectedText}");
    }

    private static void ExpectNoBuildOutputDocuments(IReadOnlyList<MSBuildDocumentSnapshot> documents)
    {
        MSBuildDocumentSnapshot? buildOutputDocument = documents.FirstOrDefault(document =>
            PathHasSegment(document.FilePath, "bin") || PathHasSegment(document.FilePath, "obj"));
        Expect(buildOutputDocument is null, $"Build output document was indexed: {buildOutputDocument?.FilePath}");
    }

    private static int VerifyRazorCodeBehindDocuments(
        LocalSampleSmoke sample,
        IReadOnlyList<MSBuildDocumentSnapshot> documents,
        IReadOnlyList<MSBuildSymbolSnapshot> symbols)
    {
        if (!sample.VerifyAllRazorCodeBehindFiles)
        {
            return 0;
        }

        string[] razorCodeBehindFiles = Directory
            .EnumerateFiles(sample.RootPath, "*.razor.cs", SearchOption.AllDirectories)
            .Where(path => !PathHasSegment(path, "bin"))
            .Where(path => !PathHasSegment(path, "obj"))
            .Where(path => !PathHasSegment(path, ".git"))
            .Where(path => !PathHasSegment(path, ".vs"))
            .Where(path => !PathHasSegment(path, "SourceBackups"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Expect(razorCodeBehindFiles.Length > 0, $"No .razor.cs files were found under {sample.RootPath}.");
        foreach (string filePath in razorCodeBehindFiles)
        {
            string relativePath = Path.GetRelativePath(sample.RootPath, filePath);
            string text = File.ReadAllText(filePath);
            bool isMixedRazorFile = LooksLikeMixedRazor(text);

            bool documentFound = documents.Any(document =>
                Path.GetFullPath(document.FilePath).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
            Expect(documentFound, $".razor.cs file was not indexed as a document: {relativePath}");

            if (isMixedRazorFile)
            {
                Expect(text.Contains("@code", StringComparison.Ordinal),
                    $"Mixed .razor.cs file did not contain an @code block: {relativePath}");
            }
            else
            {
                Expect(text.Contains("partial class", StringComparison.Ordinal),
                    $".razor.cs file does not contain an expected partial class declaration: {relativePath}");

                bool symbolFound = symbols.Any(symbol =>
                    Path.GetFullPath(symbol.FilePath).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
                Expect(symbolFound, $".razor.cs file did not contribute any indexed symbols: {relativePath}");
            }
        }

        return razorCodeBehindFiles.Length;
    }

    private static bool LooksLikeMixedRazor(string text)
    {
        return text.Contains("@code", StringComparison.Ordinal)
            || text.Contains("@page", StringComparison.Ordinal)
            || text.Contains("@using", StringComparison.Ordinal)
            || text.Contains("@inherits", StringComparison.Ordinal)
            || text.Contains("@inject", StringComparison.Ordinal);
    }

    private static void ExpectProject(MSBuildSolutionSnapshot snapshot, string name, string targetFramework)
    {
        MSBuildProjectSnapshot? project = snapshot.Projects.FirstOrDefault(project => project.Name.Equals(name, StringComparison.Ordinal));
        Expect(project is not null, $"Project was not indexed: {name}");
        Expect(project!.TargetFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase),
            $"Project {name} target framework was {project.TargetFramework}, expected {targetFramework}.");
    }

    private static MSBuildSymbolSnapshot ExpectSymbol(
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        string name,
        string kind,
        string signaturePrefix)
    {
        MSBuildSymbolSnapshot? symbol = symbols.FirstOrDefault(symbol =>
            symbol.Name.Equals(name, StringComparison.Ordinal)
            && symbol.Kind.Equals(kind, StringComparison.Ordinal)
            && symbol.Signature.StartsWith(signaturePrefix, StringComparison.Ordinal));
        Expect(symbol is not null, $"Symbol was not indexed: {kind} {signaturePrefix}");
        return symbol!;
    }

    private static void ExpectReference(
        IReadOnlyList<MSBuildReferenceSnapshot> references,
        MSBuildSymbolSnapshot target,
        ExpectedGrepAnchor referenceAnchor)
    {
        string expectedFileName = Path.GetFileName(referenceAnchor.RelativePath);
        bool found = references.Any(reference =>
            reference.TargetStableKey.Equals(target.StableKey, StringComparison.Ordinal)
            && Path.GetFileName(reference.FilePath).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)
            && reference.Snippet.Contains(referenceAnchor.ExpectedText, StringComparison.Ordinal));
        Expect(found, $"Reference to {target.Signature} was not found at grep anchor {referenceAnchor.RelativePath}: {referenceAnchor.ExpectedText}");
    }

    private static bool PathHasSegment(string filePath, string segment)
    {
        return filePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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

    private static LocalSampleSmoke[] ResolveLocalSamples(string repositoryRoot)
    {
        string[] configuredRoots = ResolveConfiguredSampleRoots(repositoryRoot);
        if (configuredRoots.Length > 0)
        {
            return configuredRoots
                .SelectMany(ResolveSolutionsFromRoot)
                .Select(CreateKnownOrGenericSample)
                .ToArray();
        }

        LocalSampleSmoke[] knownSamples =
        [
            CreateSchemaStudioWebViewerSample(@"C:\SchemaStudioWebViewer\SchemaStudioWebViewer.sln"),
            CreateSchemaStudioWebViewerSample(Path.Combine(repositoryRoot, "samples", "watched-solutions", "SchemaStudioWebViewer", "SchemaStudioWebViewer.sln")),
            CreateBlazorDetectorSample(Path.Combine(repositoryRoot, "samples", "watched-solutions", "BlazorDetectorSample", "BlazorDetectorSample.slnx")),
            CreateUSExcomManagerSample(@"C:\Source\USExcomManager\USExcomManager.sln")
        ];

        return knownSamples
            .Where(sample => File.Exists(sample.SolutionPath))
            .GroupBy(sample => sample.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string[] ResolveConfiguredSampleRoots(string repositoryRoot)
    {
        string? environmentRoots = Environment.GetEnvironmentVariable("AIMONITOR_SMOKE_SAMPLE_ROOTS");
        if (!string.IsNullOrWhiteSpace(environmentRoots))
        {
            return environmentRoots
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }

        string configPath = Path.Combine(repositoryRoot, "config", "local-smoke-samples.json");
        if (!File.Exists(configPath))
        {
            return [];
        }

        using FileStream stream = File.OpenRead(configPath);
        LocalSmokeSampleSettingsFile? file = JsonSerializer.Deserialize<LocalSmokeSampleSettingsFile>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return file?.SmokeSamples?.SampleRoots?
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .ToArray()
            ?? [];
    }

    private static IEnumerable<string> ResolveSolutionsFromRoot(string root)
    {
        string resolvedRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
        if (File.Exists(resolvedRoot)
            && (resolvedRoot.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || resolvedRoot.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            yield return resolvedRoot;
            yield break;
        }

        if (!Directory.Exists(resolvedRoot))
        {
            yield break;
        }

        foreach (string solutionPath in Directory.EnumerateFiles(resolvedRoot, "*.sln", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(resolvedRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return solutionPath;
        }
    }

    private static LocalSampleSmoke CreateKnownOrGenericSample(string solutionPath)
    {
        string name = Path.GetFileNameWithoutExtension(solutionPath);
        if (name.Equals("SchemaStudioWebViewer", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSchemaStudioWebViewerSample(solutionPath);
        }

        if (name.Equals("USExcomManager", StringComparison.OrdinalIgnoreCase))
        {
            return CreateUSExcomManagerSample(solutionPath);
        }

        if (name.Equals("BlazorDetectorSample", StringComparison.OrdinalIgnoreCase))
        {
            return CreateBlazorDetectorSample(solutionPath);
        }

        return CreateGenericSample(solutionPath);
    }

    private static LocalSampleSmoke CreateGenericSample(string solutionPath)
    {
        return new LocalSampleSmoke(
            Path.GetFileNameWithoutExtension(solutionPath),
            solutionPath,
            Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            MinimumProjectCount: 1,
            MinimumDocumentCount: 1,
            MinimumSymbolCount: 1,
            MinimumReferenceCount: 1,
            [],
            [],
            [],
            VerifyAllRazorCodeBehindFiles: false);
    }

    private static LocalSampleSmoke CreateSchemaStudioWebViewerSample(string solutionPath)
    {
        return new LocalSampleSmoke(
            "SchemaStudioWebViewer",
            solutionPath,
            Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            MinimumProjectCount: 4,
            MinimumDocumentCount: 70,
            MinimumSymbolCount: 400,
            MinimumReferenceCount: 800,
            [
                new ExpectedGrepAnchor(Path.Combine("SchemaStudio.Data", "Repositories", "DatabaseRepository.cs"), "public async Task<IReadOnlyList<DatabaseDefinition>> GetAllAsync()"),
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "DomainObjectModeler", "DomainObjectModeler.Selection.cs"), "DatabaseRepository.GetAllAsync()"),
                new ExpectedGrepAnchor(Path.Combine("WEBSemanticModel", "Services", "ViewParsingService.cs"), "public ParsedQuery ParseView(string database, string schema, string viewName)"),
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "ManageViews", "ManageViews.Parser.cs"), "ParserService.ParseView("),
                new ExpectedGrepAnchor(Path.Combine("WEBSemanticModel", "Binding", "ColumnBinder.cs"), "private static void BindFromSource(SelectItem item, SourceTable source, string column)"),
                new ExpectedGrepAnchor(Path.Combine("Models", "DisplaySchemaObject.cs"), "public bool IsBaseObject { get; set; }"),
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "ParserLab.razor"), "x.IsBaseObject")
            ],
            [
                new ExpectedProject("SchemaStudioWebViewer", "net9.0"),
                new ExpectedProject("SchemaStudio.Data", "net9.0"),
                new ExpectedProject("SchemaStudio.AIHelpers", "net9.0"),
                new ExpectedProject("SchemaStudioWebViewer.WEBSemanticModel", "net9.0")
            ],
            [
                new ExpectedReference(
                    "GetAllAsync",
                    "Method",
                    "SchemaStudio.Data.Repositories.DatabaseRepository.GetAllAsync()",
                    "DomainObjectModeler.Selection.cs",
                    new ExpectedGrepAnchor(Path.Combine("SchemaStudio.Data", "Repositories", "DatabaseRepository.cs"), "public async Task<IReadOnlyList<DatabaseDefinition>> GetAllAsync()"),
                    new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "DomainObjectModeler", "DomainObjectModeler.Selection.cs"), "DatabaseRepository.GetAllAsync()")),
                new ExpectedReference(
                    "ParseView",
                    "Method",
                    "SchemaStudioWebViewer.WEBSemanticModel.Services.ViewParsingService.ParseView(string, string, string)",
                    "ManageViews.Parser.cs",
                    new ExpectedGrepAnchor(Path.Combine("WEBSemanticModel", "Services", "ViewParsingService.cs"), "public ParsedQuery ParseView(string database, string schema, string viewName)"),
                    new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "ManageViews", "ManageViews.Parser.cs"), "ParserService.ParseView(")),
                new ExpectedReference(
                    "BindFromSource",
                    "Method",
                    "SchemaStudioWebViewer.WEBSemanticModel.Binding.ColumnBinder.BindFromSource",
                    "ColumnBinder.cs",
                    new ExpectedGrepAnchor(Path.Combine("WEBSemanticModel", "Binding", "ColumnBinder.cs"), "private static void BindFromSource(SelectItem item, SourceTable source, string column)"),
                    new ExpectedGrepAnchor(Path.Combine("WEBSemanticModel", "Binding", "ColumnBinder.cs"), "BindFromSource(item, source, col)")),
                new ExpectedReference(
                    "IsBaseObject",
                    "Property",
                    "SchemaStudioWebViewer.Models.DisplaySchemaObject.IsBaseObject",
                    "ParserLab.razor",
                    new ExpectedGrepAnchor(Path.Combine("Models", "DisplaySchemaObject.cs"), "public bool IsBaseObject { get; set; }"),
                    new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "ParserLab.razor"), "x.IsBaseObject"))
            ],
            VerifyAllRazorCodeBehindFiles: true);
    }

    private static LocalSampleSmoke CreateBlazorDetectorSample(string solutionPath)
    {
        return new LocalSampleSmoke(
            "BlazorDetectorSample",
            solutionPath,
            Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            MinimumProjectCount: 1,
            MinimumDocumentCount: 8,
            MinimumSymbolCount: 12,
            MinimumReferenceCount: 4,
            [
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "Detector", "SplitProbe.razor.cs"), "public partial class SplitProbe"),
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "Detector", "LegacyMixed.razor.cs"), "@code {"),
                new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "Detector", "LegacyMixed.razor.cs"), "@MixedModel.DisplayName")
            ],
            [
                new ExpectedProject("BlazorDetectorSample", "net10.0")
            ],
            [
                new ExpectedReference(
                    "DisplayName",
                    "Property",
                    "BlazorDetectorSample.Models.DetectorModel.DisplayName",
                    "SplitProbe.razor.cs",
                    new ExpectedGrepAnchor(Path.Combine("Models", "DetectorModel.cs"), "public string DisplayName { get; set; } = \"Detector\";"),
                    new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "Detector", "SplitProbe.razor.cs"), "DisplayName")),
                new ExpectedReference(
                    "DisplayName",
                    "Property",
                    "BlazorDetectorSample.Models.DetectorModel.DisplayName",
                    "LegacyMixed.razor.cs",
                    new ExpectedGrepAnchor(Path.Combine("Models", "DetectorModel.cs"), "public string DisplayName { get; set; } = \"Detector\";"),
                    new ExpectedGrepAnchor(Path.Combine("Components", "Pages", "Detector", "LegacyMixed.razor.cs"), "DisplayName"))
            ],
            VerifyAllRazorCodeBehindFiles: true);
    }

    private static LocalSampleSmoke CreateUSExcomManagerSample(string solutionPath)
    {
        return new LocalSampleSmoke(
            "USExcomManager",
            solutionPath,
            Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            MinimumProjectCount: 1,
            MinimumDocumentCount: 30,
            MinimumSymbolCount: 150,
            MinimumReferenceCount: 300,
            [
                new ExpectedGrepAnchor(Path.Combine("DAXGenerator", "DaxGenerator.cs"), "public DaxCompilationResult GenerateAll(FinancialPage page, IReadOnlyList<ReportRowDTO> rows)"),
                new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessorSQL.cs"), "_compilationResult = generator.GenerateAll(SelectedSheet, IngestedRows);"),
                new ExpectedGrepAnchor(Path.Combine("CubeMemberParser", "CubeMemberParser.cs"), "public CubeMemberParseResult TryParseToFilter(string formula)"),
                new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessor.cs"), "var parseResult = _parser.TryParseToFilter(rawFormula);"),
                new ExpectedGrepAnchor(Path.Combine("AppConfig", "DBConnection.cs"), "public string BuildConnectionString()"),
                new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessorSQL.cs"), "ExcomManagerConfig.Current.Connection.BuildConnectionString()")
            ],
            [
                new ExpectedProject("USExcomManager", "net8.0-windows")
            ],
            [
                new ExpectedReference(
                    "GenerateAll",
                    "Method",
                    "USExcomManager.DAXGenerator.FinancialSheetDaxGenerator.GenerateAll",
                    "ExcelProcessorSQL.cs",
                    new ExpectedGrepAnchor(Path.Combine("DAXGenerator", "DaxGenerator.cs"), "public DaxCompilationResult GenerateAll(FinancialPage page, IReadOnlyList<ReportRowDTO> rows)"),
                    new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessorSQL.cs"), "generator.GenerateAll(SelectedSheet, IngestedRows)")),
                new ExpectedReference(
                    "TryParseToFilter",
                    "Method",
                    "USExcomManager.CubeMemberParser.MemberParser.TryParseToFilter(string)",
                    "ExcelProcessor.cs",
                    new ExpectedGrepAnchor(Path.Combine("CubeMemberParser", "CubeMemberParser.cs"), "public CubeMemberParseResult TryParseToFilter(string formula)"),
                    new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessor.cs"), "_parser.TryParseToFilter(rawFormula)")),
                new ExpectedReference(
                    "BuildConnectionString",
                    "Method",
                    "USExcomManager.AppConfig.DBConnection.BuildConnectionString()",
                    "ExcelProcessorSQL.cs",
                    new ExpectedGrepAnchor(Path.Combine("AppConfig", "DBConnection.cs"), "public string BuildConnectionString()"),
                    new ExpectedGrepAnchor(Path.Combine("UI", "ExcelProcessorSQL.cs"), "ExcomManagerConfig.Current.Connection.BuildConnectionString()"))
            ],
            VerifyAllRazorCodeBehindFiles: false);
    }

    private sealed record LocalSampleSmoke(
        string Name,
        string SolutionPath,
        string RootPath,
        int MinimumProjectCount,
        int MinimumDocumentCount,
        int MinimumSymbolCount,
        int MinimumReferenceCount,
        IReadOnlyList<ExpectedGrepAnchor> GrepAnchors,
        IReadOnlyList<ExpectedProject> Projects,
        IReadOnlyList<ExpectedReference> References,
        bool VerifyAllRazorCodeBehindFiles);

    private sealed record ExpectedGrepAnchor(string RelativePath, string ExpectedText);

    private sealed record ExpectedProject(string Name, string TargetFramework);

    private sealed record ExpectedReference(
        string SymbolName,
        string SymbolKind,
        string SignaturePrefix,
        string ReferenceFileName,
        ExpectedGrepAnchor DeclarationAnchor,
        ExpectedGrepAnchor ReferenceAnchor);

    private sealed record LocalSmokeSampleSettingsFile(LocalSmokeSampleSettings? SmokeSamples);

    private sealed record LocalSmokeSampleSettings(IReadOnlyList<string?>? SampleRoots);
}
