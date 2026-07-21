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
    [Description("Get or create THE edit session for this run and declare the watched files it will touch. There is exactly ONE edit session per run: if one is already live, this returns it and MERGES the newly-planned files into its plan (it never mints a second session). Declare every file the change will touch — including files you will move code out of — before editing; call this again with any file you discover later to add it to the same session.")]
    public AIMonitorSessionState StartMonitorSession(
        [Description("Planned watched files and their MSBuild owning projects. At least one file is required.")] IReadOnlyList<AIMonitorSessionPlannedFileInput> filesPlanned,
        [Description("Short purpose for this monitor session.")] string purpose = "monitor workflow")
    {
        runtimeState.Touch();
        IReadOnlyList<AIMonitorSessionPlannedFile> plannedFiles = BuildPlannedFiles(filesPlanned);

        // One edit session per run. If the workspace already has a live (unresolved)
        // session, reuse it and merge the newly-planned files into its plan rather than
        // creating a second session — two sessions fracture the operator's review into
        // two dialogs and two write-units, which is exactly the bug this guards against
        // (ADR-0005: the session is the atomic unit).
        string? activeId = workspace.ActiveEditSessionId;
        if (!string.IsNullOrWhiteSpace(activeId)
            && LoadSessionById(activeId) is AIMonitorSessionState live
            && !IsSessionResolved(activeId))
        {
            return MergePlannedFilesIntoSession(live, plannedFiles);
        }

        AIMonitorSessionState session = new(
            $"session-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..48],
            purpose,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [])
        {
            EditPlan = new AIMonitorSessionEditPlan(DateTimeOffset.UtcNow, plannedFiles)
        };
        SaveSession(session);
        workspace.ActiveEditSessionId = session.SessionId;
        RecordMonitorSessionEvent(
            session.SessionId,
            "start-monitor-session",
            $"{plannedFiles.Count} planned file(s)",
            JsonSerializer.Serialize(session.EditPlan, JsonOptions));
        return session;
    }

    // Fold newly-planned files into an already-live session's plan (union by source path
    // + owning project), so a second start_monitor_session in the same run extends the one
    // session instead of forking a new one.
    private AIMonitorSessionState MergePlannedFilesIntoSession(
        AIMonitorSessionState session,
        IReadOnlyList<AIMonitorSessionPlannedFile> newFiles)
    {
        IReadOnlyList<AIMonitorSessionPlannedFile> existing = session.EditPlan?.FilesPlanned ?? [];
        List<AIMonitorSessionPlannedFile> merged = existing.ToList();
        foreach (AIMonitorSessionPlannedFile file in newFiles)
        {
            bool already = merged.Any(present =>
                present.SourceFilePath.Equals(file.SourceFilePath, StringComparison.OrdinalIgnoreCase)
                && present.OwningProjectPath.Equals(file.OwningProjectPath, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                merged.Add(file);
            }
        }

        AIMonitorSessionState updated = session with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            EditPlan = new AIMonitorSessionEditPlan(DateTimeOffset.UtcNow, merged)
        };
        SaveSession(updated);
        RecordMonitorSessionEvent(
            session.SessionId,
            "start-monitor-session",
            $"reused live session; plan now {merged.Count} planned file(s)",
            JsonSerializer.Serialize(updated.EditPlan, JsonOptions));
        return updated;
    }

    // A session is resolved once every staged record it owns has left review — written to
    // watched source, or rejected/superseded. A session with NO records yet is NOT
    // resolved: it is freshly created and still the live one to reuse.
    private bool IsSessionResolved(string sessionId)
    {
        IReadOnlyList<StagedEditRecord> records = workflowService.ListStagedRecords(sessionId);
        if (records.Count == 0)
        {
            return false;
        }

        foreach (StagedEditRecord record in records)
        {
            bool superseded = !string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId);
            bool pending = string.IsNullOrWhiteSpace(record.Decision) && !superseded;
            bool approvedUnwritten = record.Decision.Equals("approved", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(record.WrittenAtUtc)
                && !superseded;
            if (pending || approvedUnwritten)
            {
                return false;
            }
        }

        return true;
    }

    [McpServerTool]
    [Description("Add ONE watched file to this run's edit-session plan, incrementally, without restating the whole list. Use this to declare files as you discover them — e.g. a file you must move code OUT of — and they join the single live session. Editing or staging a file that is not in the plan is refused, so add it here first. Defaults to this run's live session when sessionId is omitted.")]
    public AIMonitorSessionState AddMonitorSessionPlannedFile(
        [Description("The watched file to add, with its MSBuild owning project when the index cannot prove a single owner.")] AIMonitorSessionPlannedFileInput file,
        [Description("Session handle. Defaults to this run's live edit session when omitted.")] string? sessionId = null)
    {
        runtimeState.Touch();
        string targetId = !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId
            : workspace.ActiveEditSessionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException("No live edit session to add a file to. Call start_monitor_session first.");
        }

        AIMonitorSessionState session = GetMonitorSession(targetId);
        IReadOnlyList<AIMonitorSessionPlannedFile> planned = BuildPlannedFiles([file]);
        return MergePlannedFilesIntoSession(session, planned);
    }

    [McpServerTool]
    [Description("Mark this run's edit plan COMPLETE and compile the overlay ONCE over the whole planned set. This is the ONLY point the overlay gate runs while editing — submit_file and the edit tools never compile it — so call this AFTER you have submitted every planned file and BEFORE staging. Returns the combined overlay result; errors name the specific file/line. If any planned file has no submitted candidate yet, it compiles nothing and tells you which files are still pending, so it is safe to call and re-call.")]
    public PlanCompletionResult CompleteEditPlan(
        [Description("Session handle returned by start_monitor_session.")] string sessionId)
    {
        runtimeState.Touch();
        AIMonitorSessionEditPlan editPlan = RequireSessionEditPlan(sessionId);

        // Refuse (report, don't compile) until every planned file has a submitted candidate.
        // This is what makes the gate race-safe: it never compiles a half-queued overlay, even
        // if the agent batches this call alongside the submits.
        List<string> unsubmitted = editPlan.FilesPlanned
            .Where(file => workflowService.GetStatus(Path.GetFullPath(file.SourceFilePath)).OperationCount == 0)
            .Select(file => workflowPaths.GetRelativeWatchedPath(Path.GetFullPath(file.SourceFilePath)))
            .ToList();
        if (unsubmitted.Count > 0)
        {
            return new PlanCompletionResult(
                false,
                false,
                0,
                [],
                unsubmitted,
                $"Plan not complete: {unsubmitted.Count} planned file(s) have no submitted candidate yet. Submit them, then call complete_edit_plan again.");
        }

        // One compile covers the whole overlay (any file compiles the full candidate set) and the
        // validator caches it, so stamping every planned file's record is cheap and consistent.
        List<string> diagnostics = new();
        bool hasErrors = false;
        foreach (AIMonitorSessionPlannedFile file in editPlan.FilesPlanned)
        {
            EditOverlayValidationResult result = workflowService.ValidateOverlayForFile(Path.GetFullPath(file.SourceFilePath));
            if (result.HasErrors)
            {
                hasErrors = true;
                diagnostics.AddRange(result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Id} {diagnostic.Message}"));
            }
        }

        IReadOnlyList<string> distinctDiagnostics = diagnostics.Distinct(StringComparer.Ordinal).Take(50).ToList();
        RecordMonitorSessionEvent(
            sessionId,
            "complete-edit-plan",
            hasErrors ? $"overlay compiled with {distinctDiagnostics.Count} error(s)" : "overlay compiled clean",
            JsonSerializer.Serialize(distinctDiagnostics, JsonOptions));
        return new PlanCompletionResult(
            true,
            hasErrors,
            distinctDiagnostics.Count,
            distinctDiagnostics,
            [],
            hasErrors
                ? $"Plan complete, but the overlay compiled WITH ERRORS ({distinctDiagnostics.Count}). Fix and re-submit the affected files, then call complete_edit_plan again."
                : "Plan complete: the overlay compiled clean. Stage the planned files for review.");
    }

    [McpServerTool]
    [Description("Replace the watched files planned for this monitor edit session after explicit operator correction.")]
    public AIMonitorSessionState SetMonitorSessionEditPlan(
        [Description("Session handle returned by start_monitor_session.")] string sessionId,
        [Description("Planned watched files and their MSBuild owning projects.")] IReadOnlyList<AIMonitorSessionPlannedFileInput> filesPlanned)
    {
        runtimeState.Touch();
        AIMonitorSessionState session = GetMonitorSession(sessionId);
        IReadOnlyList<AIMonitorSessionPlannedFile> plannedFiles = BuildPlannedFiles(filesPlanned);

        AIMonitorSessionState updated = session with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            EditPlan = new AIMonitorSessionEditPlan(DateTimeOffset.UtcNow, plannedFiles)
        };
        SaveSession(updated);
        RecordMonitorSessionEvent(
            sessionId,
            "set-monitor-session-edit-plan",
            $"{plannedFiles.Count} planned file(s)",
            JsonSerializer.Serialize(updated.EditPlan, JsonOptions));
        return updated;
    }

    [McpServerTool]
    [Description("List durable monitor session handles known to this MCP server.")]
    public IReadOnlyList<AIMonitorSessionSummary> ListMonitorSessions()
    {
        runtimeState.Touch();
        return Directory.Exists(SessionRoot)
            ? Directory.EnumerateFiles(SessionRoot, "*.json")
                .Select(LoadSession)
                .Where(session => session is not null)
                .Select(session => new AIMonitorSessionSummary(session!.SessionId, session.Purpose, session.CreatedAtUtc, session.UpdatedAtUtc, session.Events.Count))
                .OrderByDescending(session => session.UpdatedAtUtc)
                .ToArray()
            : [];
    }

    [McpServerTool]
    [Description("Return a durable monitor session by explicit sessionId handle.")]
    public AIMonitorSessionState GetMonitorSession(
        [Description("Session handle returned by start_monitor_session.")] string sessionId)
    {
        runtimeState.Touch();
        return LoadSessionById(sessionId)
            ?? throw new InvalidOperationException($"Monitor session was not found: {sessionId}");
    }

    [McpServerTool]
    [Description("Append an event to a durable monitor session.")]
    public AIMonitorSessionState RecordMonitorSessionEvent(
        [Description("Session handle returned by start_monitor_session.")] string sessionId,
        [Description("Short event type, such as user-message, tool-call, tool-result, final-answer, or error.")] string eventType,
        [Description("Human-readable event summary.")] string summary,
        [Description("Optional JSON payload for the event.")] string? payloadJson = null)
    {
        runtimeState.Touch();
        AIMonitorSessionState session = LoadSessionById(sessionId)
            ?? throw new InvalidOperationException($"Monitor session was not found: {sessionId}");
        List<AIMonitorSessionEvent> events = session.Events.ToList();
        events.Add(new AIMonitorSessionEvent(DateTimeOffset.UtcNow, eventType, summary, payloadJson));
        AIMonitorSessionState updated = session with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Events = events
        };
        SaveSession(updated);
        return updated;
    }

    [McpServerTool]
    [Description("List staged edit records owned by a durable monitor session.")]
    public IReadOnlyList<StagedEditRecord> ListSessionStagedRecords(
        [Description("Session handle returned by start_monitor_session.")] string sessionId)
    {
        runtimeState.Touch();
        _ = GetMonitorSession(sessionId);
        return workflowService.ListStagedRecords(sessionId);
    }

}
