using AIMonitor.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMonitor.Workflow;

public sealed class RoslynEditService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SourceMapJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SyntaxAnnotation FormatAnnotation = new("AIMonitorRoslynEditFormat");

    private readonly WorkflowEditService workflowService;
    private readonly WorkflowEditPaths paths;

    public RoslynEditService(MonitorSettings settings)
    {
        workflowService = new WorkflowEditService(settings);
        paths = new WorkflowEditPaths(settings);
    }

    public RoslynSourceMapResult GetSourceMap(string? path, string scope = "auto", string mode = "auto", string? namespaceName = null)
    {
        string effectiveScope = ResolveEffectiveScope(path, NormalizeScope(scope));
        string effectiveMode = ResolveEffectiveMode(effectiveScope, mode);
        string? requestedNamespace = effectiveScope.Equals("namespace", StringComparison.OrdinalIgnoreCase)
            ? namespaceName ?? path
            : namespaceName;
        string[] files = ResolveSourceMapFiles(path, effectiveScope, requestedNamespace).ToArray();
        RoslynSourceMapFile[] mappedFiles = files.Select(MapFile).Select(file => ShapeSourceMapFile(file, effectiveMode)).ToArray();
        string watchedProjectAlias = new DirectoryInfo(paths.Settings.WatchedProjectFolder).Name;
        string? watchedProjectFolder = effectiveMode.Equals("full", StringComparison.OrdinalIgnoreCase)
            ? paths.Settings.WatchedProjectFolder
            : null;
        RoslynSourceMapNextCall[] nextCalls = BuildSourceMapNextCalls(mappedFiles, effectiveMode).ToArray();
        int budgetLimit = GetSourceMapBudgetLimit(effectiveMode);
        RoslynSourceMapResult result = new(
            effectiveScope,
            effectiveMode,
            GetSourceMapModePurpose(effectiveMode),
            path,
            requestedNamespace,
            mappedFiles.Length,
            mappedFiles.Sum(file => file.Symbols.Count),
            mappedFiles,
            BudgetLimit: budgetLimit,
            WatchedProjectAlias: watchedProjectAlias,
            WatchedProjectFolder: watchedProjectFolder,
            SuggestedNextCalls: nextCalls.Length == 0 ? null : nextCalls);
        long estimatedTokenProxy = EstimateSourceMapTokenProxy(result);
        if (estimatedTokenProxy <= budgetLimit)
        {
            return result with { EstimatedTokenProxy = estimatedTokenProxy };
        }

        RoslynSourceMapNarrowingSuggestion[] suggestions = BuildSourceMapNarrowingSuggestions(mappedFiles).ToArray();
        return result with
        {
            FileCount = 0,
            SymbolCount = 0,
            Files = [],
            EstimatedTokenProxy = estimatedTokenProxy,
            WasTruncated = true,
            SuggestedNarrowing = suggestions
        };
    }

    public RoslynFileOutlineResult GetFileOutline(string watchedFilePath)
    {
        string fullPath = Path.GetFullPath(watchedFilePath);
        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CreateUnsupportedRoslynPathMessage(fullPath, "get_file_outline"));
        }

        string relativePath = paths.GetRelativeWatchedPath(fullPath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(fullPath), path: fullPath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Diagnostic[] diagnostics = root.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        RoslynFileOutlineItem[] items = diagnostics.Length == 0
            ? root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsOutlineMember).Select(member => MapOutlineItem(tree, member)).ToArray()
            : [];
        return new RoslynFileOutlineResult(
            fullPath,
            relativePath,
            diagnostics.Length == 0 ? "parsed" : "parse-error",
            diagnostics.Length,
            items);
    }

    public RoslynSymbolReadResult GetSymbol(string watchedFilePath, string symbolSelectorJson)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        RoslynSymbolSelector selector = ParseSymbolSelector(symbolSelectorJson);
        MemberDeclarationSyntax target = ResolveSingleMember(root, selector, status.RelativePath);
        FileLinePositionSpan span = root.SyntaxTree.GetLineSpan(target.Span);
        return new RoslynSymbolReadResult(
            status.WatchedFilePath,
            status.WorkingFilePath,
            status.RelativePath,
            SymbolKind(target),
            SymbolName(target),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            target.ToFullString());
    }

    public RoslynEditResult SubmitSymbol(string watchedFilePath, string symbolSelectorJson, string code, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        RoslynSymbolSelector selector = ParseSymbolSelector(symbolSelectorJson);
        MemberDeclarationSyntax target = ResolveSingleMember(root, selector, status.RelativePath);
        MemberDeclarationSyntax replacement = ParseMemberDeclaration(code, "replacement symbol")
            .WithLeadingTrivia(target.GetLeadingTrivia())
            .WithTrailingTrivia(target.GetTrailingTrivia())
            .WithAdditionalAnnotations(FormatAnnotation);
        return WriteRoot("submit_symbol", status, root.ReplaceNode(target, replacement), manifestJson, validateOverlay);
    }

    public RoslynEditResult AddUsing(string watchedFilePath, string namespaceName, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        if (root.Usings.Any(usingDirective => string.Equals(usingDirective.Name?.ToString(), namespaceName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Using '{namespaceName}' already exists in {status.RelativePath}.");
        }

        UsingDirectiveSyntax newUsing = SyntaxFactory.ParseCompilationUnit($"using {namespaceName};{Environment.NewLine}")
            .Usings
            .Single();
        UsingDirectiveSyntax[] usings = root.Usings
            .Add(newUsing)
            .OrderBy(usingDirective => usingDirective.Name?.ToString(), StringComparer.Ordinal)
            .ToArray();
        return WriteRoot("add_using", status, root.WithUsings(SyntaxFactory.List(usings)), manifestJson, validateOverlay);
    }

    public RoslynEditResult RemoveUsing(string watchedFilePath, string namespaceName, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        UsingDirectiveSyntax target = root.Usings.FirstOrDefault(usingDirective => string.Equals(usingDirective.Name?.ToString(), namespaceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Using '{namespaceName}' was not found in {status.RelativePath}.");
        CompilationUnitSyntax newRoot = root.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia)
            ?? throw new InvalidOperationException($"Using '{namespaceName}' could not be removed from {status.RelativePath}.");
        return WriteRoot("remove_using", status, newRoot, manifestJson, validateOverlay);
    }

    public RoslynEditResult SetTypePartial(string watchedFilePath, string containingType, bool isPartial, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        TypeDeclarationSyntax type = ResolveSingleType(root, containingType);
        bool currentlyPartial = type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        if (currentlyPartial == isPartial)
        {
            return WriteRoot("set_type_partial", status, root, manifestJson, validateOverlay);
        }

        TypeDeclarationSyntax newType = isPartial
            ? type.WithModifiers(type.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
            : type.WithModifiers(SyntaxFactory.TokenList(type.Modifiers.Where(modifier => !modifier.IsKind(SyntaxKind.PartialKeyword))));
        return WriteRoot("set_type_partial", status, root.ReplaceNode(type, newType.WithAdditionalAnnotations(FormatAnnotation)), manifestJson, validateOverlay);
    }

    public RoslynEditResult AddSymbol(string watchedFilePath, string containingType, string symbolType, string code, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        TypeDeclarationSyntax type = ResolveSingleType(root, containingType);
        MemberDeclarationSyntax newMember = ParseMemberDeclaration(code, "new candidate symbol");
        if (!SymbolKind(newMember).Equals(symbolType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"New symbol kind '{SymbolKind(newMember)}' does not match requested kind '{symbolType}'.");
        }

        SyntaxList<MemberDeclarationSyntax> members = type.Members;
        int insertIndex = members.Count;
        if (!string.IsNullOrWhiteSpace(afterSymbol))
        {
            int afterIndex = IndexOfMember(members, afterSymbol);
            if (afterIndex < 0)
            {
                throw new InvalidOperationException($"afterSymbol '{afterSymbol}' was not found in type '{containingType}'.");
            }

            insertIndex = afterIndex + 1;
        }

        newMember = ApplyInsertionTrivia(newMember, type, insertIndex).WithAdditionalAnnotations(FormatAnnotation);
        TypeDeclarationSyntax newType = type.WithMembers(members.Insert(insertIndex, newMember));
        return WriteRoot("add_symbol", status, root.ReplaceNode(type, newType), manifestJson, validateOverlay);
    }

    public RoslynEditResult AddField(string watchedFilePath, string containingType, string declaration, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        return AddSymbol(watchedFilePath, containingType, "field", declaration, afterSymbol, manifestJson, validateOverlay);
    }

    public RoslynEditResult AddProperty(string watchedFilePath, string containingType, string declaration, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        return AddSymbol(watchedFilePath, containingType, "property", declaration, afterSymbol, manifestJson, validateOverlay);
    }

    public RoslynEditResult AddMethod(string watchedFilePath, string containingType, string declaration, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        return AddSymbol(watchedFilePath, containingType, "method", declaration, afterSymbol, manifestJson, validateOverlay);
    }

    public RoslynEditResult AddConstructor(string watchedFilePath, string containingType, string declaration, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        return AddSymbol(watchedFilePath, containingType, "constructor", declaration, afterSymbol, manifestJson, validateOverlay);
    }

    public RoslynEditResult AddNestedType(string watchedFilePath, string containingType, string declaration, string? afterSymbol = null, string? manifestJson = null, bool validateOverlay = true)
    {
        MemberDeclarationSyntax member = ParseMemberDeclaration(declaration, "new nested type");
        string kind = SymbolKind(member);
        if (kind is not ("class" or "struct" or "interface" or "record" or "enum"))
        {
            throw new InvalidOperationException($"Nested type declaration must be class, struct, interface, record, or enum. Actual kind: '{kind}'.");
        }

        return AddSymbol(watchedFilePath, containingType, kind, declaration, afterSymbol, manifestJson, validateOverlay);
    }

    public RoslynEditResult RemoveSymbol(string watchedFilePath, string symbolSelectorJson, string? manifestJson = null, bool validateOverlay = true)
    {
        EditSessionStatus status = EnsureSession(watchedFilePath);
        CompilationUnitSyntax root = ParseCompilationUnit(status.WorkingFilePath, status.RelativePath);
        RoslynSymbolSelector selector = ParseSymbolSelector(symbolSelectorJson);
        MemberDeclarationSyntax target = ResolveSingleMember(root, selector, status.RelativePath);
        CompilationUnitSyntax newRoot = root.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia)
            ?? throw new InvalidOperationException($"Symbol '{selector.Name}' could not be removed from {status.RelativePath}.");
        return WriteRoot("remove_symbol", status, newRoot, manifestJson, validateOverlay);
    }

    private IEnumerable<string> ResolveSourceMapFiles(string? requestedPath, string scope, string? namespaceName)
    {
        string root = paths.Settings.WatchedProjectFolder;
        if (scope.Equals("project", StringComparison.OrdinalIgnoreCase)
            || scope.Equals("auto", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(requestedPath))
        {
            return EnumerateSourceFiles(root);
        }

        if (scope.Equals("namespace", StringComparison.OrdinalIgnoreCase))
        {
            return EnumerateSourceFiles(root).Where(file => FileContainsNamespace(file, namespaceName));
        }

        string targetPath = string.IsNullOrWhiteSpace(requestedPath)
            ? root
            : Path.IsPathRooted(requestedPath)
                ? Path.GetFullPath(requestedPath)
                : Path.GetFullPath(Path.Combine(root, requestedPath));
        paths.GetRelativeWatchedPath(targetPath);
        if (File.Exists(targetPath))
        {
            return targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? [targetPath]
                : throw new InvalidOperationException(CreateUnsupportedRoslynPathMessage(targetPath, "get_source_map"));
        }

        if (Directory.Exists(targetPath))
        {
            return EnumerateSourceFiles(targetPath);
        }

        throw new FileNotFoundException("Source map target file or folder was not found.", targetPath);
    }

    private RoslynSourceMapFile MapFile(string filePath)
    {
        string relativePath = paths.GetRelativeWatchedPath(filePath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), path: filePath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Diagnostic[] diagnostics = root.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        RoslynSourceMapSymbol[] symbols = diagnostics.Length == 0
            ? root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsOutlineMember).Select(member => MapSymbol(tree, relativePath, member)).ToArray()
            : [];
        return new RoslynSourceMapFile(
            filePath,
            relativePath,
            diagnostics.Length == 0 ? "parsed" : "parse-error",
            diagnostics.Length,
            root.Usings.Select(usingDirective => usingDirective.Name?.ToString() ?? string.Empty).Where(value => value.Length > 0).ToArray(),
            root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(item => item.Name.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
            symbols,
            FileHash.Compute(filePath),
            new FileInfo(filePath).Length,
            diagnostics.Select(MapDiagnostic).Take(10).ToArray());
    }

    private static RoslynSourceMapSymbol MapSymbol(SyntaxTree tree, string relativePath, MemberDeclarationSyntax member)
    {
        FileLinePositionSpan span = tree.GetLineSpan(member.Span);
        string? elisionReason = GetElisionReason(relativePath, member);
        return new RoslynSourceMapSymbol(
            SymbolKind(member),
            SymbolName(member),
            BuildStableSymbolKey(relativePath, member),
            BuildSignature(member),
            BuildNamespace(member),
            BuildContainingType(member),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            ComputeHash(member.ToFullString()),
            GetModifiers(member),
            GetReturnType(member),
            GetParameterTypes(member),
            GetParameterNames(member),
            GetArity(member),
            member.Kind().ToString(),
            GetBaseTypes(member),
            GetAttributeSummaries(member),
            HasDocumentation(member),
            HasVisibleAttributes(member),
            HasModifier(member, SyntaxKind.StaticKeyword),
            HasModifier(member, SyntaxKind.AsyncKeyword),
            HasModifier(member, SyntaxKind.OverrideKeyword),
            HasModifier(member, SyntaxKind.VirtualKeyword),
            HasModifier(member, SyntaxKind.PartialKeyword),
            elisionReason is not null ? true : null,
            elisionReason);
    }

    private static RoslynFileOutlineItem MapOutlineItem(SyntaxTree tree, MemberDeclarationSyntax member)
    {
        FileLinePositionSpan span = tree.GetLineSpan(member.Span);
        return new RoslynFileOutlineItem(
            SymbolKind(member),
            SymbolName(member),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            BuildSignature(member),
            BuildNamespace(member),
            BuildContainingType(member),
            member.Kind().ToString());
    }

    private static RoslynSourceMapDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        return new RoslynSourceMapDiagnostic(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1);
    }

    private static RoslynSourceMapFile ShapeSourceMapFile(RoslynSourceMapFile file, string mode)
    {
        if (mode.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        RoslynSourceMapSymbol[] symbols = file.Symbols
            .Select(symbol => ShapeSourceMapSymbol(symbol, mode))
            .ToArray();

        if (mode.Equals("selector", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            return file with
            {
                SourceFilePath = null,
                DiagnosticsSummary = file.DiagnosticCount > 0 ? file.DiagnosticsSummary : null,
                Usings = NullIfEmpty(file.Usings),
                Namespaces = NullIfEmpty(file.Namespaces),
                Symbols = symbols
            };
        }

        return file with
        {
            SourceFilePath = null,
            Sha256 = null,
            Length = null,
            DiagnosticsSummary = file.DiagnosticCount > 0 ? file.DiagnosticsSummary : null,
            Usings = null,
            Namespaces = NullIfEmpty(file.Namespaces),
            Symbols = symbols
        };
    }

    private static RoslynSourceMapSymbol ShapeSourceMapSymbol(RoslynSourceMapSymbol symbol, string mode)
    {
        if (mode.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return symbol;
        }

        if (symbol.IsElided == true)
        {
            return symbol with
            {
                StableSymbolKey = null,
                Signature = null,
                TextHash = null,
                Modifiers = null,
                ReturnType = null,
                ParameterTypes = null,
                ParameterNames = null,
                Arity = null,
                SyntaxKind = null,
                BaseTypes = null,
                Attributes = null,
                HasDocumentation = null,
                HasAttributes = null,
                IsStatic = null,
                IsAsync = null,
                IsOverride = null,
                IsVirtual = null,
                IsPartial = null
            };
        }

        if (mode.Equals("selector", StringComparison.OrdinalIgnoreCase))
        {
            return symbol with
            {
                BaseTypes = NullIfEmpty(symbol.BaseTypes),
                Attributes = NullIfEmpty(ToAttributeNamesOnly(symbol.Attributes)),
                Modifiers = NullIfEmpty(symbol.Modifiers),
                ParameterTypes = NullIfEmpty(symbol.ParameterTypes),
                ParameterNames = NullIfEmpty(symbol.ParameterNames),
                IsPartial = symbol.IsPartial == true ? true : null
            };
        }

        if (mode.Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            return symbol with
            {
                BaseTypes = NullIfEmpty(symbol.BaseTypes),
                Attributes = NullIfEmpty(symbol.Attributes),
                Modifiers = NullIfEmpty(symbol.Modifiers),
                ParameterTypes = NullIfEmpty(symbol.ParameterTypes),
                ParameterNames = NullIfEmpty(symbol.ParameterNames),
                IsPartial = symbol.IsPartial == true ? true : null
            };
        }

        return symbol with
        {
            StableSymbolKey = null,
            Signature = null,
            TextHash = null,
            Modifiers = null,
            ReturnType = null,
            ParameterTypes = null,
            ParameterNames = null,
            Arity = null,
            SyntaxKind = null,
            BaseTypes = NullIfEmpty(symbol.BaseTypes),
            Attributes = NullIfEmpty(ToAttributeNamesOnly(symbol.Attributes)),
            HasDocumentation = null,
            IsStatic = null,
            IsAsync = null,
            IsOverride = null,
            IsVirtual = null,
            IsPartial = symbol.IsPartial == true ? true : null
        };
    }

    private static IReadOnlyList<RoslynSourceMapAttribute>? ToAttributeNamesOnly(IReadOnlyList<RoslynSourceMapAttribute>? attributes)
    {
        return attributes?.Select(attribute => new RoslynSourceMapAttribute(attribute.Name)).ToArray();
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T>? values)
    {
        return values is null || values.Count == 0 ? null : values;
    }

    private EditSessionStatus EnsureSession(string watchedFilePath)
    {
        string fullPath = Path.GetFullPath(watchedFilePath);
        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CreateUnsupportedRoslynPathMessage(fullPath, "Roslyn edit tools"));
        }

        return workflowService.EnsureEditableSession(fullPath);
    }

    private RoslynEditResult WriteRoot(string operation, EditSessionStatus status, CompilationUnitSyntax root, string? manifestJson, bool validateOverlay)
    {
        CompilationUnitSyntax formatted = FormatAnnotatedNodes(root);
        EditSessionStatus updatedStatus = workflowService.WriteWorkingCandidate(status.WatchedFilePath, formatted.ToFullString(), manifestJson, validateOverlay);
        return new RoslynEditResult(
            operation,
            updatedStatus.WatchedFilePath,
            updatedStatus.WorkingFilePath,
            updatedStatus.RelativePath,
            "updated",
            $"{operation} updated the monitor-owned Working candidate.",
            FileHash.Compute(updatedStatus.WorkingFilePath),
            updatedStatus.OperationCount,
            string.IsNullOrWhiteSpace(updatedStatus.ManifestJson) ? null : updatedStatus.ManifestJson,
            updatedStatus.SyntaxValidation,
            updatedStatus.OverlayValidation);
    }

    private static CompilationUnitSyntax ParseCompilationUnit(string filePath, string relativePath)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), path: filePath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Diagnostic[] diagnostics = root.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (diagnostics.Length > 0)
        {
            throw new InvalidOperationException($"C# parse failed for {relativePath}: {diagnostics[0].GetMessage()}");
        }

        return root;
    }

    private static RoslynSymbolSelector ParseSymbolSelector(string symbolSelectorJson)
    {
        return JsonSerializer.Deserialize<RoslynSymbolSelector>(symbolSelectorJson, JsonOptions)
            ?? throw new InvalidOperationException("symbolSelectorJson could not be parsed.");
    }

    private static MemberDeclarationSyntax ParseMemberDeclaration(string code, string label)
    {
        MemberDeclarationSyntax member = SyntaxFactory.ParseMemberDeclaration(code)
            ?? throw new InvalidOperationException($"{label} is not a complete C# member declaration.");
        Diagnostic[] diagnostics = member.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (diagnostics.Length > 0)
        {
            throw new InvalidOperationException($"{label} has syntax errors: {diagnostics[0].GetMessage()}");
        }

        return member;
    }

    private static MemberDeclarationSyntax ResolveSingleMember(CompilationUnitSyntax root, RoslynSymbolSelector selector, string relativePath)
    {
        MemberDeclarationSyntax[] matches = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(IsOutlineMember)
            .Where(member => MatchesSelector(member, selector, relativePath))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Symbol '{selector.Name}' was not found."),
            _ => throw new InvalidOperationException($"Symbol selector for '{selector.Name}' is ambiguous. Add containingType, memberKind, parameterTypes, or stableSymbolKey.")
        };
    }

    private static bool MatchesSelector(MemberDeclarationSyntax member, RoslynSymbolSelector selector, string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(selector.StableSymbolKey)
            && !BuildStableSymbolKey(relativePath, member).Equals(selector.StableSymbolKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.MemberKind)
            && !SymbolKind(member).Equals(selector.MemberKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.Name)
            && !SymbolName(member).Equals(selector.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.ContainingNamespace)
            && !BuildNamespace(member).Equals(selector.ContainingNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.ContainingType)
            && !string.Equals(BuildContainingType(member), selector.ContainingType, StringComparison.Ordinal))
        {
            return false;
        }

        if (selector.Arity is not null && GetArity(member) != selector.Arity.Value)
        {
            return false;
        }

        return selector.ParameterTypes is not { Count: > 0 } || ParameterTypesMatch(member, selector.ParameterTypes);
    }

    private static TypeDeclarationSyntax ResolveSingleType(CompilationUnitSyntax root, string containingType)
    {
        TypeDeclarationSyntax[] matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => MatchesContainingType(type, containingType))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Containing type '{containingType}' was not found. Use the containing type shown by get_source_map (its simple name, e.g. 'Calculator', or the namespace-qualified name)."),
            _ => throw new InvalidOperationException(
                $"Containing type '{containingType}' is ambiguous; qualify it with its namespace as shown by get_source_map.")
        };
    }

    // Match a candidate type against the caller's containingType in whatever form they passed:
    // the simple name ('Calculator'), the nested chain ('Outer.Inner'), or the fully
    // namespace-qualified name ('Ns.Outer.Inner'). The source map reports the simple/nested form,
    // but an agent commonly composes the namespace-qualified name, so all are honored. (Previously
    // a dotted name was compared to BuildContainingType(type) -- the ENCLOSING-type chain, which is
    // null for a top-level type -- so a namespace-qualified name never matched; see the
    // typed-symbol resolve bug.)
    private static bool MatchesContainingType(TypeDeclarationSyntax type, string containingType)
    {
        string simple = type.Identifier.ValueText;
        if (containingType.Equals(simple, StringComparison.Ordinal))
        {
            return true;
        }

        string? enclosing = BuildContainingType(type);
        string nested = string.IsNullOrEmpty(enclosing) ? simple : $"{enclosing}.{simple}";
        if (containingType.Equals(nested, StringComparison.Ordinal))
        {
            return true;
        }

        string ns = BuildNamespace(type);
        string qualified = string.IsNullOrEmpty(ns) ? nested : $"{ns}.{nested}";
        return containingType.Equals(qualified, StringComparison.Ordinal);
    }

    private static int IndexOfMember(SyntaxList<MemberDeclarationSyntax> members, string symbolName)
    {
        for (int index = 0; index < members.Count; index++)
        {
            if (SymbolName(members[index]).Equals(symbolName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static MemberDeclarationSyntax ApplyInsertionTrivia(MemberDeclarationSyntax newMember, TypeDeclarationSyntax type, int insertIndex)
    {
        SyntaxList<MemberDeclarationSyntax> members = type.Members;
        if (members.Count == 0)
        {
            return newMember
                .WithoutLeadingTrivia()
                .WithLeadingTrivia(SyntaxFactory.Whitespace("        "))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        }

        MemberDeclarationSyntax indentationSource = insertIndex < members.Count
            ? members[insertIndex]
            : members[^1];
        string indentation = GetDeclarationIndentation(indentationSource);
        return newMember
            .WithoutLeadingTrivia()
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indentation))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
    }

    private static string GetDeclarationIndentation(MemberDeclarationSyntax member)
    {
        string leadingText = member.GetLeadingTrivia().ToFullString();
        int lineStart = Math.Max(leadingText.LastIndexOf('\n'), leadingText.LastIndexOf('\r'));
        string indentation = lineStart >= 0 ? leadingText[(lineStart + 1)..] : leadingText;
        return !string.IsNullOrEmpty(indentation) && indentation.All(char.IsWhiteSpace)
            ? indentation
            : "        ";
    }

    private static CompilationUnitSyntax FormatAnnotatedNodes(CompilationUnitSyntax root)
    {
        using AdhocWorkspace workspace = new();
        SyntaxNode formatted = Formatter.Format(root, FormatAnnotation, workspace);
        return (CompilationUnitSyntax)formatted;
    }

    private IEnumerable<string> EnumerateSourceFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderBuildOrHiddenDirectory(path))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static bool FileContainsNamespace(string filePath, string? namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return true;
        }

        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), path: filePath).GetCompilationUnitRoot();
        return root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Any(item => item.Name.ToString().Equals(namespaceName, StringComparison.Ordinal));
    }

    private static string NormalizeScope(string? scope)
    {
        string normalized = string.IsNullOrWhiteSpace(scope) ? "auto" : scope.Trim().ToLowerInvariant();
        return normalized is "auto" or "file" or "folder" or "namespace" or "project"
            ? normalized
            : throw new InvalidOperationException("Source map scope must be auto, file, folder, namespace, or project.");
    }

    private string ResolveEffectiveScope(string? path, string scope)
    {
        if (!scope.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "project";
        }

        string targetPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(paths.Settings.WatchedProjectFolder, path));
        return File.Exists(targetPath) ? "file"
            : Directory.Exists(targetPath) ? "folder"
            : "project";
    }

    private static string CreateUnsupportedRoslynPathMessage(string path, string toolName)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return $"{toolName} uses the C# Roslyn workflow surface and cannot read or edit Razor markup directly. Use a .razor.cs code-behind file for Roslyn symbol tools, or use get_file/submit_file/replace_text_in_file/replace_span_in_file against the monitor-owned Working candidate for markup edits.";
        }

        return $"{toolName} currently supports C# source files only. Use a .cs file for Roslyn symbol tools, or use the text/file workflow tools for {extension} files.";
    }

    private static string NormalizeMode(string? mode)
    {
        string normalized = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        return normalized is "auto" or "navigation" or "selector" or "detail" or "full"
            ? normalized
            : throw new InvalidOperationException("Source map mode must be auto, navigation, selector, detail, or full.");
    }

    private static string ResolveEffectiveMode(string scope, string? mode)
    {
        string normalized = NormalizeMode(mode);
        if (!normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return scope.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? "selector"
            : "navigation";
    }

    private static int GetSourceMapBudgetLimit(string mode)
    {
        return mode switch
        {
            "navigation" => 20000,
            "selector" => 25000,
            "detail" => 20000,
            "full" => 15000,
            _ => 15000
        };
    }

    private static string GetSourceMapModePurpose(string mode)
    {
        return mode.Equals("navigation", StringComparison.OrdinalIgnoreCase) ? "broad-orientation"
            : mode.Equals("selector", StringComparison.OrdinalIgnoreCase) ? "stable-symbol-selection"
            : mode.Equals("detail", StringComparison.OrdinalIgnoreCase) ? "contract-detail"
            : "audit-debug";
    }

    private static long EstimateSourceMapTokenProxy(RoslynSourceMapResult result)
    {
        string json = JsonSerializer.Serialize(result, SourceMapJsonOptions);
        return Math.Max(1, (json.Length + 3L) / 4L);
    }

    private static IEnumerable<RoslynSourceMapNarrowingSuggestion> BuildSourceMapNarrowingSuggestions(IReadOnlyList<RoslynSourceMapFile> files)
    {
        return files
            .OrderByDescending(file => file.Symbols.Count)
            .ThenByDescending(file => file.DiagnosticCount)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(file => new RoslynSourceMapNarrowingSuggestion(
                file.RelativePath,
                file.DiagnosticCount > 0 ? "diagnostics-present" : "high-symbol-count",
                file.Symbols.Count,
                file.DiagnosticCount));
    }

    private static IEnumerable<RoslynSourceMapNextCall> BuildSourceMapNextCalls(IReadOnlyList<RoslynSourceMapFile> files, string mode)
    {
        if (mode.Equals("navigation", StringComparison.OrdinalIgnoreCase))
        {
            List<RoslynSourceMapNextCall> calls = files
                .OrderByDescending(file => file.DiagnosticCount)
                .ThenByDescending(file => file.Symbols.Count)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select((file, index) => new RoslynSourceMapNextCall(
                    index + 1,
                    "get_source_map",
                    file.DiagnosticCount > 0 ? "inspect-file-with-diagnostics" : "inspect-file-selectors",
                    new Dictionary<string, string>
                    {
                        ["path"] = file.RelativePath,
                        ["scope"] = "file",
                        ["mode"] = "selector"
                    }))
                .ToList();
            AddUsingNamespaceNextCalls(calls, files, calls.Count + 1);
            return calls;
        }

        if (mode.Equals("selector", StringComparison.OrdinalIgnoreCase))
        {
            List<RoslynSourceMapNextCall> calls = files
                .SelectMany(file => file.Symbols
                    .Where(symbol => !string.IsNullOrWhiteSpace(symbol.StableSymbolKey))
                    .Where(symbol => symbol.Kind is "method" or "constructor" or "property" or "event" or "field")
                    .OrderBy(symbol => SourceMapSymbolNextCallRank(symbol.Kind))
                    .ThenBy(symbol => symbol.StartLine)
                    .Select(symbol => new { File = file, Symbol = symbol }))
                .Take(10)
                .Select((item, index) => new RoslynSourceMapNextCall(
                    index + 1,
                    "get_symbol",
                    "read-selected-symbol-body",
                    new Dictionary<string, string>
                    {
                        ["path"] = item.File.RelativePath,
                        ["symbolSelectorJson"] = BuildStableKeySelectorJson(item.Symbol)
                    }))
                .ToList();
            AddUsingNamespaceNextCalls(calls, files, calls.Count + 1);
            return calls;
        }

        return [];
    }

    private static void AddUsingNamespaceNextCalls(List<RoslynSourceMapNextCall> calls, IReadOnlyList<RoslynSourceMapFile> files, int startRank)
    {
        foreach (string usingNamespace in files
            .SelectMany(file => file.Usings ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(6))
        {
            calls.Add(new RoslynSourceMapNextCall(
                startRank++,
                "get_source_map",
                "inspect-referenced-namespace-surface",
                new Dictionary<string, string>
                {
                    ["scope"] = "namespace",
                    ["namespaceName"] = usingNamespace,
                    ["mode"] = "navigation"
                }));
        }
    }

    private static int SourceMapSymbolNextCallRank(string kind)
    {
        return kind switch
        {
            "method" => 0,
            "constructor" => 1,
            "property" => 2,
            "event" => 3,
            "field" => 4,
            _ => 9
        };
    }

    private static string BuildStableKeySelectorJson(RoslynSourceMapSymbol symbol)
    {
        return JsonSerializer.Serialize(new RoslynSymbolSelector(StableSymbolKey: symbol.StableSymbolKey), SourceMapJsonOptions);
    }

    private static string BuildStableSymbolKey(string relativePath, MemberDeclarationSyntax member)
    {
        string namespaceName = BuildNamespace(member);
        string containingType = BuildContainingType(member) ?? string.Empty;
        string signatureKey = member switch
        {
            MethodDeclarationSyntax method => $"{method.Identifier.ValueText}({string.Join(",", method.ParameterList.Parameters.Select(ParameterKey))})",
            ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}({string.Join(",", constructor.ParameterList.Parameters.Select(ParameterKey))})",
            DelegateDeclarationSyntax del => $"{del.Identifier.ValueText}({string.Join(",", del.ParameterList.Parameters.Select(ParameterKey))})",
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax evt => evt.Identifier.ValueText,
            EventFieldDeclarationSyntax eventField => string.Join(",", eventField.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            _ => SymbolName(member)
        };
        return $"{NormalizePath(relativePath)}::{namespaceName}::{containingType}::{SymbolKind(member)}::{signatureKey}";
    }

    private static string ParameterKey(ParameterSyntax parameter)
    {
        string modifier = parameter.Modifiers.ToFullString().Trim();
        string type = parameter.Type?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(modifier) ? type : $"{modifier} {type}";
    }

    private static string BuildNamespace(SyntaxNode node)
    {
        string[] names = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .ToArray();
        return string.Join(".", names);
    }

    private static string? BuildContainingType(MemberDeclarationSyntax member)
    {
        string[] names = member.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(type => type.Identifier.ValueText)
            .ToArray();
        return names.Length == 0 ? null : string.Join(".", names);
    }

    private static int GetArity(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.TypeParameterList?.Parameters.Count ?? 0,
            TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            DelegateDeclarationSyntax del => del.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0
        };
    }

    private static bool ParameterTypesMatch(MemberDeclarationSyntax member, IReadOnlyList<string> expected)
    {
        SeparatedSyntaxList<ParameterSyntax>? parameters = member switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters,
            ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters,
            DelegateDeclarationSyntax del => del.ParameterList.Parameters,
            _ => null
        };
        if (parameters is null || parameters.Value.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            string actualType = parameters.Value[index].Type?.ToString() ?? string.Empty;
            if (!actualType.Equals(expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOutlineMember(MemberDeclarationSyntax member)
    {
        return member is BaseTypeDeclarationSyntax
            or MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or PropertyDeclarationSyntax
            or FieldDeclarationSyntax
            or EventFieldDeclarationSyntax
            or EventDeclarationSyntax
            or DelegateDeclarationSyntax;
    }

    private static string SymbolKind(MemberDeclarationSyntax member)
    {
        return member switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            EnumDeclarationSyntax => "enum",
            MethodDeclarationSyntax => "method",
            ConstructorDeclarationSyntax => "constructor",
            PropertyDeclarationSyntax => "property",
            FieldDeclarationSyntax => "field",
            EventFieldDeclarationSyntax => "event",
            EventDeclarationSyntax => "event",
            DelegateDeclarationSyntax => "delegate",
            _ => member.Kind().ToString()
        };
    }

    private static string SymbolName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            EventFieldDeclarationSyntax eventField => string.Join(", ", eventField.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            EventDeclarationSyntax evt => evt.Identifier.ValueText,
            DelegateDeclarationSyntax del => del.Identifier.ValueText,
            _ => member.Kind().ToString()
        };
    }

    private static string BuildSignature(MemberDeclarationSyntax member)
    {
        MemberDeclarationSyntax cleanMember = member.WithoutLeadingTrivia();
        return cleanMember switch
        {
            PropertyDeclarationSyntax property => property.WithAccessorList(null).WithExpressionBody(null).WithSemicolonToken(default).ToFullString().Trim(),
            MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).ToFullString().Trim(),
            ConstructorDeclarationSyntax constructor => constructor.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).ToFullString().Trim(),
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            FieldDeclarationSyntax field => field.WithDeclaration(field.Declaration.WithVariables(SyntaxFactory.SeparatedList(field.Declaration.Variables.Select(variable => variable.WithInitializer(null))))).ToFullString().Trim(),
            _ => cleanMember.ToFullString().Split(["\r\n", "\n"], StringSplitOptions.None)[0].Trim()
        };
    }

    private static IReadOnlyList<string> GetModifiers(MemberDeclarationSyntax member)
    {
        SyntaxTokenList modifiers = member switch
        {
            BaseTypeDeclarationSyntax type => type.Modifiers,
            BaseMethodDeclarationSyntax method => method.Modifiers,
            EventDeclarationSyntax evt => evt.Modifiers,
            EventFieldDeclarationSyntax eventField => eventField.Modifiers,
            BasePropertyDeclarationSyntax property => property.Modifiers,
            FieldDeclarationSyntax field => field.Modifiers,
            DelegateDeclarationSyntax del => del.Modifiers,
            _ => default
        };
        return modifiers.Select(modifier => modifier.ValueText).ToArray();
    }

    private static bool HasModifier(MemberDeclarationSyntax member, SyntaxKind kind)
    {
        SyntaxTokenList modifiers = member switch
        {
            BaseTypeDeclarationSyntax type => type.Modifiers,
            BaseMethodDeclarationSyntax method => method.Modifiers,
            EventDeclarationSyntax evt => evt.Modifiers,
            EventFieldDeclarationSyntax eventField => eventField.Modifiers,
            BasePropertyDeclarationSyntax property => property.Modifiers,
            FieldDeclarationSyntax field => field.Modifiers,
            DelegateDeclarationSyntax del => del.Modifiers,
            _ => default
        };
        return modifiers.Any(modifier => modifier.IsKind(kind));
    }

    private static IReadOnlyList<string> GetBaseTypes(MemberDeclarationSyntax member)
    {
        return member is BaseTypeDeclarationSyntax type && type.BaseList is not null
            ? type.BaseList.Types.Select(item => item.Type.ToString()).ToArray()
            : [];
    }

    private static bool HasDocumentation(MemberDeclarationSyntax member)
    {
        return member.GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .Any();
    }

    private static bool HasVisibleAttributes(MemberDeclarationSyntax member)
    {
        return member.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => !ShouldSkipSourceMapAttribute(attribute.Name.ToString()));
    }

    private static IReadOnlyList<RoslynSourceMapAttribute> GetAttributeSummaries(MemberDeclarationSyntax member)
    {
        return member.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => !ShouldSkipSourceMapAttribute(attribute.Name.ToString()))
            .Select(attribute => new RoslynSourceMapAttribute(
                attribute.Name.ToString(),
                attribute.ArgumentList?.Arguments.Select(argument => argument.ToString()).ToArray()))
            .ToArray();
    }

    private static bool ShouldSkipSourceMapAttribute(string attributeName)
    {
        string simpleName = attributeName.Split('.').Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            simpleName = simpleName[..^"Attribute".Length];
        }

        return simpleName is "AIChange" or "AIHistory" or "AIInstructions" or "UserHistory"
            || simpleName.StartsWith("AI", StringComparison.Ordinal)
                && simpleName is not ("AIFileContext");
    }

    private static string? GetElisionReason(string relativePath, MemberDeclarationSyntax member)
    {
        string normalizedPath = NormalizePath(relativePath);
        string name = SymbolName(member);
        if (normalizedPath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            if (member is MethodDeclarationSyntax method
                && name.Equals("InitializeComponent", StringComparison.Ordinal)
                && method.ParameterList.Parameters.Count == 0)
            {
                return "winforms-designer-initialize-component";
            }

            if (member is MethodDeclarationSyntax dispose
                && name.Equals("Dispose", StringComparison.Ordinal)
                && dispose.ParameterList.Parameters.Count == 1
                && dispose.ParameterList.Parameters[0].Type?.ToString() == "bool")
            {
                return "winforms-designer-dispose";
            }

            if (member is FieldDeclarationSyntax or EventFieldDeclarationSyntax)
            {
                return "winforms-designer-field";
            }
        }

        if (name.Equals("BuildRenderTree", StringComparison.Ordinal)
            && member is MethodDeclarationSyntax renderMethod
            && renderMethod.ParameterList.Parameters.Count == 1)
        {
            return "razor-generated-render-plumbing";
        }

        return null;
    }

    private static string? GetReturnType(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.ReturnType.ToString(),
            PropertyDeclarationSyntax property => property.Type.ToString(),
            FieldDeclarationSyntax field => field.Declaration.Type.ToString(),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Type.ToString(),
            EventDeclarationSyntax evt => evt.Type.ToString(),
            DelegateDeclarationSyntax del => del.ReturnType.ToString(),
            _ => null
        };
    }

    private static IReadOnlyList<string> GetParameterTypes(MemberDeclarationSyntax member)
    {
        return GetParameters(member).Select(parameter => parameter.Type?.ToString() ?? string.Empty).ToArray();
    }

    private static IReadOnlyList<string> GetParameterNames(MemberDeclarationSyntax member)
    {
        return GetParameters(member).Select(parameter => parameter.Identifier.ValueText).ToArray();
    }

    private static IEnumerable<ParameterSyntax> GetParameters(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters,
            ConstructorDeclarationSyntax constructor => constructor.ParameterList.Parameters,
            DelegateDeclarationSyntax del => del.ParameterList.Parameters,
            _ => []
        };
    }

    private static bool IsUnderBuildOrHiddenDirectory(string path)
    {
        string[] parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part.StartsWith(".", StringComparison.Ordinal)
            || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string DetectDominantNewLine(string text)
    {
        int crlf = 0;
        int lf = 0;
        int cr = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[index] == '\n')
            {
                lf++;
            }
        }

        if (crlf >= lf && crlf >= cr && crlf > 0)
        {
            return "\r\n";
        }

        if (lf >= cr && lf > 0)
        {
            return "\n";
        }

        return Environment.NewLine;
    }

    private static string NormalizeLineEndings(string content, string newLine)
    {
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return newLine.Equals("\n", StringComparison.Ordinal)
            ? normalized
            : normalized.Replace("\n", newLine, StringComparison.Ordinal);
    }

    private static string ComputeHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
