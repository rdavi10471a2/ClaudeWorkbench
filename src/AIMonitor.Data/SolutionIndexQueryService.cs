using AIMonitor.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIMonitor.Data;

public sealed class SolutionIndexQueryService
{
    private const int MaxFileLimit = 5000;
    private const int MaxSymbolLimit = 50000;

    // Dot-prefixed metadata member names. A qualified "ContainingType + '.' + Name" query for one of these
    // produces a doubled-dot string ("Foo..ctor"), which the generic last-dot split would mis-parse.
    private static readonly string[] DotPrefixedMemberNames = { ".ctor", ".cctor" };

    private readonly MonitorSettings settings;
    private readonly SolutionIndexStore store;

    public SolutionIndexQueryService(
        MonitorSettings settings,
        SolutionIndexStore store,
        string databasePath)
    {
        this.settings = settings;
        this.store = store;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public static SolutionIndexQueryService Create(MonitorSettings settings)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        return new SolutionIndexQueryService(settings, store, databasePath);
    }

    public MonitorStatusResult GetMonitorStatus()
    {
        bool databaseExists = File.Exists(DatabasePath);
        SolutionIndexSummary summary = databaseExists
            ? store.GetSummary()
            : new SolutionIndexSummary(string.Empty, DateTimeOffset.MinValue, 0, 0, 0);
        IReadOnlyList<IndexedDocumentRow> documents = databaseExists ? store.ListDocuments() : [];
        IReadOnlyList<IndexedSymbolRow> symbols = databaseExists ? store.ListSymbols() : [];
        IReadOnlyList<IndexedReferenceRow> references = databaseExists ? store.ListReferences() : [];
        IReadOnlyList<IndexedCallSiteRow> callSites = databaseExists ? store.ListCallSites() : [];
        IReadOnlyList<IndexedRelationshipRow> relationships = databaseExists ? store.ListRelationships() : [];
        bool rebuildRequired = databaseExists && new SolutionIndexDatabase(DatabasePath).IsFullRebuildRequired();

        return new MonitorStatusResult
        {
            WatchedSolutionPath = settings.WatchedSolutionPath,
            RuntimeRoot = settings.RuntimeRoot,
            DatabasePath = DatabasePath,
            DatabaseExists = databaseExists,
            IndexedInputPath = summary.InputPath,
            IndexedAtUtc = summary.IndexedAtUtc,
            ProjectCount = summary.ProjectCount,
            DocumentCount = summary.DocumentCount,
            SymbolCount = symbols.Count,
            ReferenceCount = references.Count,
            CallSiteCount = callSites.Count,
            RelationshipCount = relationships.Count,
            StaleFileCount = documents.Count(IsStale),
            DiagnosticCount = summary.DiagnosticCount,
            RebuildRequired = rebuildRequired
        };
    }

    public SolutionIndexSummary GetSummary()
    {
        return store.GetSummary();
    }

