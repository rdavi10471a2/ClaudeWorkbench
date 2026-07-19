using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Workflow;

namespace AIMonitor.McpServer;

public sealed record AIMonitorMcpStatus(
    string RepositoryRoot,
    string RuntimeRoot,
    string WatchedSolutionPath,
    string WatchedProjectFolder,
    string DatabasePath,
    bool DatabaseExists,
    int ProjectCount,
    int DocumentCount,
    int SymbolCount,
    int ReferenceCount,
    int CallSiteCount,
    int RelationshipCount,
    int StaleFileCount,
    int DiagnosticCount);

public sealed record AIMonitorWorkflowStatus(
    string WatchedSolutionPath,
    string WatchedProjectFolder,
    string RuntimeRoot,
    string WorkingRoot);

public sealed record AIMonitorToolErrorResult(
    bool IsError,
    string Message,
    string Expected,
    string? Received);

public sealed record AIMonitorIndexedReferenceResult(
    string TargetStableKey,
    string FilePath,
    int Line,
    int Column,
    string ReferenceKind,
    string Snippet,
    string TargetName,
    string TargetKind,
    string CallerStableKey,
    string CallerName,
    string CallerKind);

public sealed record AIMonitorSolutionIndexTree(
    IReadOnlyList<IndexedProjectRow> Projects,
    IReadOnlyList<IndexedDocumentRow> Files,
    IReadOnlyList<AIMonitorNamespaceTree> Namespaces);

public sealed record AIMonitorNamespaceTree(
    string Namespace,
    IReadOnlyList<string> Files,
    int SymbolCount);

public sealed record AIMonitorStageCandidateResult(
    string StagedRecordId,
    string StagedHash,
    string Status,
    string Classification,
    string StagedRecordPath,
    StagedEditSummary StagedRecordSummary,
    StagedEditRecord? StagedRecord,
    string NextStep);

public sealed record AIMonitorSelfCheckResult(
    string RepositoryRoot,
    string RuntimeRoot,
    string WatchedSolutionPath,
    string WatchedProjectFolder,
    string WorkingRoot,
    string HistoryRoot,
    string StagedRoot,
    bool WatchedSolutionExists,
    bool WatchedProjectFolderExists,
    string SafetySummary,
    string OverallStatus,
    IReadOnlyList<AIMonitorGuardrailCheck> Guardrails);

public sealed record AIMonitorGuardrailCheck(
    string Name,
    string Status,
    string Message,
    string? Path);

public sealed record AIMonitorRefreshIndexFileResult(
    SolutionIndexSummary Summary,
    MonitorStatusResult Status,
    long ElapsedMilliseconds,
    IndexedFileDetailResult Detail,
    IReadOnlyList<IndexedDocumentRow> Files,
    IReadOnlyList<IndexedSymbolRow> Symbols);

public sealed record AIMonitorRefreshIndexResult(
    SolutionIndexSummary Summary,
    MonitorStatusResult Status,
    long ElapsedMilliseconds);

public sealed record AIMonitorRefreshFileAndIndexResult(
    EditSessionStatus Refresh,
    AIMonitorRefreshIndexFileResult Index);

public sealed record AIMonitorSessionState(
    string SessionId,
    string Purpose,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AIMonitorSessionEvent> Events)
{
    public IReadOnlyList<AIMonitorSessionFileAccess> Files { get; init; } = [];

    public AIMonitorSessionEditPlan? EditPlan { get; init; }
}

public sealed record AIMonitorSessionEditPlan(
    DateTimeOffset DeclaredAtUtc,
    IReadOnlyList<AIMonitorSessionPlannedFile> FilesPlanned);

public sealed record AIMonitorSessionPlannedFile(
    string SourceFilePath,
    string RelativePath,
    string OwningProjectPath,
    string FileName,
    string ProjectName,
    string Role,
    string Reason);

public sealed record AIMonitorSessionPlannedFileInput(
    string SourceFilePath,
    string? OwningProjectPath = null,
    string? Role = null,
    string? Reason = null);

public sealed record PlannedSessionDecisionOptions(
    bool DeferIndexRefresh,
    PostAcceptIndexRefreshPlan? RefreshPlan,
    IReadOnlyList<StagedEditRecord> TerminalValidationRecords);

public sealed record AIMonitorSessionSummary(
    string SessionId,
    string Purpose,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int EventCount);

public sealed record AIMonitorSessionEvent(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Summary,
    string? PayloadJson);

public sealed record AIMonitorSessionFileAccess(
    string SessionId,
    string SourceFilePath,
    string RelativePath,
    string AccessKind,
    AIMonitorFileHashInfo Hash,
    int FetchCount,
    DateTimeOffset FirstAccessedAtUtc,
    DateTimeOffset LastAccessedAtUtc);

public sealed record AIMonitorFileHashInfo(
    string Sha256,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record AIMonitorFileReadResult(
    string SourceFilePath,
    string RelativePath,
    AIMonitorFileHashInfo Hash,
    AIMonitorSessionFileAccess? SessionAccess,
    string Content);

public sealed record AIMonitorFileHashCheckResult(
    string SourceFilePath,
    bool KnownInSession,
    bool ChangedSinceFetch,
    AIMonitorFileHashInfo Current,
    AIMonitorFileHashInfo? Previous,
    AIMonitorSessionFileAccess? PreviousAccess);

public sealed record AIMonitorFileMatch(
    string Name,
    string Path,
    string RelativePath);

public sealed record AIMonitorCompatibilityResult(
    string Status,
    string Message,
    IReadOnlyDictionary<string, string?> Arguments);

public sealed record AIMonitorLedgerInfo(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record AIMonitorLedgerReadResult(
    string Path,
    bool Exists,
    string Content);

public sealed record AIMonitorWatchedProjectInfo(
    string Name,
    string Path,
    IReadOnlyList<string> SolutionFiles);

public sealed record AIMonitorServerShutdownResult(
    int ProcessId,
    DateTimeOffset RequestedAtUtc,
    string Reason);
