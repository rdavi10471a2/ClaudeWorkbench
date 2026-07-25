using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Threads;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

// The conversation-Threads page — a master/detail: a kanban CHOOSER on the left (one column per
// lifecycle state) and a DETAILS pane on the right for the selected thread. A thread is a named,
// resumable pointer to an SDK session (autosaved as you talk). States are derived: Active is
// computed (the thread whose session is live); Planned/Archived/Abandoned are stored. Every write
// here is metadata or disk reclamation — never watched source — so nothing goes through the gate.
public partial class ThreadsTab : IAsyncDisposable
{
    [Inject]
    private ThreadService Threads { get; set; } = default!;

    [Inject]
    private SidecarClient Sidecar { get; set; } = default!;

    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private SidecarEventStream Events { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private IReadOnlyList<ThreadRecord> threads = [];
    private string? activeThreadId;
    private string? selectedThreadId;
    private bool busy;
    private string? message;
    private bool messageIsError;

    // Edit buffers for the details pane (populated on selection).
    private string editName = string.Empty;
    private string editDescription = string.Empty;
    private string editNote = string.Empty;

    private bool TurnActive => Session.Status.Working;

    // Resizable board|details split, using the shared splitter the other tabs use.
    private ElementReference threadsBody;
    private ElementReference threadsBoard;
    private ElementReference threadsDetails;
    private ElementReference threadsSplitter;
    private IJSObjectReference? resizeModule;

    // The board columns, left to right. Active is COMPUTED (the live session) — you land in it by
    // Resume/Open, not by moving a card; the others are stored statuses moved via the detail buttons.
    private static readonly (string Key, string Label)[] Columns =
    [
        (ThreadStatus.Planned, "Planned"),
        ("active", "Active"),
        (ThreadStatus.Archived, "Archived"),
        (ThreadStatus.Abandoned, "Abandoned"),
    ];

    protected override void OnInitialized()
    {
        Events.Changed += OnStreamChanged;
        Load();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Attach the shared column splitter. Guarded like GitTab: an unhandled OnAfterRender
        // exception would tear the circuit down, and the splitter element carries its own
        // "already attached" dataset flag so re-renders re-attach only when genuinely new.
        try
        {
            resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
            await resizeModule.InvokeVoidAsync("attachThreadsSplitter", threadsBody, threadsBoard, threadsDetails, threadsSplitter);
        }
        catch (JSException)
        {
            // Non-fatal: the splitter just won't be draggable this render; it re-attaches next render.
        }
    }

    private void OnStreamChanged() => InvokeAsync(() =>
    {
        // Autosave (session_started) may add/rename a thread and Active tracks the live session —
        // reload so the board and the Active badge stay honest.
        Load();
        StateHasChanged();
    });

    private void Load()
    {
        threads = Threads.List();
        activeThreadId = Threads.ActiveThread()?.ThreadId;
        if (selectedThreadId is not null && threads.All(thread => thread.ThreadId != selectedThreadId))
        {
            selectedThreadId = null;
        }
    }

    private ThreadRecord? SelectedThread =>
        selectedThreadId is null ? null : threads.FirstOrDefault(thread => thread.ThreadId == selectedThreadId);

    private void Select(ThreadRecord thread)
    {
        selectedThreadId = thread.ThreadId;
        editName = thread.Name;
        editDescription = thread.Description ?? string.Empty;
        editNote = thread.UserNote ?? string.Empty;
        message = null;
    }

    private bool IsActive(ThreadRecord thread) =>
        activeThreadId is not null && string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal);

    private bool IsSelected(ThreadRecord thread) =>
        string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal);

    // Bucket a thread into exactly one column: the live one goes to Active regardless of its stored
    // status; everything else falls to its stored status column.
    private IEnumerable<ThreadRecord> ColumnThreads(string columnKey)
    {
        if (columnKey == "active")
        {
            return threads.Where(IsActive);
        }

        return threads.Where(thread => !IsActive(thread) && thread.Status == columnKey);
    }

    private void Refresh()
    {
        Load();
        message = null;
    }

    private void NewStub()
    {
        ThreadRecord stub = Threads.CreateStub(name: null, description: null);
        Load();
        Select(stub);
        Report("Created a planned thread stub.", error: false);
    }

    // Save the details pane's name/description/note for the selected thread.
    private void SaveDetails()
    {
        ThreadRecord? thread = SelectedThread;
        if (thread is null)
        {
            return;
        }

        string name = editName.Trim();
        if (name.Length > 0 && !string.Equals(name, thread.Name, StringComparison.Ordinal))
        {
            Threads.Rename(thread.ThreadId, name);
        }

        Threads.SetDescription(thread.ThreadId, string.IsNullOrWhiteSpace(editDescription) ? null : editDescription.Trim());
        Threads.SetUserNote(thread.ThreadId, string.IsNullOrWhiteSpace(editNote) ? null : editNote.Trim());
        Report("Saved thread details.", error: false);
        Load();
    }

    // Reopen a stored thread: restore its transcript from our mirror, then prime the sidecar to
    // resume that session. Blocked while a turn is live (the sidecar refuses a resume mid-turn).
    private async Task ResumeAsync(ThreadRecord thread)
    {
        if (thread.IsStub)
        {
            Report("This is a planned stub — it has no conversation to resume yet.", error: true);
            return;
        }

        if (TurnActive)
        {
            Report("Finish or interrupt the current turn before resuming another thread.", error: true);
            return;
        }

        busy = true;
        string? sessionId = Threads.PrepareResume(thread.ThreadId);
        bool ok = sessionId is not null && await Sidecar.ResumeAsync(sessionId);
        busy = false;
        Report(
            ok
                ? $"Resumed “{thread.Name}” — switch to the Workbench tab and continue the conversation."
                : "Could not resume — the sidecar rejected it (a turn may be active).",
            error: !ok);
        Load();
    }

    private void TogglePromotion(ThreadRecord thread)
    {
        Threads.SetKind(thread.ThreadId, thread.Kind == ThreadKind.Task ? ThreadKind.Discussion : ThreadKind.Task);
        Load();
    }

    private void SetStatus(ThreadRecord thread, string status)
    {
        Threads.SetStatus(thread.ThreadId, status);
        Load();
    }

    private async Task DeleteAsync(ThreadRecord thread)
    {
        bool confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Delete “{thread.Name}”? This removes the thread AND its transcript from disk. This cannot be undone.");
        if (!confirmed)
        {
            return;
        }

        busy = true;
        bool ok = Threads.DeleteThread(thread.ThreadId);
        busy = false;
        if (string.Equals(thread.ThreadId, selectedThreadId, StringComparison.Ordinal))
        {
            selectedThreadId = null;
        }

        Report(ok ? "Deleted the thread and reclaimed its transcript." : "Thread was already gone.", error: !ok);
        Load();
    }

    private void Report(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    public async ValueTask DisposeAsync()
    {
        Events.Changed -= OnStreamChanged;
        if (resizeModule is not null)
        {
            try
            {
                await resizeModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone; nothing to release.
            }
        }
    }
}

