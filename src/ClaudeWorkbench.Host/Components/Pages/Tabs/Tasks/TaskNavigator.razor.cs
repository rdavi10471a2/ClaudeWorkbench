using ClaudeWorkbench.Host.Tasks;
using Microsoft.AspNetCore.Components;
using Radzen;
using WebMouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs.Tasks;

public partial class TaskNavigator : ComponentBase
{
    private string newTaskName = string.Empty;

    [Inject]
    private ContextMenuService ContextMenuService { get; set; } = default!;

    [Parameter]
    public IReadOnlyList<TaskBoardColumnViewModel> Columns { get; set; } = [];

    [Parameter]
    public string? SelectedTaskId { get; set; }

    [Parameter]
    public EventCallback<string> OnSelectTask { get; set; }

    [Parameter]
    public EventCallback<TaskCreateRequest> OnCreateTask { get; set; }

    [Parameter]
    public EventCallback<TaskMoveRequest> OnMoveTask { get; set; }

    [Parameter]
    public EventCallback<string> OnArchiveTask { get; set; }

    [Parameter]
    public EventCallback<string> OnRestoreTask { get; set; }

    private async Task CreateTask()
    {
        if (string.IsNullOrWhiteSpace(newTaskName))
        {
            return;
        }

        await OnCreateTask.InvokeAsync(new TaskCreateRequest(newTaskName, null));
        newTaskName = string.Empty;
    }

    private async Task SelectTask(string taskId)
    {
        await OnSelectTask.InvokeAsync(taskId);
    }

    private bool IsSelected(string taskId)
    {
        return !string.IsNullOrWhiteSpace(SelectedTaskId)
            && SelectedTaskId.Equals(taskId, StringComparison.Ordinal);
    }

    private void OpenContextMenu(WebMouseEventArgs args, TaskBoardTaskViewModel task)
    {
        List<ContextMenuItem> items = BuildContextMenuItems(task);
        ContextMenuService.Open(args, items, menuArgs => _ = InvokeAsync(() => HandleContextMenuClick(menuArgs)));
    }

    private List<ContextMenuItem> BuildContextMenuItems(TaskBoardTaskViewModel task)
    {
        List<ContextMenuItem> items = [];

        if (task.IsArchived)
        {
            items.Add(new ContextMenuItem
            {
                Text = "Restore task",
                Icon = "unarchive",
                Value = BuildMenuValue("restore", task.Id, null)
            });

            return items;
        }

        foreach (TaskBoardColumnViewModel column in Columns.Where(column => !column.IsArchive))
        {
            items.Add(new ContextMenuItem
            {
                Text = "Move to " + column.StateName,
                Icon = "drive_file_move",
                Disabled = task.StateCode.Equals(column.StateCode, StringComparison.Ordinal),
                Value = BuildMenuValue("move", task.Id, column.StateCode)
            });
        }

        if (task.StateCode.Equals("Done", StringComparison.Ordinal))
        {
            items.Add(new ContextMenuItem
            {
                Text = "Archive task",
                Icon = "archive",
                Value = BuildMenuValue("archive", task.Id, null)
            });
        }

        return items;
    }

    private async Task HandleContextMenuClick(MenuItemEventArgs args)
    {
        if (args.Value is not string value)
        {
            return;
        }

        string[] parts = value.Split('|', 3);
        if (parts.Length < 2)
        {
            return;
        }

        string action = parts[0];
        string taskId = parts[1];
        ContextMenuService.Close();

        if (action.Equals("move", StringComparison.Ordinal) && parts.Length == 3)
        {
            await OnMoveTask.InvokeAsync(new TaskMoveRequest(taskId, parts[2]));
            return;
        }

        if (action.Equals("archive", StringComparison.Ordinal))
        {
            await OnArchiveTask.InvokeAsync(taskId);
            return;
        }

        if (action.Equals("restore", StringComparison.Ordinal))
        {
            await OnRestoreTask.InvokeAsync(taskId);
        }
    }

    private static string BuildMenuValue(string action, string taskId, string? stateCode)
    {
        return stateCode is null
            ? action + "|" + taskId
            : action + "|" + taskId + "|" + stateCode;
    }
}
