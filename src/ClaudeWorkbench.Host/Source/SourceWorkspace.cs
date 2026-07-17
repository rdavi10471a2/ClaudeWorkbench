using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;

namespace ClaudeWorkbench.Host.Source;

// Builds the source-browser snapshot from the AIMonitor engine index (in-process)
// and rebuilds that index. Ported from CodexAppServerDemo's SourceWorkspaceService,
// simplified to source-only (no test projection) and retargeted at our engine.
public sealed class SourceWorkspace
{
    private const long MaxReadableFileBytes = 768 * 1024;

    private readonly MonitorSettings settings;
    private readonly SolutionIndexQueryService query;

    public SourceWorkspace(MonitorSettings settings, SolutionIndexQueryService query)
    {
        this.settings = settings;
        this.query = query;
    }

    // --- retained state (singleton) so the Source view survives tab switches,
    //     component re-creation, and browser refresh within a host session ----
    private SourceWorkspaceSnapshot current = SourceWorkspaceSnapshot.Empty("Loading source index...");
    private string filter = string.Empty;
    private string? selectedPath;
    private int? selectedLine;
    private bool rebuilding;
    private bool loaded;

    public event Action? Changed;

    public SourceWorkspaceSnapshot Snapshot => current;

    public string Filter => filter;

    public string? SelectedPath => selectedPath;

    public bool Rebuilding => rebuilding;

    public void EnsureLoaded()
    {
        if (!loaded)
        {
            loaded = true;
            Refresh();
        }
    }

    public void SetFilter(string value)
    {
        filter = value ?? string.Empty;
    }

    public void ApplyFilter()
    {
        Refresh();
    }

    public void Select(SourceSelection selection)
    {
        selectedPath = selection.RelativePath;
        selectedLine = selection.Line;
        Refresh();
    }

    public async Task RebuildAsync()
    {
        rebuilding = true;
        Changed?.Invoke();
        try
        {
            await new SolutionIndexRebuildService().RebuildAsync(settings);
        }
        finally
        {
            rebuilding = false;
            selectedPath = null;
            selectedLine = null;
            loaded = true;
            Refresh();
        }
    }

    private void Refresh()
    {
        current = BuildSnapshot(selectedPath, selectedLine, filter);
        Changed?.Invoke();
    }

    public SourceWorkspaceSnapshot BuildSnapshot(string? selectedRelativePath, int? selectedLine, string? filter)
    {
        MonitorStatusResult status = query.GetMonitorStatus();
        if (!status.DatabaseExists)
        {
            return WithMessage(status, filter, "Solution index is missing. Rebuild the index to load source.");
        }

        IReadOnlyList<IndexedProjectRow> projects = query.ListProjects();
        IReadOnlyList<IndexedDocumentRow> documents = query.ListDocuments();
        if (projects.Count == 0 || documents.Count == 0)
        {
            return WithMessage(status, filter, "Solution index is empty or stale. Rebuild the index to load source.");
        }

        IReadOnlyList<IndexedSymbolRow> symbols = query.ListSymbols();
        string watchedRoot = settings.WatchedProjectFolder;
        IReadOnlyList<SourceFileEntry> files = BuildFiles(watchedRoot, documents, filter);
        IReadOnlyList<SourceTreeNode> tree = BuildTree(projects, documents, symbols, watchedRoot, files);
        SourceFileEntry? selectedEntry = SelectFile(files, selectedRelativePath);
        SourceFileDocument? selectedFile = selectedEntry is null ? null : LoadDocument(selectedEntry, symbols, selectedLine);

        return new SourceWorkspaceSnapshot(
            watchedRoot,
            settings.WatchedSolutionPath,
            status.DatabasePath,
            files,
            tree,
            selectedFile,
            filter ?? string.Empty,
            files.Count == 0 ? "No indexed source files matched the current filter." : string.Empty);
    }

    public async Task RebuildIndexAsync()
    {
        await new SolutionIndexRebuildService().RebuildAsync(settings);
    }

