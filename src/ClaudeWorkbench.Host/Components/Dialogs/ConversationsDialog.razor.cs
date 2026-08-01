using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Conversations;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;

namespace ClaudeWorkbench.Host.Components.Dialogs;

// The Conversations dialog — a modal (opened from the Workbench toolbar), master/detail: a VERTICAL
// board CHOOSER on the left (Active pinned on top, then Archived and Abandoned) and a DETAILS pane
// on the right for the selected thread. A thread is a named, resumable pointer to an SDK session
// (autosaved as you talk). Active is computed (the thread whose session is live); the rest are
// stored. Every write here is metadata or disk reclamation — never watched source — no gate.
public partial class ConversationsDialog : IAsyncDisposable
{
    [Inject]
    private ConversationService Conversations { get; set; } = default!;

    [Inject]
    private SidecarClient Sidecar { get; set; } = default!;

    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private SidecarEventStream Events { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private DialogService DialogService { get; set; } = default!;

    [Inject]
    private NotificationService Notifications { get; set; } = default!;

    [Inject]
    private IConversationWorkspace Workspace { get; set; } = default!;

    private IReadOnlyList<ConversationRecord> threads = [];
    private string? activeConversationId;
    private string? selectedConversationId;
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

    // The two groups, top to bottom: Current is COMPUTED (the live session) and pinned on top;
    // Archived is every other saved conversation, newest-first. Disposal is Delete — no soft states.
    private static readonly (string Key, string Label)[] Columns =
    [
        ("active", "Current"),
        (ConversationStatus.Archived, "Archived"),
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
            resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js?v=6");
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
        threads = Conversations.List();
        activeConversationId = Conversations.ActiveConversation()?.ConversationId;
        if (selectedConversationId is not null && threads.All(thread => thread.ConversationId != selectedConversationId))
        {
            selectedConversationId = null;
        }

        // Show the Active (current) conversation's details by default when nothing is selected — on
        // form load, and again if the selection was cleared (e.g. after a delete).
        if (selectedConversationId is null && activeConversationId is not null)
        {
            ConversationRecord? active = threads.FirstOrDefault(thread => thread.ConversationId == activeConversationId);
            if (active is not null)
            {
                Select(active);
            }
        }
    }

    private ConversationRecord? SelectedThread =>
        selectedConversationId is null ? null : threads.FirstOrDefault(thread => thread.ConversationId == selectedConversationId);

    private void Select(ConversationRecord thread)
    {
        selectedConversationId = thread.ConversationId;
        editName = thread.Name;
        editDescription = thread.Description ?? string.Empty;
        editNote = thread.UserNote ?? string.Empty;
        message = null;
    }

    private bool IsActive(ConversationRecord thread) =>
        activeConversationId is not null && string.Equals(thread.ConversationId, activeConversationId, StringComparison.Ordinal);

    private bool IsSelected(ConversationRecord thread) =>
        string.Equals(thread.ConversationId, selectedConversationId, StringComparison.Ordinal);

    // Bucket a thread into exactly one column: the live one goes to Active regardless of its stored
    // status; everything else falls to its stored status column.
    private IEnumerable<ConversationRecord> ColumnThreads(string columnKey)
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

    // Save the details pane's name/description/note for the selected thread.
    private void SaveDetails()
    {
        ConversationRecord? thread = SelectedThread;
        if (thread is null)
        {
            return;
        }

        string name = editName.Trim();
        if (name.Length > 0 && !string.Equals(name, thread.Name, StringComparison.Ordinal))
        {
            Conversations.Rename(thread.ConversationId, name);
        }

        Conversations.SetDescription(thread.ConversationId, string.IsNullOrWhiteSpace(editDescription) ? null : editDescription.Trim());
        Conversations.SetUserNote(thread.ConversationId, string.IsNullOrWhiteSpace(editNote) ? null : editNote.Trim());
        Report("Saved thread details.", error: false);
        Load();
    }

    // Reopen a stored thread: restore its transcript from our mirror, then prime the sidecar to
    // resume that session. Blocked while a turn is live (the sidecar refuses a resume mid-turn).
    private async Task ResumeAsync(ConversationRecord thread)
    {
        if (thread.IsStub)
        {
            Report("This thread has no conversation to resume yet.", error: true);
            return;
        }

        if (TurnActive)
        {
            Report("Finish or interrupt the current turn before resuming another thread.", error: true);
            return;
        }

        // Guard against reopening a conversation from a different folder: its transcript is about that
        // other working directory, so continuing it here would be against the wrong code. Warn and let
        // the operator back out. (Normally every conversation in this list shares the current cwd.)
        if (!PathsEqual(thread.Cwd, Workspace.Cwd))
        {
            bool? proceed = await DialogService.Confirm(
                $"This conversation is from a different folder:\n{thread.Cwd}\n\nThe current workspace is:\n{Workspace.Cwd}\n\nResuming it here continues that conversation against this workspace's code. Resume anyway?",
                "Different workspace",
                new ConfirmOptions { OkButtonText = "Resume anyway", CancelButtonText = "Cancel" });
            if (proceed != true)
            {
                return;
            }
        }

        busy = true;
        string? sessionId = Conversations.PrepareResume(thread.ConversationId);
        bool ok = sessionId is not null && await Sidecar.ResumeAsync(sessionId);
        busy = false;
        if (ok)
        {
            // Repaint the chat pane from the runtime mirror so the operator sees the same history the
            // agent's context was just restored to (resume alone leaves the pane blank). Last 50
            // interactions, tool calls included, with a "restored" divider at the bottom.
            Session.RestoreHistory(Conversations.ReadTranscriptWindow(thread.ConversationId));

            // Close the modal and drop the operator back on the Workbench to continue the thread.
            DialogService.Close();
            return;
        }

        Report("Could not resume — the sidecar rejected it (a turn may be active).", error: true);
        Load();
    }

    // Case-insensitive, normalized path equality (Windows). Used to compare a conversation's stored
    // cwd to the current workspace's cwd before resuming.
    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            string na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task DeleteAsync(ConversationRecord thread)
    {
        // The current conversation can't be deleted — that would tangle "delete" with "start a new one".
        // Leave it (New Thread) or switch to another first; say so with a toast.
        if (IsActive(thread))
        {
            Notifications.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Warning,
                Summary = "Can't delete the current conversation",
                Detail = "Start or switch to another conversation first, then delete this one.",
                Duration = 5000,
            });
            return;
        }

        // A real in-app confirm dialog (not the browser's window.confirm).
        bool? confirmed = await DialogService.Confirm(
            $"Delete “{thread.Name}”? This removes the conversation AND its transcript from disk. This cannot be undone.",
            "Delete conversation",
            new ConfirmOptions { OkButtonText = "Delete", CancelButtonText = "Cancel" });
        if (confirmed != true)
        {
            return;
        }

        busy = true;
        bool ok = Conversations.DeleteConversation(thread.ConversationId);
        busy = false;
        if (string.Equals(thread.ConversationId, selectedConversationId, StringComparison.Ordinal))
        {
            selectedConversationId = null;
        }

        Report(ok ? "Deleted the conversation and reclaimed its transcript." : "Conversation was already gone.", error: !ok);
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

