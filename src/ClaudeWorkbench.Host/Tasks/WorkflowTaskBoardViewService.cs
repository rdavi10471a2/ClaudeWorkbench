namespace ClaudeWorkbench.Host.Tasks;

// Maps repository rows to board view models and builds the columns (states +
// a synthetic Archived column). The board's on-disk location comes from the
// shared TaskBoardRepositoryFactory (also used by the MCP task tools).
public sealed class WorkflowTaskBoardViewService : IWorkflowTaskBoardViewService
{
    private readonly TaskBoardRepositoryFactory factory;

    public WorkflowTaskBoardViewService(TaskBoardRepositoryFactory factory)
    {
        this.factory = factory;
    }

    public TaskBoardViewModel GetBoard(string? selectedTaskId)
    {
        if (!factory.HasWorkspace)
        {
            return TaskBoardViewModel.Empty;
        }

        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        List<TaskBoardColumnViewModel> columns = snapshot.States
            .OrderBy(state => state.SortOrder)
            .Select(state => new TaskBoardColumnViewModel(
                state.Code,
                state.Name,
                state.IsTerminal,
                false,
                snapshot.Tasks
                    .Where(task => !task.IsArchived && task.StateCode.Equals(state.Code, StringComparison.Ordinal))
                    .Select(task => ToTaskViewModel(task, snapshot.Files))
                    .ToArray()))
            .ToList();
        columns.Add(new TaskBoardColumnViewModel(
            "Archived",
            "Archived",
            true,
            true,
            snapshot.Tasks
                .Where(task => task.IsArchived)
                .Select(task => ToTaskViewModel(task, snapshot.Files))
                .ToArray()));

        WorkflowTaskRow? selectedRow = SelectTask(snapshot.Tasks, selectedTaskId);
        TaskBoardTaskDetailViewModel? selectedTask = selectedRow is null
            ? null
            : ToDetailViewModel(repository, selectedRow, snapshot.Files, snapshot.Events);

        return new TaskBoardViewModel(
            factory.WatchedSolutionPath ?? string.Empty,
            repository.DatabasePath,
            repository.TaskMemoryRoot,
            columns,
            repository.ListArchivedDiscussions()
                .Select(row => new TaskBoardArchivedDiscussionViewModel(
                    row.Id,
                    row.Name,
                    row.MarkdownPath,
                    row.ThreadId,
                    row.Trigger,
                    row.TurnMode,
                    FormatDate(row.CreatedAt)))
                .ToArray(),
            selectedTask);
    }