    private SourceWorkspaceSnapshot WithMessage(MonitorStatusResult status, string? filter, string message)
    {
        return new SourceWorkspaceSnapshot(
            settings.WatchedProjectFolder,
            settings.WatchedSolutionPath,
            status.DatabasePath,
            [],
            [],
            null,
            filter ?? string.Empty,
            message);
    }

    private static IReadOnlyList<SourceFileEntry> BuildFiles(
        string watchedRoot,
        IReadOnlyList<IndexedDocumentRow> documents,
        string? filter)
    {
        return documents
            .Select(document => document.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SourceFileEntry(
                NormalizePath(Path.GetRelativePath(watchedRoot, path)),
                Path.GetFullPath(path),
                GetLanguage(Path.GetExtension(path)),
                GetFileSize(path),
                File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue))
            .Where(file => string.IsNullOrWhiteSpace(filter)
                || file.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(file => GetFileRank(file.RelativePath))
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SourceTreeNode> BuildTree(
        IReadOnlyList<IndexedProjectRow> projects,
        IReadOnlyList<IndexedDocumentRow> documents,
        IReadOnlyList<IndexedSymbolRow> symbols,
        string watchedRoot,
        IReadOnlyList<SourceFileEntry> visibleFiles)
    {
        Dictionary<string, SourceFileEntry> visibleByFullPath = visibleFiles
            .ToDictionary(file => Path.GetFullPath(file.FullPath), StringComparer.OrdinalIgnoreCase);
        List<SourceTreeNode> projectNodes = [];

        foreach (IndexedProjectRow project in projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            MutableSourceTreeNode projectNode = new(project.Name, "project", null);
            string projectDirectory = Path.GetDirectoryName(project.ProjectPath) ?? watchedRoot;
            foreach (IndexedDocumentRow document in documents
                .Where(document => PathEquals(document.ProjectPath, project.ProjectPath))
                .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                string fullDocumentPath = Path.GetFullPath(document.FilePath);
                if (!visibleByFullPath.TryGetValue(fullDocumentPath, out SourceFileEntry? fileEntry))
                {
                    continue;
                }

                string relativeToProject = NormalizePath(Path.GetRelativePath(projectDirectory, fullDocumentPath));
                string[] segments = relativeToProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0 || segments.Any(IsHiddenBuildFolder))
                {
                    continue;
                }

                MutableSourceTreeNode current = projectNode;
                for (int index = 0; index < segments.Length; index++)
                {
                    bool isFile = index == segments.Length - 1;
                    current = current.GetOrAdd(
                        segments[index],
                        isFile ? "file" : "folder",
                        isFile ? fileEntry : null);
                }

                AddSymbolNodes(current, document, symbols, fileEntry);
            }

            projectNodes.Add(ConvertNode(projectNode));
        }

        return projectNodes;
    }

    private static SourceTreeNode ConvertNode(MutableSourceTreeNode node)
    {
        IReadOnlyList<SourceTreeNode> children = node.Children
            .OrderBy(child => GetTreeNodeRank(child.Kind))
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ConvertNode)
            .ToArray();

        return new SourceTreeNode(node.Name, node.Kind, node.File, node.TargetFile, node.Line, children);
    }

