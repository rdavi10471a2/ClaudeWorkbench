using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Workflow;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    // ~4 chars per token. Past this a raw-index payload overflows the model's inline read and the
    // harness spills it to a file the agent cannot chunk back — so the tree/query tools return a
    // compact overflow envelope instead, mirroring get_source_map's graceful-truncation contract.
    private const int IndexToolCharBudget = 80_000;

    // Serialize once to measure; return the payload when it fits, otherwise the caller's envelope.
    private static object BudgetIndexPayload(object payload, Func<int, AIMonitorIndexOverflowEnvelope> overflow)
    {
        int approxChars = JsonSerializer.Serialize(payload, JsonOptions).Length;
        return approxChars <= IndexToolCharBudget ? payload : overflow(approxChars);
    }

    [McpServerTool]
    [Description("Rebuild the monitor-owned SQLite index for the watched solution.")]
    public async Task<AIMonitorRefreshIndexResult> RefreshSolutionIndex()
    {
        runtimeState.Touch();
        Stopwatch stopwatch = Stopwatch.StartNew();
        SolutionIndexSummary summary = await new SolutionIndexRebuildService().RebuildAsync(settings);
        stopwatch.Stop();
        return new AIMonitorRefreshIndexResult(summary, queryService.GetMonitorStatus(), stopwatch.ElapsedMilliseconds);
    }

    [McpServerTool]
    [Description("Refresh one watched C# file in the monitor-owned SQLite solution index. AIMonitor currently rebuilds the semantic index and returns the requested file slice.")]
    public async Task<AIMonitorRefreshIndexFileResult> RefreshSolutionIndexFile(
        [Description("Watched C# file path, absolute or relative to the watched solution folder.")] string path)
    {
        runtimeState.Touch();
        AIMonitorRefreshIndexResult refresh = await RefreshSolutionIndex();
        IndexedFileDetailResult detail = queryService.GetFileDetail(path);
        return new AIMonitorRefreshIndexFileResult(
            refresh.Summary,
            refresh.Status,
            refresh.ElapsedMilliseconds,
            detail,
            detail.Files,
            detail.Symbols);
    }

    [McpServerTool]
    [Description("Refresh a watched source file into the monitor-owned Working folder, then refresh the same file in the monitor-owned SQLite solution index.")]
    public async Task<AIMonitorRefreshFileAndIndexResult> RefreshFileAndIndex(
        [Description("Watched source file path, absolute or relative to the watched solution folder.")] string sourceFilePath)
    {
        runtimeState.Touch();
        EditSessionStatus refresh = workflowService.Refresh(ResolveWatchedPath(sourceFilePath));
        AIMonitorRefreshIndexFileResult index = await RefreshSolutionIndexFile(sourceFilePath);
        return new AIMonitorRefreshFileAndIndexResult(refresh, index);
    }

    [McpServerTool]
    [Description("Return status for the monitor-owned watched solution index, including database path and indexed counts.")]
    public MonitorStatusResult GetSolutionIndexStatus()
    {
        runtimeState.Touch();
        return queryService.GetMonitorStatus();
    }

    [McpServerTool]
    [Description("Return the monitor-owned watched solution index as compact JSON with indexed files and symbols. Use maxFiles/maxSymbols to budget the payload.")]
    public SolutionIndexQueryResult GetSolutionIndex(
        [Description("Maximum files to return.")] int maxFiles = 5000,
        [Description("Maximum symbols to return.")] int maxSymbols = 50000)
    {
        runtimeState.Touch();
        return queryService.QueryIndex(maxFiles: maxFiles, maxSymbols: maxSymbols);
    }

    [McpServerTool]
    [Description("Return the monitor-owned watched solution index tree as compact JSON: projects, namespaces, and files. Budgeted: when the tree is too large to read inline it returns a compact overflow envelope (counts + highest-symbol namespaces + ready-to-run narrower calls) instead of spilling to a file. For structural discovery prefer get_source_map (folder/navigation).")]
    public object GetSolutionIndexTree()
    {
        runtimeState.Touch();
        IReadOnlyList<IndexedProjectRow> projects = queryService.ListProjects();
        IReadOnlyList<IndexedDocumentRow> documents = queryService.ListDocuments();
        IReadOnlyList<AIMonitorNamespaceTree> namespaces = queryService.ListSymbols()
            .GroupBy(symbol => symbol.Namespace)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AIMonitorNamespaceTree(
                group.Key,
                group.Select(symbol => symbol.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                group.Count()))
            .ToArray();

        AIMonitorSolutionIndexTree tree = new(projects, documents, namespaces);
        return BudgetIndexPayload(tree, approxChars => new AIMonitorIndexOverflowEnvelope(
            true,
            $"The full index tree (~{approxChars} chars) exceeds the {IndexToolCharBudget}-char inline budget and would spill to a file you cannot read back. Narrow with the calls below, or use get_source_map for structural discovery.",
            approxChars,
            IndexToolCharBudget,
            projects.Count,
            documents.Count,
            namespaces.Sum(ns => ns.SymbolCount),
            namespaces
                .OrderByDescending(ns => ns.SymbolCount)
                .Take(10)
                .Select(ns => $"{ns.Namespace} — {ns.SymbolCount} symbol(s)")
                .ToArray(),
            [
                "get_source_map(scope: \"folder\", mode: \"navigation\", path: \"<folder>\")",
                "query_solution_index(scope: \"namespace\", value: \"<namespace from suggestedNarrowing>\")",
            ]));
    }

    [McpServerTool]
    [Description("Query the monitor-owned watched solution index by scope. Scopes: solution, namespace, folder, file. Budgeted: an over-large result is returned as a compact overflow envelope (counts + ready-to-run narrower calls) instead of spilling to a file that cannot be read back inline. For structural discovery prefer get_source_map (folder/navigation, then file/selector).")]
    public object QuerySolutionIndex(
        [Description("Index scope: solution, namespace, folder, or file.")] string scope = "solution",
        [Description("Namespace text, folder path, or file path for scoped queries. Omit for solution scope.")] string? value = null,
        [Description("Maximum files to return.")] int maxFiles = 200,
        [Description("Maximum symbols to return.")] int maxSymbols = 500)
    {
        runtimeState.Touch();
        SolutionIndexQueryResult result = queryService.QueryIndex(scope, value, maxFiles, maxSymbols);
        return BudgetIndexPayload(result, approxChars => new AIMonitorIndexOverflowEnvelope(
            true,
            $"This {scope} query (~{approxChars} chars) exceeds the {IndexToolCharBudget}-char inline budget and would spill to a file you cannot read back. Narrow the scope or lower maxSymbols, or use get_source_map.",
            approxChars,
            IndexToolCharBudget,
            0,
            result.Files.Count,
            result.Symbols.Count,
            [value is null ? $"scope={scope}" : $"scope={scope}, value={value}"],
            [
                "get_source_map(scope: \"folder\", mode: \"navigation\", path: \"<folder>\")",
                "get_source_map(scope: \"file\", mode: \"selector\", path: \"<file>\")",
                "query_solution_index(scope: \"file\", value: \"<file>\")",
            ]));
    }

    [McpServerTool]
    [Description("Find indexed C# symbols by name text, optional kind, optional exact namespace, and optional containing type using the monitor-owned watched solution index. Qualified Type.Member text is treated as a containing-type member lookup.")]
    public IndexedSymbolSearchResult FindIndexedSymbols(
        [Description("Symbol name text to search for. Use Type.Member to avoid homonym fanout for members.")] string text,
        [Description("Optional exact symbol kind, such as class, method, property, field, constructor, enum, delegate, interface, struct, or record.")] string? kind = null,
        [Description("Optional exact namespace filter.")] string? namespaceName = null,
        [Description("Optional containing type filter, such as OrderRepository or My.Namespace.OrderRepository.")] string? containingType = null,
        [Description("Maximum symbols to return.")] int maxResults = 100)
    {
        runtimeState.Touch();
        return queryService.FindSymbols(text, kind, namespaceName, containingType, maxResults);
    }

    [McpServerTool]
    [Description("Return one indexed C# symbol by stable symbol key from the monitor-owned watched solution index.")]
    public object? GetIndexedSymbol(
        [Description("Stable symbol key returned by query_solution_index or find_indexed_symbols.")] string stableSymbolKey)
    {
        runtimeState.Touch();
        if (TryCreateIndexedStableSymbolKeyError(stableSymbolKey) is { } error)
        {
            return error;
        }

        return queryService.FindSymbols(string.Empty, maxResults: 50000)
            .Symbols
            .FirstOrDefault(symbol => symbol.Symbol.StableKey.Equals(stableSymbolKey, StringComparison.Ordinal));
    }

    [McpServerTool]
    [Description("Return persisted indexed reference sites for one stable C# symbol key. Lean shape omits repeated project path and file hash fields; rich shape returns complete stored rows.")]
    public object FindIndexedReferences(
        [Description("Stable symbol key returned by query_solution_index, find_indexed_symbols, or get_indexed_symbol.")] string stableSymbolKey,
        [Description("Maximum reference rows to return.")] int maxResults = 500,
        [Description("Response shape: lean or rich. Lean is optimized for MCP token cost; rich preserves every persisted reference row field.")] string responseShape = "lean")
    {
        runtimeState.Touch();
        if (TryCreateIndexedStableSymbolKeyError(stableSymbolKey) is { } error)
        {
            return error;
        }

        IndexedReferenceRow[] references = queryService.ListReferences(stableSymbolKey).Take(maxResults).ToArray();
        if (responseShape.Equals("rich", StringComparison.OrdinalIgnoreCase))
        {
            return references;
        }

        return references.Select(ToMcpReferenceRow).ToArray();
    }

    // The inverse of find_indexed_references: that one is symbol-keyed ("where is THIS used"),
    // this one is file-keyed ("what does this file reference"). Answering it needed the retired
    // AIMonitor.Cli until now — the engine method existed with no tool in front of it, so the
    // index stored rows nothing on the live surface could read.
    [McpServerTool]
    [Description("Return the persisted indexed references that occur INSIDE one file — the inverse of find_indexed_references, which is symbol-keyed. Use this to see what a file depends on before editing it.")]
    public object FindReferencesInFile(
        [Description("File path, absolute or relative to the watched solution folder.")] string path,
        [Description("Maximum reference rows to return.")] int maxResults = 500,
        [Description("Response shape: lean or rich. Lean is optimized for MCP token cost; rich preserves every persisted reference row field.")] string responseShape = "lean")
    {
        runtimeState.Touch();
        IndexedReferenceRow[] references = queryService
            .ListReferencesInFile(ResolveWatchedPath(path))
            .Take(maxResults)
            .ToArray();

        if (responseShape.Equals("rich", StringComparison.OrdinalIgnoreCase))
        {
            return references;
        }

        return references.Select(ToMcpReferenceRow).ToArray();
    }

    [McpServerTool]
    [Description("Return the NuGet PackageReference rows captured for the watched solution at index time, with the project that declares each one.")]
    public object ListPackageReferences(
        [Description("Maximum package rows to return.")] int maxResults = 500)
    {
        runtimeState.Touch();
        return queryService.ListPackageReferences().Take(maxResults).ToArray();
    }

    [McpServerTool]
    [Description("Return persisted indexed invocation/object-creation call sites for one stable C# method or constructor symbol key, including caller identity.")]
    public object FindIndexedCallers(
        [Description("Stable method or constructor symbol key returned by query_solution_index, find_indexed_symbols, or get_indexed_symbol.")] string stableSymbolKey,
        [Description("Maximum caller rows to return.")] int maxResults = 500)
    {
        runtimeState.Touch();
        if (TryCreateIndexedStableSymbolKeyError(stableSymbolKey) is { } error)
        {
            return error;
        }

        return queryService.ListCallSites(stableKey: stableSymbolKey)
            .Take(maxResults)
            .ToArray();
    }

    [McpServerTool]
    [Description("Return persisted indexed symbol relationship rows for one stable symbol key, including incoming and outgoing relationship direction.")]
    public object FindIndexedRelationships(
        [Description("Stable symbol key returned by query_solution_index, find_indexed_symbols, or get_indexed_symbol.")] string stableSymbolKey,
        [Description("Optional exact relationship kind filter.")] string? relationshipKind = null,
        [Description("Relationship direction: outgoing, incoming, or both.")] string direction = "both",
        [Description("Maximum relationship rows to return.")] int maxResults = 500)
    {
        runtimeState.Touch();
        if (TryCreateIndexedStableSymbolKeyError(stableSymbolKey) is { } error)
        {
            return error;
        }

        return queryService.ListRelationships(stableSymbolKey, direction, relationshipKind)
            .Take(maxResults)
            .ToArray();
    }

}

// Returned by get_solution_index_tree / query_solution_index when the full payload would exceed the
// inline token budget: the counts to orient on, the highest-symbol namespaces to drill into, and
// exact narrower calls to run — the same "narrow, don't dump" contract get_source_map uses.
public sealed record AIMonitorIndexOverflowEnvelope(
    bool WasTruncated,
    string Reason,
    int ApproxChars,
    int CharBudget,
    int ProjectCount,
    int FileCount,
    int SymbolCount,
    IReadOnlyList<string> SuggestedNarrowing,
    IReadOnlyList<string> SuggestedNextCalls);
