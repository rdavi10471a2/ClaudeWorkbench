using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Runtime;
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
    [McpServerTool]
    [Description("Return paths and high-level status for the AIMonitor MCP server and watched solution.")]
    public AIMonitorMcpStatus GetMonitorStatus()
    {
        runtimeState.Touch();
        MonitorStatusResult indexStatus = queryService.GetMonitorStatus();
        return new AIMonitorMcpStatus(
            settings.RepositoryRoot,
            settings.RuntimeRoot,
            settings.WatchedSolutionPath,
            settings.WatchedProjectFolder,
            indexStatus.DatabasePath,
            indexStatus.DatabaseExists,
            indexStatus.ProjectCount,
            indexStatus.DocumentCount,
            indexStatus.SymbolCount,
            indexStatus.ReferenceCount,
            indexStatus.CallSiteCount,
            indexStatus.RelationshipCount,
            indexStatus.StaleFileCount,
            indexStatus.DiagnosticCount);
    }

    [McpServerTool]
    [Description("Return the monitor workflow status, including watched solution, runtime root, Working folder, and configured WinMerge candidates.")]
    public AIMonitorWorkflowStatus GetWorkflowStatus()
    {
        runtimeState.Touch();
        return new AIMonitorWorkflowStatus(
            settings.WatchedSolutionPath,
            settings.WatchedProjectFolder,
            settings.RuntimeRoot,
            workflowPaths.WorkingRoot,
            settings.WinMergeCandidatePaths.FirstOrDefault(File.Exists),
            settings.WinMergeCandidatePaths);
    }

    [McpServerTool]
    [Description("Return evaluated self-check guardrails for configured roots, working folders, diff tool availability, and watched-source safety boundaries.")]
    public AIMonitorSelfCheckResult GetSelfCheck()
    {
        runtimeState.Touch();
        AIMonitorGuardrailCheck[] guardrails = BuildSelfCheckGuardrails();
        string overallStatus = guardrails.Any(check => check.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)) ? "failed"
            : guardrails.Any(check => check.Status.Equals("warning", StringComparison.OrdinalIgnoreCase)) ? "warning"
            : guardrails.Any(check => check.Status.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) ? "unavailable"
            : "passed";
        return new AIMonitorSelfCheckResult(
            settings.RepositoryRoot,
            settings.RuntimeRoot,
            settings.WatchedSolutionPath,
            settings.WatchedProjectFolder,
            workflowPaths.WorkingRoot,
            workflowPaths.HistoryRoot,
            workflowPaths.StagedRoot,
            File.Exists(settings.WatchedSolutionPath),
            Directory.Exists(settings.WatchedProjectFolder),
            settings.WinMergeCandidatePaths.FirstOrDefault(File.Exists),
            "agents edit monitor-owned Working candidates only; the operator's in-app merge-review Accept is the sole watched-source mutation surface",
            overallStatus,
            guardrails);
    }

    [McpServerTool]
    [Description("List monitor run/history entries recorded under monitor-owned workflow history.")]
    public IReadOnlyList<Dictionary<string, object?>> ListMonitorRuns(
        [Description("Maximum entries to return.")] int maxEntries = 100)
    {
        runtimeState.Touch();
        string path = Path.Combine(workflowPaths.HistoryRoot, "_runs.json");
        if (!File.Exists(path))
        {
            return [];
        }

        IReadOnlyList<Dictionary<string, object?>> entries = JsonSerializer.Deserialize<IReadOnlyList<Dictionary<string, object?>>>(File.ReadAllText(path), JsonOptions) ?? [];
        return entries.TakeLast(maxEntries).ToArray();
    }

    [McpServerTool]
    [Description("Return recorded entries for one monitor run id.")]
    public IReadOnlyList<Dictionary<string, object?>> GetMonitorRun(
        [Description("Run id from list_monitor_runs.")] string runId)
    {
        runtimeState.Touch();
        return ListMonitorRuns(500)
            .Where(entry => entry.TryGetValue("runId", out object? value) && string.Equals(value?.ToString(), runId, StringComparison.Ordinal))
            .ToArray();
    }

    [McpServerTool]
    [Description("List monitor-owned per-file ledgers.")]
    public IReadOnlyList<AIMonitorLedgerInfo> ListLedgers(
        [Description("Maximum ledgers to return.")] int maxEntries = 100)
    {
        runtimeState.Touch();
        string root = Path.Combine(workflowPaths.HistoryRoot, "Ledgers");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.md")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(maxEntries)
                .Select(info => new AIMonitorLedgerInfo(info.FullName, info.Length, info.LastWriteTimeUtc))
                .ToArray()
            : [];
    }

    [McpServerTool]
    [Description("Read one monitor-owned per-file ledger by source file or ledger path.")]
    public AIMonitorLedgerReadResult GetLedger(
        [Description("Optional source file path, absolute or relative to the watched solution folder.")] string? sourceFilePath = null,
        [Description("Optional absolute ledger path under the ledger root.")] string? ledgerPath = null)
    {
        runtimeState.Touch();
        string root = Path.GetFullPath(Path.Combine(workflowPaths.HistoryRoot, "Ledgers"));
        string path = !string.IsNullOrWhiteSpace(ledgerPath)
            ? Path.GetFullPath(ledgerPath)
            : Path.Combine(root, $"{Sanitize(workflowPaths.GetRelativeWatchedPath(ResolveWatchedPath(sourceFilePath ?? throw new InvalidOperationException("sourceFilePath or ledgerPath is required."))).Replace(Path.DirectorySeparatorChar, '_'))}.md");
        string relativeLedgerPath = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relativeLedgerPath)
            || relativeLedgerPath.Equals("..", StringComparison.Ordinal)
            || relativeLedgerPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeLedgerPath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ledger path must be under monitor-owned ledger storage.");
        }

        return new AIMonitorLedgerReadResult(path, File.Exists(path), File.Exists(path) ? File.ReadAllText(path) : string.Empty);
    }

    [McpServerTool]
    [Description("Archive/prune monitor-owned history. AIMonitor keeps history by default; this compatibility tool reports the current retention posture without deleting files.")]
    public AIMonitorCompatibilityResult PruneMonitorHistory(
        [Description("Retention window in days.")] int retentionDays = 7)
    {
        runtimeState.Touch();
        return new AIMonitorCompatibilityResult(
            "not-pruned",
            "AIMonitor currently keeps workflow history until an explicit UI/operator cleanup flow is implemented.",
            new Dictionary<string, string?> { ["retentionDays"] = retentionDays.ToString() });
    }

    [McpServerTool]
    [Description("Return the Markdown tool manifest for the AIMonitor MCP Server tool surface.")]
    public string GetToolManifest()
    {
        runtimeState.Touch();
        return ComposeToolManifest();
    }

    [McpServerTool]
    [Description("Return the normal staging guide for AIMonitor watched-project edits.")]
    public string GetStagingGuide()
    {
        runtimeState.Touch();
        return ComposeStagingGuide();
    }

    [McpServerTool]
    [Description("Return the smoke-test coverage todo/catalog for AIMonitor.")]
    public string GetSmokeTestCatalog()
    {
        runtimeState.Touch();
        string path = Path.Combine(settings.RepositoryRoot, "docs", "findings", "SmokeCoverageTodo.md");
        return File.Exists(path)
            ? File.ReadAllText(path)
            : "AIMonitor smoke coverage catalog is missing.";
    }

    [McpServerTool]
    [Description("List watched project folders. AIMonitor currently has one configured watched project folder.")]
    public IReadOnlyList<AIMonitorWatchedProjectInfo> ListWatchedProjects()
    {
        runtimeState.Touch();
        return
        [
            new AIMonitorWatchedProjectInfo(
                Path.GetFileName(settings.WatchedProjectFolder),
                settings.WatchedProjectFolder,
                File.Exists(settings.WatchedSolutionPath) ? [settings.WatchedSolutionPath] : [])
        ];
    }

    [McpServerTool]
    [Description("Request graceful shutdown of this AIMonitor MCP server process.")]
    public AIMonitorServerShutdownResult ShutdownServer(
        [Description("Optional operator/client reason for the shutdown request.")] string? reason = null)
    {
        runtimeState.RequestShutdown(reason);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            applicationLifetime.StopApplication();
        });
        return new AIMonitorServerShutdownResult(Environment.ProcessId, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(reason) ? "shutdown_server requested" : reason);
    }

}