    private static void AddSymbolNodes(
        MutableSourceTreeNode documentNode,
        IndexedDocumentRow document,
        IReadOnlyList<IndexedSymbolRow> symbols,
        SourceFileEntry fileEntry)
    {
        Dictionary<string, MutableSourceTreeNode> typeNodes = new(StringComparer.Ordinal);
        foreach (IndexedSymbolRow symbol in symbols
            .Where(symbol => PathEquals(symbol.FilePath, document.FilePath))
            .OrderBy(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            if (IsTypeLikeSymbol(symbol))
            {
                MutableSourceTreeNode typeNode = documentNode.GetOrAdd(
                    FormatSymbolNode(symbol),
                    "type",
                    null,
                    fileEntry,
                    Math.Max(symbol.StartLine, 1));
                typeNodes[symbol.Signature] = typeNode;
                continue;
            }

            MutableSourceTreeNode parent = documentNode;
            if (!string.IsNullOrWhiteSpace(symbol.ContainingType)
                && typeNodes.TryGetValue(symbol.ContainingType, out MutableSourceTreeNode? typeParent))
            {
                parent = typeParent;
            }

            MutableSourceTreeNode group = parent.GetOrAdd(
                FormatSymbolGroupName(symbol),
                "group",
                null,
                fileEntry,
                Math.Max(symbol.StartLine, 1));
            group.GetOrAdd(
                FormatSymbolNode(symbol),
                GetSymbolNodeKind(symbol),
                null,
                fileEntry,
                Math.Max(symbol.StartLine, 1));
        }
    }

    private static SourceFileEntry? SelectFile(IReadOnlyList<SourceFileEntry> files, string? selectedRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(selectedRelativePath))
        {
            SourceFileEntry? selected = files.FirstOrDefault(file =>
                file.RelativePath.Equals(NormalizePath(selectedRelativePath), StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return files.FirstOrDefault();
    }

    private static SourceFileDocument LoadDocument(SourceFileEntry entry, IReadOnlyList<IndexedSymbolRow> symbols, int? selectedLine)
    {
        string fullPath = Path.GetFullPath(entry.FullPath);
        FileInfo info = new(fullPath);
        int safeSelectedLine = Math.Max(selectedLine ?? 1, 1);
        if (!info.Exists)
        {
            return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension),
                "Indexed file no longer exists on disk.", safeSelectedLine, [], 0);
        }

        if (info.Length > MaxReadableFileBytes)
        {
            return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension),
                $"File is too large to preview ({info.Length:n0} bytes).", safeSelectedLine,
                BuildOutlineFromIndex(symbols, fullPath), info.Length);
        }

