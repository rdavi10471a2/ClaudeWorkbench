namespace ClaudeWorkbench.Host.Tasks;

// UI-facing contract over the task board. There is exactly one watched workspace
// per host, so (unlike the ported Codex service) methods take no workspaceRoot --
// the service resolves the board + task-memory paths from the WorkspaceManager.
public interface IWorkflowTaskBoardViewService
{
    TaskBoardViewModel GetBoard(string? selectedTaskId);

    TaskBoardTaskViewModel CreateTask(string name, string? shortName, string? description, string? notesMarkdown);

    TaskBoardTaskViewModel UpdateTaskDetails(string taskId, string name, string? shortName, string? description);

    TaskBoardTaskViewModel MoveTask(string taskId, string stateCode);

    TaskBoardTaskViewModel UpdateNotes(string taskId, string notesMarkdown);

    TaskBoardTaskViewModel ArchiveTask(string taskId);

    TaskBoardTaskViewModel RestoreTask(string taskId);

    string ReadArchivedDiscussionContent(string archivedDiscussionId);
}
