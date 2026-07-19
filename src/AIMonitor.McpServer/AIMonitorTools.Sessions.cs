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
    [Description("Create a durable monitor session handle and declare the watched files planned for this edit session.")]
    public AIMonitorSessionState StartMonitorSession(
        [Description("Planned watched files and their MSBuild owning projects. At least one file is required.")] IReadOnlyList<AIMonitorSessionPlannedFileInput> filesPlanned,
        [Description("Short purpose for this monitor session.")] string purpose = "monitor workflow")
    {
        runtimeState.Touch();
        IReadOnlyList<AIMonitorSessionPlannedFile> plannedFiles = BuildPlannedFiles(filesPlanned);
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
        RecordMonitorSessionEvent(
            session.SessionId,
            "start-monitor-session",
            $"{plannedFiles.Count} planned file(s)",
            JsonSerializer.Serialize(session.EditPlan, JsonOptions));
        return session;
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
