using ClaudeWorkbench.Host.Tasks;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs.Tasks;

public partial class TaskWorkspace : ComponentBase
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private string taskName = string.Empty;
    private string taskDescription = string.Empty;
    private string notesMarkdown = string.Empty;
    private string newFilePath = string.Empty;
    private string newFileIntent = string.Empty;
    private string newFileRole = string.Empty;
    private string newComment = string.Empty;
    private string selectedPane = "Task";
    private string selectedStateCode = string.Empty;
    private string? loadedTaskId;

    [Parameter]
    public TaskBoardTaskDetailViewModel? Task { get; set; }

    [Parameter]
    public IReadOnlyList<TaskBoardColumnViewModel> StateOptions { get; set; } = [];

    [Parameter]
    public bool IsNavigatorVisible { get; set; }

    [Parameter]
    public EventCallback<TaskDetailsSaveRequest> OnSaveDetails { get; set; }

    [Parameter]
    public EventCallback<string> OnSaveNotes { get; set; }

    [Parameter]
    public EventCallback<TaskFileAddRequest> OnAddFile { get; set; }

    [Parameter]
    public EventCallback<string> OnAddComment { get; set; }

    [Parameter]
    public EventCallback OnToggleNavigator { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    protected override void OnParametersSet()
    {
        if (Task is null)
        {
            loadedTaskId = null;
            taskName = string.Empty;
            taskDescription = string.Empty;
            notesMarkdown = string.Empty;
            selectedStateCode = string.Empty;
            return;
        }

        if (loadedTaskId is not null && loadedTaskId.Equals(Task.Id, StringComparison.Ordinal))
        {
            return;
        }

        loadedTaskId = Task.Id;
        taskName = Task.Name;
        taskDescription = Task.Description;
        notesMarkdown = Task.NotesMarkdown;
        selectedStateCode = Task.IsArchived ? "Archived" : Task.StateCode;
        newFilePath = string.Empty;
        newFileIntent = string.Empty;
        newFileRole = string.Empty;
        newComment = string.Empty;
    }

    private async Task SaveDetails()
    {
        if (Task is null)
        {
            return;
        }

        string stateCode = string.IsNullOrWhiteSpace(selectedStateCode)
            ? Task.StateCode
            : selectedStateCode;
        await OnSaveDetails.InvokeAsync(new TaskDetailsSaveRequest(Task.Id, taskName, taskDescription, null, stateCode, notesMarkdown));
    }

    private async Task SaveNotes()
    {
        if (Task is null)
        {
            return;
        }

        await OnSaveNotes.InvokeAsync(notesMarkdown);
    }

    private async Task AddFile()
    {
        if (Task is null || string.IsNullOrWhiteSpace(newFilePath))
        {
            return;
        }

        await OnAddFile.InvokeAsync(new TaskFileAddRequest(Task.Id, newFilePath, newFileIntent, newFileRole));
        newFilePath = string.Empty;
        newFileIntent = string.Empty;
        newFileRole = string.Empty;
    }

    private async Task AddComment()
    {
        if (string.IsNullOrWhiteSpace(newComment))
        {
            return;
        }

        await OnAddComment.InvokeAsync(newComment);
        newComment = string.Empty;
    }

    private Task ToggleNavigator()
    {
        return OnToggleNavigator.InvokeAsync();
    }

    private Task RefreshWorkspace()
    {
        return OnRefresh.InvokeAsync();
    }

    private void SelectPane(string pane)
    {
        selectedPane = pane;
    }

    private bool IsPane(string pane)
    {
        return selectedPane.Equals(pane, StringComparison.Ordinal);
    }

    private static string RenderMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return Markdown.ToHtml(markdown, MarkdownPipeline);
    }
}