        string text = File.ReadAllText(fullPath);
        int lineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
        return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension), text,
            Math.Min(safeSelectedLine, lineCount), BuildOutlineFromIndex(symbols, fullPath), info.Length);
    }

    private static IReadOnlyList<SourceSymbolEntry> BuildOutlineFromIndex(IReadOnlyList<IndexedSymbolRow> symbols, string fullPath)
    {
        return symbols
            .Where(symbol => PathEquals(symbol.FilePath, fullPath))
            .OrderBy(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Take(160)
            .Select(symbol => new SourceSymbolEntry(
                string.IsNullOrWhiteSpace(symbol.Signature) ? symbol.Name : SimplifySignature(symbol.Signature),
                symbol.Kind,
                Math.Max(symbol.StartLine, 1)))
            .ToArray();
    }

    private static string SimplifySignature(string signature)
    {
        return signature
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.Tasks.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.", string.Empty, StringComparison.Ordinal)
            .Replace("System.", string.Empty, StringComparison.Ordinal);
    }

    private static long GetFileSize(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static string GetLanguage(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".razor" => "Razor",
            ".cshtml" => "Razor",
            ".csproj" => "MSBuild",
            ".sln" => "Solution",
            ".slnx" => "Solution",
            ".json" => "JSON",
            ".md" => "Markdown",
            ".xml" => "XML",
            ".xaml" => "XAML",
            ".css" => "CSS",
            ".html" => "HTML",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            _ => "Text",
        };
    }

    private static int GetFileRank(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static int GetTreeNodeRank(string kind)
    {
        return kind switch
        {
            "project" => 0,
            "folder" => 1,
            "file" => 2,
            "type" => 3,
            "group" => 4,
            "constructor" => 5,
            "method" => 5,
            "property" => 5,
            "field" => 5,
            "symbol" => 5,
            _ => 10,
        };
    }

    private static string GetSymbolNodeKind(IndexedSymbolRow symbol)
    {
        if (IsConstructorSymbol(symbol))
        {
            return "constructor";
        }

        if (symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase))
        {
            return "method";
        }

        if (symbol.Kind.Equals("Property", StringComparison.OrdinalIgnoreCase))
        {
            return "property";
        }

        if (symbol.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase))
        {
            return "field";
        }

        return "symbol";
    }

    private static string FormatSymbolNode(IndexedSymbolRow symbol)
    {
        return $"{FormatLocalSignature(symbol)} [{symbol.StartLine}-{symbol.EndLine}]";
    }

    private static string FormatLocalSignature(IndexedSymbolRow symbol)
    {
        if (IsTypeLikeSymbol(symbol))
        {
            return symbol.Name;
        }

        string signature = string.IsNullOrWhiteSpace(symbol.Signature) ? symbol.Name : symbol.Signature;
        return SimplifySignatureTypes(RemoveContainingTypePrefix(signature, symbol));
    }

    private static string RemoveContainingTypePrefix(string signature, IndexedSymbolRow symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.ContainingType))
        {
            string containingPrefix = symbol.ContainingType + ".";
            if (signature.StartsWith(containingPrefix, StringComparison.Ordinal))
            {
                return signature[containingPrefix.Length..];
            }
        }

        if (!string.IsNullOrWhiteSpace(symbol.Namespace))
        {
            string namespacePrefix = symbol.Namespace + ".";
            if (signature.StartsWith(namespacePrefix, StringComparison.Ordinal))
            {
                return signature[namespacePrefix.Length..];
            }
        }

        return signature;
    }

    private static string SimplifySignatureTypes(string signature)
    {
        int parameterStart = signature.IndexOf('(', StringComparison.Ordinal);
        int parameterEnd = signature.LastIndexOf(')');
        if (parameterStart < 0 || parameterEnd <= parameterStart)
        {
            return GetUnqualifiedTypeName(signature);
        }

        string name = signature[..parameterStart];
        string parameters = signature[(parameterStart + 1)..parameterEnd];
        string suffix = signature[(parameterEnd + 1)..];
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return $"{GetUnqualifiedTypeName(name)}(){suffix}";
        }

        string[] parts = parameters.Split(',');
        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = SimplifyParameter(parts[index].Trim());
        }

        return $"{GetUnqualifiedTypeName(name)}({string.Join(", ", parts)}){suffix}";
    }

    private static string SimplifyParameter(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return parameter;
        }

        string[] tokens = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < tokens.Length; index++)
        {
            tokens[index] = SimplifyTypeExpression(tokens[index]);
        }

        return string.Join(" ", tokens);
    }

    private static string SimplifyTypeExpression(string text)
    {
        return text
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.Tasks.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.", string.Empty, StringComparison.Ordinal)
            .Replace("System.", string.Empty, StringComparison.Ordinal);
    }

    private static string GetUnqualifiedTypeName(string name)
    {
        int genericIndex = name.IndexOf('<', StringComparison.Ordinal);
        string rootName = genericIndex >= 0 ? name[..genericIndex] : name;
        int lastDot = rootName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < rootName.Length - 1)
        {
            rootName = rootName[(lastDot + 1)..];
        }

        return genericIndex >= 0 ? rootName + name[genericIndex..] : rootName;
    }

    private static string FormatSymbolGroupName(IndexedSymbolRow symbol)
    {
        string access = string.IsNullOrWhiteSpace(symbol.Accessibility) ? "access unknown" : symbol.Accessibility.ToLowerInvariant();
        if (IsConstructorSymbol(symbol))
        {
            return $"{access} constructors";
        }

        if (symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase))
        {
            return $"{access} methods";
        }

        return $"{access} members";
    }

    private static bool IsTypeLikeSymbol(IndexedSymbolRow symbol)
    {
        return symbol.Kind.Equals("NamedType", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Class", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Struct", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Enum", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Record", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConstructorSymbol(IndexedSymbolRow symbol)
    {
        return symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
            && (symbol.MethodKind.Equals("Constructor", StringComparison.OrdinalIgnoreCase)
                || symbol.MethodKind.Equals("StaticConstructor", StringComparison.OrdinalIgnoreCase)
                || symbol.Name.Equals(".ctor", StringComparison.Ordinal)
                || symbol.Name.Equals(".cctor", StringComparison.Ordinal));
    }

    private static bool IsHiddenBuildFolder(string segment)
    {
        return segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("CSSourceBackups", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