    public SolutionIndexQueryResult QueryIndex(
        string scope = "solution",
        string? value = null,
        int maxFiles = 200,
        int maxSymbols = 500)
    {
        IReadOnlyList<IndexedDocumentRow> allDocuments = ListDocuments();
        IReadOnlyList<IndexedSymbolRow> allSymbols = ListSymbols();
        IEnumerable<IndexedDocumentRow> documents = allDocuments;
        IEnumerable<IndexedSymbolRow> symbols = allSymbols;
        string normalizedScope = NormalizeScope(scope);
        if (normalizedScope == "file")
        {
            string filePath = ResolveWatchedPath(RequireScopeValue(value, normalizedScope));
            documents = documents.Where(row => PathEquals(row.FilePath, filePath));
            symbols = symbols.Where(row => PathEquals(row.FilePath, filePath));
        }
        else if (normalizedScope == "folder")
        {
            string folderPath = ResolveWatchedPath(RequireScopeValue(value, normalizedScope));
            documents = documents.Where(row => PathIsUnderFolder(row.FilePath, folderPath));
            symbols = symbols.Where(row => PathIsUnderFolder(row.FilePath, folderPath));
        }
        else if (normalizedScope == "namespace")
        {
            string namespaceName = RequireScopeValue(value, normalizedScope);
            symbols = symbols.Where(row => row.Namespace.Equals(namespaceName, StringComparison.Ordinal));
            HashSet<string> files = symbols.Select(row => row.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            documents = documents.Where(row => files.Contains(row.FilePath));
        }
        else if (normalizedScope != "solution")
        {
            throw new ArgumentException("Scope must be solution, namespace, folder, or file.", nameof(scope));
        }

        IndexedDocumentRow[] scopedDocuments = documents.ToArray();
        IndexedSymbolRow[] scopedSymbols = symbols.ToArray();
        (int clampedFileLimit, bool filesClamped) = ClampLimit(maxFiles, MaxFileLimit);
        (int clampedSymbolLimit, bool symbolsClamped) = ClampLimit(maxSymbols, MaxSymbolLimit);

        return new SolutionIndexQueryResult(
            GetMonitorStatus(),
            scopedDocuments.Take(clampedFileLimit).ToArray(),
            scopedSymbols.Take(clampedSymbolLimit).ToArray(),
            normalizedScope,
            value,
            clampedFileLimit,
            clampedSymbolLimit,
            scopedDocuments.Length,
            scopedSymbols.Length,
            filesClamped || symbolsClamped);
    }

    public IReadOnlyList<IndexedProjectRow> ListProjects()
    {
        return store.ListProjects();
    }

    public IReadOnlyList<IndexedDocumentRow> ListDocuments(string? projectPath = null, string? filePath = null)
    {
        IEnumerable<IndexedDocumentRow> rows = store.ListDocuments();

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            rows = rows.Where(row => string.Equals(row.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string fullPath = ResolveWatchedPath(filePath);
            rows = rows.Where(row => PathEquals(row.FilePath, fullPath));
        }

        return rows.ToList();
    }

    public IReadOnlyList<IndexedSymbolRow> ListSymbols(string? filePath = null, string? name = null)
    {
        IEnumerable<IndexedSymbolRow> rows = store.ListSymbols();

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string fullPath = ResolveWatchedPath(filePath);
            rows = rows.Where(row => PathEquals(row.FilePath, fullPath));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            rows = rows.Where(row => string.Equals(row.Name, name, StringComparison.Ordinal));
        }

        return rows.ToList();
    }

    public IndexedSymbolSearchResult FindSymbols(
        string text,
        string? kind = null,
        string? namespaceName = null,
        string? containingType = null,
        int maxResults = 100)
    {
        string searchText = text ?? string.Empty;
        string? containingTypeFilter = containingType;
        bool exactName = false;
        if (string.IsNullOrWhiteSpace(containingTypeFilter)
            && TryParseQualifiedMemberSearch(searchText, out string parsedContainingType, out string parsedName))
        {
            containingTypeFilter = parsedContainingType;
            searchText = parsedName;
            exactName = true;
        }

        IEnumerable<IndexedSymbolRow> symbols = ListSymbols()
            .Where(symbol => exactName
                ? symbol.Name.Equals(searchText, StringComparison.OrdinalIgnoreCase)
                : symbol.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(kind))
        {
            symbols = symbols.Where(symbol => symbol.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            symbols = symbols.Where(symbol => symbol.Namespace.Equals(namespaceName, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(containingTypeFilter))
        {
            symbols = symbols.Where(symbol => MatchesContainingType(symbol, containingTypeFilter));
        }

        IndexedSymbolRow[] matchedSymbols = symbols.ToArray();
        (int clampedLimit, bool limitClamped) = ClampLimit(maxResults, MaxSymbolLimit);
        return new IndexedSymbolSearchResult(
            matchedSymbols.Take(clampedLimit).Select(ToQueryItem).ToArray(),
            matchedSymbols.Length,
            clampedLimit,
            limitClamped);
    }

    private static bool TryParseQualifiedMemberSearch(string text, out string containingType, out string name)
    {
        containingType = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();

        // ".ctor"/".cctor" are themselves dot-prefixed, so a qualified "ContainingType + '.' + Name" query
        // is "Foo..ctor". Split on the special-name boundary first so the member name keeps its leading dot;
        // the generic last-dot split below would otherwise yield containingType="Foo." and name="ctor".
        foreach (string special in DotPrefixedMemberNames)
        {
            string composed = "." + special;
            if (trimmed.Length > composed.Length
                && trimmed.EndsWith(composed, StringComparison.Ordinal))
            {
                containingType = trimmed[..^composed.Length];
                name = special;
                return containingType.Length > 0;
            }
        }

        int separatorIndex = trimmed.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= trimmed.Length - 1)
        {
            return false;
        }

        containingType = trimmed[..separatorIndex];
        name = trimmed[(separatorIndex + 1)..];
        return true;
    }

    private static bool MatchesContainingType(IndexedSymbolRow symbol, string containingType)
    {
        string filter = containingType.Trim();
        if (filter.Length == 0)
        {
            return true;
        }

        if (symbol.ContainingType.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || symbol.ContainingType.EndsWith("." + filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string qualifiedContainingType = string.IsNullOrWhiteSpace(symbol.Namespace)
            ? symbol.ContainingType
            : symbol.Namespace + "." + symbol.ContainingType;
        if (qualifiedContainingType.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || qualifiedContainingType.EndsWith("." + filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return symbol.Signature.StartsWith(filter + "." + symbol.Name, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IndexedReferenceRow> ListReferences(string? stableKey = null)
    {
        return store.ListReferences(stableKey);
    }

    public IReadOnlyList<IndexedCallSiteRow> ListCallSites(string? stableKey = null)
    {
        return store.ListCallSites(stableKey);
    }

    public IReadOnlyList<IndexedRelationshipRow> ListRelationships(
        string? stableKey = null,
        string direction = "both",
        string? relationshipKind = null)
    {
        return store.ListRelationships(stableKey, direction, relationshipKind);
    }

    public IReadOnlyList<IndexedReferenceRow> ListReferencesInFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        string fullPath = ResolveWatchedPath(filePath);
        return store.ListReferences()
            .Where(row => PathEquals(row.FilePath, fullPath))
            .ToList();
    }

    public IndexedFileDetailResult GetFileDetail(string path)
    {
        string fullPath = ResolveWatchedPath(path);
        IReadOnlyList<IndexedDocumentRow> files = ListDocuments(filePath: fullPath);
        IReadOnlyList<IndexedSymbolRow> symbols = ListSymbols(filePath: fullPath);
        IReadOnlyList<IndexedReferenceRow> references = ListReferencesInFile(fullPath);
        bool fileExists = File.Exists(fullPath);
        string currentHash = fileExists ? ComputeFileHash(fullPath) : string.Empty;
        long length = fileExists ? new FileInfo(fullPath).Length : 0;
        DateTime lastWriteTimeUtc = fileExists ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
        bool isIndexed = files.Count > 0;
        bool isStale = isIndexed && files.Any(file =>
            !string.IsNullOrWhiteSpace(file.ContentHash)
            && !file.ContentHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase));
        string parseStatus = !fileExists
            ? "missing"
            : isIndexed ? "indexed" : "not-indexed";

        return new IndexedFileDetailResult(
            path,
            fullPath,
            fileExists,
            length,
            lastWriteTimeUtc,
            currentHash,
            parseStatus,
            isIndexed,
            isStale,
            null,
            files,
            symbols,
            references);
    }

    public IReadOnlyList<IndexedPackageReferenceRow> ListPackageReferences()
    {
        return store.ListPackageReferences();
    }

    private static bool IsStale(IndexedDocumentRow document)
    {
        if (string.IsNullOrWhiteSpace(document.ContentHash))
        {
            return false;
        }

        if (!File.Exists(document.FilePath))
        {
            return true;
        }

        using FileStream stream = File.OpenRead(document.FilePath);
        string currentHash = ComputeHash(stream);
        return !currentHash.Equals(document.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFileHash(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return ComputeHash(stream);
    }

    private static string ComputeHash(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string ResolveWatchedPath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(settings.WatchedProjectFolder, path));
    }

    private static string NormalizeScope(string scope)
    {
        return string.IsNullOrWhiteSpace(scope)
            ? "solution"
            : scope.Trim().ToLowerInvariant();
    }

    private static string RequireScopeValue(string? value, string scope)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Scope '{scope}' requires a value.", nameof(value))
            : value.Trim();
    }

    private static bool PathEquals(string left, string right)
    {
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathIsUnderFolder(string filePath, string folderPath)
    {
        string fullFilePath = Path.GetFullPath(filePath);
        string fullFolderPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullFilePath.Equals(fullFolderPath, StringComparison.OrdinalIgnoreCase)
            || fullFilePath.StartsWith(fullFolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullFilePath.StartsWith(fullFolderPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Value, bool Clamped) ClampLimit(int requested, int maximum)
    {
        int clamped = Math.Clamp(requested, 0, maximum);
        return (clamped, clamped != requested);
    }

    private IndexedSymbolQueryItem ToQueryItem(IndexedSymbolRow symbol)
    {
        string relativePath = Path.GetRelativePath(settings.WatchedProjectFolder, symbol.FilePath);
        string selectorHintJson = JsonSerializer.Serialize(new
        {
            containingNamespace = symbol.Namespace,
            containingType = symbol.ContainingType,
            memberKind = symbol.Kind,
            name = symbol.Name
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new IndexedSymbolQueryItem(symbol, relativePath, selectorHintJson);
    }
}
