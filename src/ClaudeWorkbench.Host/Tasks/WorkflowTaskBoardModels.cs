namespace ClaudeWorkbench.Host.Tasks;

public sealed record WorkflowTaskStateRow(
    string Code,
    string Name,
    int SortOrder,
    bool IsTerminal);

public sealed record WorkflowTaskEventTypeRow(
    string Code,
    string Name,
    int SortOrder);

public sealed record WorkflowTaskRow(
    string Id,
    int TaskNumber,
    string Name,
    string Description,
    string ShortName,
    string Slug,
    string StateCode,
    string StateName,
    string? NotesMarkdownPath,
    string? AgentNotesMarkdownPath,
    bool IsArchived,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ActivatedAt,
    DateTime? CompletedAt,
    DateTime? ArchivedAt);

public sealed record WorkflowTaskFileRow(
    string Id,
    string TaskId,
    string RelativePath,
    string? Intent,
    string? FileRole);

public sealed record WorkflowTaskEventRow(
    string Id,
    string TaskId,
    string EventTypeCode,
    string EventTypeName,
    string? Message,
    string? PayloadJson,
    DateTime CreatedAt);

public sealed record WorkflowTaskBoardSnapshot(
    IReadOnlyList<WorkflowTaskStateRow> States,
    IReadOnlyList<WorkflowTaskEventTypeRow> EventTypes,
    IReadOnlyList<WorkflowTaskRow> Tasks,
    IReadOnlyList<WorkflowTaskFileRow> Files,
    IReadOnlyList<WorkflowTaskEventRow> Events);
