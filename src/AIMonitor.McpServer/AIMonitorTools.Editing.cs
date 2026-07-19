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
    [McpServerTool]
    [Description("Refresh a watched source file into the monitor-owned Working folder and clear candidate state for that file.")]
    public EditSessionStatus RefreshFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string sourceFilePath)
    {
        runtimeState.Touch();
        return workflowService.Refresh(ResolveWatchedPath(sourceFilePath));
    }

    [McpServerTool]
    [Description("Create a new-file edit session with an empty monitor-owned Working candidate. Watched source is not created.")]
    public EditSessionStatus NewFile(
        [Description("Future watched source path, absolute or relative to the watched solution folder.")] string sourceFilePath,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null)
    {
        runtimeState.Touch();
        EditSessionStatus status = workflowService.NewFile(ResolveWatchedPath(sourceFilePath));
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "new-file", status.WatchedFilePath, JsonSerializer.Serialize(status, JsonOptions));
        }

        return status;
    }

    [McpServerTool]
    [Description("Read a watched source file through the Monitor MCP server.")]
    public AIMonitorFileReadResult GetFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string sourceFilePath,
        [Description("Optional session handle. When supplied, records that the file was fetched.")] string? sessionId = null)
    {
        runtimeState.Touch();
        string path = ResolveWatchedPath(sourceFilePath);
        string text = File.ReadAllText(path);
        AIMonitorFileHashInfo hashInfo = GetFileHashInfo(path);
        AIMonitorSessionFileAccess? access = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            access = RecordSessionFileAccess(sessionId, path, "read", hashInfo);
            RecordMonitorSessionEvent(sessionId, "file-fetch", path, JsonSerializer.Serialize(hashInfo, JsonOptions));
        }

        return new AIMonitorFileReadResult(path, workflowPaths.GetRelativeWatchedPath(path), hashInfo, access, text);
    }

    [McpServerTool]
    [Description("Check whether a watched source file has changed since it was last fetched in a durable monitor session.")]
    public AIMonitorFileHashCheckResult CheckFileHash(
        [Description("Session handle returned by start_monitor_session.")] string sessionId,
        [Description("Source file path, absolute or relative to the watched solution folder.")] string sourceFilePath)
    {
        runtimeState.Touch();
        AIMonitorSessionState session = GetMonitorSession(sessionId);
        string path = ResolveWatchedPath(sourceFilePath);
        AIMonitorFileHashInfo current = GetFileHashInfo(path);
        AIMonitorSessionFileAccess? access = session.Files
            .Where(item => item.SourceFilePath.Equals(path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LastAccessedAtUtc)
            .FirstOrDefault();
        AIMonitorFileHashInfo? previous = access?.Hash;

        return new AIMonitorFileHashCheckResult(
            path,
            previous is not null,
            previous?.Sha256.Equals(current.Sha256, StringComparison.OrdinalIgnoreCase) == false,
            current,
            previous,
            access);
    }

    [McpServerTool]
    [Description("Find source or related files under the watched project folder by filename or wildcard pattern.")]
    public IReadOnlyList<AIMonitorFileMatch> FindFile(
        [Description("Filename or wildcard pattern, such as Program.cs or *.razor.")] string fileNameOrPattern,
        [Description("Maximum number of matches to return.")] int maxResults = 25)
    {
        runtimeState.Touch();
        string pattern = string.IsNullOrWhiteSpace(fileNameOrPattern) ? "*" : fileNameOrPattern;
        return Directory.Exists(settings.WatchedProjectFolder)
            ? Directory.EnumerateFiles(settings.WatchedProjectFolder, pattern, SearchOption.AllDirectories)
                .Where(path => !IsUnderBuildOrHiddenDirectory(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(path => new AIMonitorFileMatch(Path.GetFileName(path), path, workflowPaths.GetRelativeWatchedPath(path)))
                .ToArray()
            : [];
    }

    [McpServerTool]
    [Description("Return a Roslyn-derived outline for a watched C# source file, including kind, name, span, signature, namespace, and containing type.")]
    public RoslynFileOutlineResult GetFileOutline(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        return roslynEditService.GetFileOutline(fullPath);
    }

    [McpServerTool]
    [Description("Return a Roslyn-derived source map for a C# file, folder, namespace, or watched project. Use selector mode before C# symbol edits.")]
    public object GetSourceMap(
        [Description("Optional source file/folder path, or namespace text when scope is namespace.")] string? path = null,
        [Description("Source map scope: auto, file, folder, namespace, or project.")] string scope = "auto",
        [Description("Source map density: auto, navigation, selector, detail, or full.")] string mode = "auto",
        [Description("Optional namespace text when scope is namespace.")] string? namespaceName = null,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null)
    {
        runtimeState.Touch();
        RoslynSourceMapResult result;
        try
        {
            result = roslynEditService.GetSourceMap(path, scope, mode, namespaceName);
        }
        catch (InvalidOperationException ex) when (IsRecoverableRoslynGuidanceError(ex))
        {
            return new AIMonitorToolErrorResult(true, ex.Message, "Use .cs/.razor.cs for Roslyn symbol tools or text/file workflow tools for markup.", path);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "get-source-map", path ?? settings.WatchedProjectFolder, JsonSerializer.Serialize(result, JsonOptions));
        }

        return result;
    }

    [McpServerTool]
    [Description("Read one C# symbol body from the monitor-owned Working candidate using a Roslyn selector.")]
    public object GetSymbol(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("Compatibility shortcut symbol name.")] string? symbolName = null,
        [Description("Structured selector JSON from get_source_map when available.")] string? symbolSelectorJson = null,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null)
    {
        runtimeState.Touch();
        string selector = !string.IsNullOrWhiteSpace(symbolSelectorJson)
            ? symbolSelectorJson
            : JsonSerializer.Serialize(new RoslynSymbolSelector(Name: symbolName), JsonOptions);
        RoslynSymbolReadResult result;
        try
        {
            result = roslynEditService.GetSymbol(ResolveWatchedPath(path), selector);
        }
        catch (InvalidOperationException ex) when (IsRecoverableRoslynGuidanceError(ex))
        {
            return new AIMonitorToolErrorResult(true, ex.Message, "Use .cs/.razor.cs for Roslyn symbol tools or text/file workflow tools for markup.", path);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "get-symbol", result.WatchedFilePath, JsonSerializer.Serialize(result, JsonOptions));
        }

        return result;
    }

    [McpServerTool]
    [Description("Return edit workflow status for one watched source file.")]
    public EditSessionStatus GetEditStatus(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string sourceFilePath,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null)
    {
        runtimeState.Touch();
        EditSessionStatus status = workflowService.GetStatus(ResolveWatchedPath(sourceFilePath));
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "get-edit-status", status.WatchedFilePath, JsonSerializer.Serialize(status, JsonOptions));
        }

        return status;
    }

    [McpServerTool]
    [Description("Write a full-file candidate into the monitor-owned Working mirror. Does not create a staged record.")]
    public EditSessionStatus SubmitFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("Complete replacement file content.")] string content,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null,
        [Description("Optional JSON manifest expressing model intent.")] string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        EditSessionStatus status = workflowService.SubmitFile(fullPath, content, manifestJson, !deferOverlayValidation);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "submit-file", fullPath, manifestJson);
        }

        return status;
    }

    [McpServerTool]
    [Description("Replace exact oldText in the monitor-owned Working mirror candidate.")]
    public ReplaceTextResult ReplaceTextInFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("Exact old text to replace using ordinal matching.")] string oldText,
        [Description("Replacement text.")] string newText,
        [Description("Required number of matches in the current edit base. Leave -1 for unique replacement when occurrenceIndex is unset, or no total-match assertion when occurrenceIndex is set.")] int expectedMatches = -1,
        [Description("Optional 0-based occurrence index. Leave -1 for unique/global replacement; set 0 or greater to replace one occurrence without requiring unique oldText.")] int occurrenceIndex = -1,
        [Description("Optional SHA-256 hash of the current Working candidate.")] string? expectedFileHash = null,
        [Description("Optional SHA-256 hash of oldText.")] string? expectedOldTextHash = null,
        [Description("Optional durable session handle.")] string? sessionId = null,
        [Description("Optional JSON manifest expressing model intent.")] string? manifestJson = null)
    {
        runtimeState.Touch();
        if (!string.IsNullOrWhiteSpace(expectedOldTextHash)
            && !ComputeHash(oldText).Equals(expectedOldTextHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("oldText hash did not match expectedOldTextHash.");
        }

        int? expectedMatchCount = expectedMatches >= 0
            ? expectedMatches
            : occurrenceIndex >= 0 ? null : 1;

        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        ReplaceTextResult result = workflowService.ReplaceText(
            fullPath,
            oldText,
            newText,
            expectedMatchCount,
            expectedFileHash,
            occurrenceIndex >= 0 ? occurrenceIndex : null,
            manifestJson,
            !deferOverlayValidation);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "replace-text-in-file", result.WatchedFilePath, JsonSerializer.Serialize(result, JsonOptions));
        }

        return result;
    }

    [McpServerTool]
    [Description("Find exact text in the current Working candidate and return 1-based line/column bounds.")]
    public TextSpanResult FindTextSpan(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("Exact text to find using ordinal matching.")] string findText,
        [Description("0-based occurrence index when text appears multiple times.")] int occurrenceIndex = 0,
        [Description("Optional SHA-256 hash of the current Working candidate.")] string? expectedFileHash = null,
        [Description("Optional durable session handle.")] string? sessionId = null)
    {
        runtimeState.Touch();
        _ = sessionId;
        string fullPath = ResolveWatchedPath(path);
        EnsureSession(fullPath);
        return workflowService.FindTextSpan(fullPath, findText, occurrenceIndex, expectedFileHash);
    }

    [McpServerTool]
    [Description("Replace an exact 1-based line/column span in the monitor-owned Working mirror candidate.")]
    public EditSessionStatus ReplaceSpanInFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("1-based start line.")] int startLine,
        [Description("1-based start column.")] int startColumn,
        [Description("1-based exclusive end line.")] int endLine,
        [Description("1-based exclusive end column.")] int endColumn,
        [Description("Replacement text.")] string newText,
        [Description("Optional SHA-256 hash of the current Working candidate.")] string? expectedFileHash = null,
        [Description("Optional SHA-256 hash of the extracted old span text.")] string? expectedOldTextHash = null,
        [Description("Optional exact old span text.")] string? expectedOldText = null,
        [Description("Optional durable session handle.")] string? sessionId = null,
        [Description("Optional JSON manifest expressing model intent.")] string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        EnsureSession(fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        EditSessionStatus status = workflowService.ReplaceSpan(
            fullPath,
            startLine,
            startColumn,
            endLine,
            endColumn,
            newText,
            expectedFileHash,
            expectedOldTextHash,
            expectedOldText,
            manifestJson,
            !deferOverlayValidation);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "replace-span-in-file", status.WatchedFilePath, null);
        }

        return status;
    }

}
