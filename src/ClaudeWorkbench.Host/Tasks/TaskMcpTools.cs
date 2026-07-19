using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ClaudeWorkbench.Host.Tasks;

// MCP task surface for the governed agent. The operator curates the board (which
// task is Active, its state) through the Tasks tab; the agent reads the current
// task + its notes and writes back its own agent-notes as durable memory. Reads
// auto-allow at the sidecar gate; update_agent_notes writes only to the runtime
// task-memory store (planning/task-memory), never to watched source.
[McpServerToolType]
public sealed class TaskMcpTools
{
    private readonly TaskBoardRepositoryFactory factory;

    public TaskMcpTools(TaskBoardRepositoryFactory factory)
    {
        this.factory = factory;
    }

    [McpServerTool]
    [Description("Return the current Active task for the watched workspace, including its description plus the current user-notes and agent-notes markdown content. Call this at the start of a turn to load task context and your prior agent notes.")]
    public CurrentTaskResult GetCurrentTask()
    {
        if (!factory.HasWorkspace)
        {
            return CurrentTaskResult.NoWorkspace();
        }

        WorkflowTaskBoardRepository repository = factory.Create();
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        WorkflowTaskRow? active = snapshot.Tasks.FirstOrDefault(task =>
            !task.IsArchived && task.StateCode.Equals("Active", StringComparison.Ordinal));
        if (active is null)
        {
            return CurrentTaskResult.NoCurrentTask(factory.WatchedSolutionPath);
        }

        return new CurrentTaskResult(
            true,
            factory.WatchedSolutionPath,
            true,
            active.Id,
            active.TaskNumber,
            "TASK-" + active.TaskNumber.ToString("0000"),
            active.Name,
            active.Description,
            active.ShortName,
            active.StateCode,
            active.StateName,
            repository.ReadNotes(active.NotesMarkdownPath),
            repository.ReadNotes(active.AgentNotesMarkdownPath),
            null);
    }

    [McpServerTool]
    [Description("List tasks on the board (id, number, label, name, state), most recently updated first. Use this to find a task id to act on. Archived tasks are excluded unless includeArchived is true.")]
    public IReadOnlyList<TaskSummary> ListTasks(
        [Description("Include archived tasks in the list.")] bool includeArchived = false)
    {
        if (!factory.HasWorkspace)
        {
            return [];
        }

        WorkflowTaskBoardRepository repository = factory.Create();
        WorkflowTaskBoardSnapshot snapshot = repository.LoadSnapshot();
        return snapshot.Tasks
            .Where(task => includeArchived || !task.IsArchived)
            .Select(task => new TaskSummary(
                task.Id,
                task.TaskNumber,
                "TASK-" + task.TaskNumber.ToString("0000"),
                task.Name,
                task.StateCode,
                task.StateName,
                task.IsArchived))
            .ToArray();
    }

    [McpServerTool]
    [Description("Replace the agent-notes (task memory) markdown for a task. Pass a task id from get_current_task or list_tasks. Agent notes are your durable scratchpad, persisted across turns and threads and shown to the operator on the task's Agent Notes pane. This writes to the runtime task-memory store only and never touches watched source.")]
    public AgentNotesResult UpdateAgentNotes(
        [Description("The task id (from get_current_task or list_tasks).")] string taskId,
        [Description("The full agent-notes markdown to store. Replaces any existing agent notes for the task.")] string notesMarkdown)
    {
        if (!factory.HasWorkspace)
        {
            return new AgentNotesResult(false, taskId, "No watched workspace is selected.");
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new AgentNotesResult(false, taskId, "A task id is required.");
        }

        try
        {
            WorkflowTaskBoardRepository repository = factory.Create();
            repository.UpdateAgentNotes(taskId, notesMarkdown ?? string.Empty);
            return new AgentNotesResult(true, taskId, "Agent notes updated.");
        }
        catch (Exception exception)
        {
            return new AgentNotesResult(false, taskId, exception.Message);
        }
    }
}

public sealed record CurrentTaskResult(
    bool Success,
    string? WatchedSolutionPath,
    bool HasCurrentTask,
    string? TaskId,
    int TaskNumber,
    string? TaskLabel,
    string? TaskName,
    string? TaskDescription,
    string? TaskShortName,
    string? TaskStateCode,
    string? TaskStateName,
    string? UserNotes,
    string? AgentNotes,
    string? Message)
{
    public static CurrentTaskResult NoWorkspace()
    {
        return new CurrentTaskResult(false, null, false, null, 0, null, null, null, null, null, null, null, null,
            "No watched workspace is selected.");
    }

    public static CurrentTaskResult NoCurrentTask(string? watchedSolutionPath)
    {
        return new CurrentTaskResult(true, watchedSolutionPath, false, null, 0, null, null, null, null, null, null, null, null,
            "No task is Active. Move a task to Active on the Tasks board to set the current task.");
    }
}

public sealed record TaskSummary(
    string Id,
    int TaskNumber,
    string TaskLabel,
    string Name,
    string StateCode,
    string StateName,
    bool IsArchived);

public sealed record AgentNotesResult(bool Success, string TaskId, string Message);
