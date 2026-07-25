using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Threads;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

// The conversation-Threads page — the thread list that replaced the retired Tasks board. A thread
// is a named, resumable pointer to an SDK session (autosaved as you talk). States are a DERIVED
// view of lifecycle: Active is computed (the thread whose session is the live one), the rest
// (Planned/Archived/Abandoned) are stored. Every write here is metadata or disk reclamation — never
// watched source — so nothing goes through the governance gate.
public partial class ThreadsTab : IDisposable
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
    private bool busy;
    private string? message;
    private bool messageIsError;

    // Inline editors, keyed by thread id (only one open at a time).
    private string? editingNameId;
    private string editingName = string.Empty;
    private string? editingDetailsId;
    private string editingDescription = string.Empty;
    private string editingNote = string.Empty;

    private bool TurnActive => Session.Status.Working;

    protected override void OnInitialized()
    {
        Events.Changed += OnStreamChanged;
        Load();
    }

    private void OnStreamChanged() => InvokeAsync(() =>
    {
        // The autosave (session_started) may have added/renamed a thread, and Active tracks the live
        // session — reload so the list and the Active badge stay honest.
        Load();
        StateHasChanged();
    });

    private void Load()
    {
        threads = Threads.List();
        activeThreadId = Threads.ActiveThread()?.ThreadId;
    }

    private bool IsActive(ThreadRecord thread) =>
        activeThreadId is not null && string.Equals(thread.ThreadId, activeThreadId, StringComparison.Ordinal);

    private static string StatusLabel(ThreadRecord thread, bool active) =>
        active ? "Active"
        : thread.Status switch
        {
            ThreadStatus.Planned => "Planned",
            ThreadStatus.Abandoned => "Abandoned",
            _ => "Archived",
        };

    private static string StatusCss(ThreadRecord thread, bool active) =>
        active ? "active"
        : thread.Status switch
        {
            ThreadStatus.Planned => "planned",
            ThreadStatus.Abandoned => "abandoned",
            _ => "archived",
        };

    private void Refresh()
    {
        Load();
        message = null;
    }

    private void NewStub()
    {
        Threads.CreateStub(name: null, description: null);
        Report("Created a planned thread stub.", error: false);
        Load();
    }

    // Reopen a stored thread: prime the sidecar to resume its session. The operator then continues
    // in the Workbench tab. Blocked while a turn is live (the sidecar refuses a resume mid-turn).
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
        bool ok = await Sidecar.ResumeAsync(thread.SessionId!);
        busy = false;
        if (ok)
        {
            Report($"Resumed “{thread.Name}” — switch to the Workbench tab and continue the conversation.", error: false);
        }
        else
        {
            Report("Could not resume — the sidecar rejected it (a turn may be active).", error: true);
        }

        Load();
    }

    private void BeginRename(ThreadRecord thread)
    {
        editingNameId = thread.ThreadId;
        editingName = thread.Name;
        editingDetailsId = null;
    }

    private void SaveRename(ThreadRecord thread)
    {
        string name = editingName.Trim();
        if (!string.IsNullOrEmpty(name) && !string.Equals(name, thread.Name, StringComparison.Ordinal))
        {
            Threads.Rename(thread.ThreadId, name);
        }

        editingNameId = null;
        Load();
    }

    private void CancelRename() => editingNameId = null;

    private void BeginDetails(ThreadRecord thread)
    {
        editingDetailsId = thread.ThreadId;
        editingDescription = thread.Description ?? string.Empty;
        editingNote = thread.UserNote ?? string.Empty;
        editingNameId = null;
    }

    private void SaveDetails(ThreadRecord thread)
    {
        Threads.SetDescription(thread.ThreadId, string.IsNullOrWhiteSpace(editingDescription) ? null : editingDescription.Trim());
        Threads.SetUserNote(thread.ThreadId, string.IsNullOrWhiteSpace(editingNote) ? null : editingNote.Trim());
        editingDetailsId = null;
        Report("Saved thread details.", error: false);
        Load();
    }

    private void CancelDetails() => editingDetailsId = null;

    private void SetStatus(ThreadRecord thread, string status)
    {
        Threads.SetStatus(thread.ThreadId, status);
        Load();
    }

    private void TogglePromotion(ThreadRecord thread)
    {
        string next = thread.Kind == ThreadKind.Task ? ThreadKind.Discussion : ThreadKind.Task;
        Threads.SetKind(thread.ThreadId, next);
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
        Report(ok ? "Deleted the thread and reclaimed its transcript." : "Thread was already gone.", error: !ok);
        Load();
    }

    private void Report(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    public void Dispose() => Events.Changed -= OnStreamChanged;
}