    public TaskBoardTaskViewModel CreateTask(string name, string? shortName, string? description, string? notesMarkdown)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.CreateTask(name, shortName, description, notesMarkdown);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public TaskBoardTaskViewModel UpdateTaskDetails(string taskId, string name, string? shortName, string? description)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.UpdateTaskDetails(taskId, name, shortName, description);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public TaskBoardTaskViewModel MoveTask(string taskId, string stateCode)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.MoveTask(taskId, stateCode);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public TaskBoardTaskViewModel UpdateNotes(string taskId, string notesMarkdown)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.UpdateNotes(taskId, notesMarkdown);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public TaskBoardTaskViewModel ArchiveTask(string taskId)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.ArchiveTask(taskId);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public TaskBoardTaskViewModel RestoreTask(string taskId)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        WorkflowTaskRow row = repository.RestoreTask(taskId);
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return ToTaskViewModel(row, snapshot.Files);
    }

    public string ReadArchivedDiscussionContent(string archivedDiscussionId)
    {
        WorkflowTaskBoardRepository repository = CreateRepository();
        ArchivedDiscussionRow row = repository.ListArchivedDiscussions()
            .FirstOrDefault(candidate => candidate.Id.Equals(archivedDiscussionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Archived discussion was not found: " + archivedDiscussionId);
        string archiveRoot = Path.GetFullPath(Path.Combine(
            factory.WorkspaceRoot,
            "planning",
            "archived-discussions"));
        string archivePath = Path.GetFullPath(row.MarkdownPath);
        if (!IsPathWithinRoot(archivePath, archiveRoot))
        {
            throw new InvalidOperationException("Archived discussion file is outside the archived discussion store.");
        }

        if (!File.Exists(archivePath))
        {
            throw new InvalidOperationException("Archived discussion file is missing: " + archivePath);
        }

        return File.ReadAllText(archivePath);
    }

    private WorkflowTaskBoardRepository CreateRepository()
    {
        return factory.Create();
    }

    private static WorkflowTaskRow? SelectTask(IReadOnlyList<WorkflowTaskRow> tasks, string? selectedTaskId)
    {
        if (!string.IsNullOrWhiteSpace(selectedTaskId))
        {
            WorkflowTaskRow? selected = tasks.FirstOrDefault(task =>
                task.Id.Equals(selectedTaskId, StringComparison.Ordinal));
            if (selected is not null)
            {
                return selected;
            }
        }

        return tasks.FirstOrDefault(task => !task.IsArchived && task.StateCode.Equals("Active", StringComparison.Ordinal))
            ?? tasks.FirstOrDefault(task => !task.IsArchived)
            ?? tasks.FirstOrDefault();
    }

    private static TaskBoardTaskViewModel ToTaskViewModel(
        WorkflowTaskRow task,
        IReadOnlyList<WorkflowTaskFileRow> files)
    {
        return new TaskBoardTaskViewModel(
            task.Id,
            task.TaskNumber,
            FormatTaskLabel(task.TaskNumber),
            task.Name,
            task.Description,
            task.ShortName,
            task.StateCode,
            task.StateName,
            task.IsArchived,
            FormatDate(task.UpdatedAt),
            files.Count(file => file.TaskId.Equals(task.Id, StringComparison.Ordinal)));
    }

    private static TaskBoardTaskDetailViewModel ToDetailViewModel(
        WorkflowTaskBoardRepository repository,
        WorkflowTaskRow task,
        IReadOnlyList<WorkflowTaskFileRow> files,
        IReadOnlyList<WorkflowTaskEventRow> events)
    {
        return new TaskBoardTaskDetailViewModel(
            task.Id,
            task.TaskNumber,
            FormatTaskLabel(task.TaskNumber),
            task.Name,
            task.Description,
            task.ShortName,
            task.StateCode,
            task.StateName,
            task.IsArchived,
            FormatDate(task.CreatedAt),
            FormatDate(task.UpdatedAt),
            task.NotesMarkdownPath,
            repository.ReadNotes(task.NotesMarkdownPath),
            task.AgentNotesMarkdownPath,
            repository.ReadNotes(task.AgentNotesMarkdownPath),
            files
                .Where(file => file.TaskId.Equals(task.Id, StringComparison.Ordinal))
                .Select(file => new TaskBoardFileViewModel(
                    file.Id,
                    file.RelativePath,
                    file.Intent ?? string.Empty,
                    file.FileRole ?? string.Empty))
                .ToArray(),
            events
                .Where(taskEvent => taskEvent.TaskId.Equals(task.Id, StringComparison.Ordinal))
                .OrderByDescending(taskEvent => taskEvent.CreatedAt)
                .Take(80)
                .Select(taskEvent => new TaskBoardEventViewModel(
                    taskEvent.Id,
                    taskEvent.EventTypeCode,
                    taskEvent.EventTypeName,
                    taskEvent.Message ?? string.Empty,
                    FormatDate(taskEvent.CreatedAt)))
                .ToArray());
    }

    private static string FormatDate(DateTime value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatTaskLabel(int taskNumber)
    {
        return "TASK-" + taskNumber.ToString("0000");
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedCandidate.Equals(normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.AltDirectorySeparatorChar,
                comparison);
    }
}
