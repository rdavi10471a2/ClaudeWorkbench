namespace ClaudeWorkbench.Host.Tasks;

public sealed record TaskBoardViewModel(
    string WatchedSolutionPath,
    string DatabasePath,
    string TaskMemoryRoot,
    IReadOnlyList<TaskBoardColumnViewModel> Columns,
    IReadOnlyList<TaskBoardArchivedDiscussionViewModel> ArchivedDiscussions,
    TaskBoardTaskDetailViewModel? SelectedTask)
{
    public static TaskBoardViewModel Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        [],
        null);
}

public sealed record TaskBoardColumnViewModel(
    string StateCode,
    string StateName,
    bool IsTerminal,
    bool IsArchive,
    IReadOnlyList<TaskBoardTaskViewModel> Tasks);

public sealed record TaskBoardTaskViewModel(
    string Id,
    int TaskNumber,
    string TaskLabel,
    string Name,
    string Description,
    string ShortName,
    string StateCode,
    string StateName,
    bool IsArchived,
    string UpdatedLabel,
    int FileCount);

public sealed record TaskBoardTaskDetailViewModel(
    string Id,
    int TaskNumber,
    string TaskLabel,
    string Name,
    string Description,
    string ShortName,
    string StateCode,
    string StateName,
    bool IsArchived,
    string CreatedLabel,
    string UpdatedLabel,
    string? NotesMarkdownPath,
    string NotesMarkdown,
    string? AgentNotesMarkdownPath,
    string AgentNotesMarkdown,
    IReadOnlyList<TaskBoardFileViewModel> Files,
    IReadOnlyList<TaskBoardEventViewModel> Events);

public sealed record TaskBoardFileViewModel(
    string Id,
    string RelativePath,
    string Intent,
    string FileRole);

public sealed record TaskBoardEventViewModel(
    string Id,
    string EventTypeCode,
    string EventTypeName,
    string Message,
    string CreatedLabel);

public sealed record TaskBoardArchivedDiscussionViewModel(
    string Id,
    string Name,
    string MarkdownPath,
    string? ThreadId,
    string Trigger,
    string TurnMode,
    string CreatedLabel);
