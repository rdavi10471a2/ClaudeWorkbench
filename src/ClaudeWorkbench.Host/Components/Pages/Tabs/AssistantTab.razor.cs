using System.Text;
using ClaudeWorkbench.Host.Components.Dialogs;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using ClaudeWorkbench.Host.Conversations;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

public partial class AssistantTab : IDisposable, IAsyncDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private DialogService Dialogs { get; set; } = default!;

    [Inject]
    private ConversationService ConversationStore { get; set; } = default!;

    [Inject]
    private SidecarEventStream Events { get; set; } = default!;

    // The current conversation shown in the composer's conversation bar. Provisioned on load so it's
    // named before the first turn; refreshed only when the live session actually changes (not per chunk).
    private string? currentConversationName;
    private string? lastConversationSessionId;

    private string ConversationLabel => currentConversationName ?? "Unsaved";

    private ElementReference assistantLayout;
    private ElementReference chatComposer;
    private ElementReference assistantSplitter;
    private ElementReference transcriptPanel;
    private ElementReference transcriptView;
    private ElementReference chatInput;
    private ElementReference composerPasteZone;
    private IJSObjectReference? resizeModule;
    private IJSObjectReference? attachModule;
    private DotNetObjectReference<AssistantTab>? selfRef;
    private string draft = string.Empty;
    // On by default (operator preference): the agent's tool calls auto-approve without a per-call prompt.
    // The merge review still gates every write to watched source — this only skips the tool-permission gate.
    // Per-thread; New Thread resets it back to this default (on).
    private bool autoApprove = true;
    private bool usageOpen;
    private bool wasWorking;
    // True when the TRANSCRIPT changed and the per-render JS (scroll/highlight/mermaid) must run.
    // Set in OnChanged (a streamed/new message); NOT set by composer keystrokes, so typing skips
    // the transcript-wide JS that was yanking the view on every character. True initially so the
    // first render processes existing content.
    private bool transcriptDirty = true;
    private UsageSnapshot? usage;
    private readonly List<PendingAttachment> attachments = new();
    private string? uploadError;

    private sealed record PendingAttachment(string Name, string Path);

    private bool Working => Session.Status.Working;

    private bool HasTranscript => Session.Transcript.Count > 0;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
        ConversationStore.ConversationCreated += OnConversationCreated;
        ConversationStore.ConversationsChanged += OnConversationsChanged;
        // Provision (or reuse) the current conversation so its name shows before the first turn.
        currentConversationName = ConversationStore.EnsureCurrentConversation().Name;
        lastConversationSessionId = Events.CurrentSessionId;
    }

    // A new conversation was created (New Thread / first-turn autosave) — reflect its name in the bar.
    private void OnConversationCreated(ConversationRecord conversation) => InvokeAsync(() =>
    {
        currentConversationName = conversation.Name;
        lastConversationSessionId = conversation.SessionId;
        StateHasChanged();
    });

    // A conversation was renamed/deleted — re-read the current one so the bar stays honest.
    private void OnConversationsChanged() => InvokeAsync(() =>
    {
        currentConversationName = ConversationStore.ActiveConversation()?.Name;
        StateHasChanged();
    });

    // Refresh the bar's name only when the live session actually changed (resume/new), not on every
    // streamed chunk (which also raises Session.Changed).
    private void RefreshConversationName()
    {
        string? sessionId = Events.CurrentSessionId;
        if (string.Equals(sessionId, lastConversationSessionId, StringComparison.Ordinal))
        {
            return;
        }

        lastConversationSessionId = sessionId;
        currentConversationName = ConversationStore.ActiveConversation()?.Name;
    }

    // Open the Conversations board (browse / resume / rename / delete) as a modal.
    private async Task OpenConversationsAsync()
    {
        await Dialogs.OpenAsync<ConversationsDialog>(
            "Conversations",
            null,
            new DialogOptions { Width = "90vw", Height = "82vh", Resizable = true, Draggable = false });
    }

    private void OnChanged()
    {
        InvokeAsync(async () =>
        {
            bool working = Session.Status.Working;
            // Refresh usage when a turn just finished (if the panel is open).
            if (wasWorking && !working && usageOpen)
            {
                usage = await Session.GetUsageAsync();
            }

            wasWorking = working;
            RefreshConversationName();
            // The session changed (message streamed/added/status) — the transcript may have grown,
            // so the next render must re-run the transcript-wide JS.
            transcriptDirty = true;
            StateHasChanged();
        });
    }

    private async Task ToggleUsageAsync()
    {
        usageOpen = !usageOpen;
        if (usageOpen)
        {
            await RefreshUsageAsync();
        }
    }

    private async Task RefreshUsageAsync()
    {
        usage = await Session.GetUsageAsync();
        StateHasChanged();
    }

    private static string Pct(double? value)
    {
        return value is double d ? $"{d:0}%" : "—";
    }

    private static string BarWidth(double? value)
    {
        return value is double d ? $"{Math.Clamp(d, 0, 100):0}%" : "0%";
    }

    private static string Reset(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out DateTimeOffset when))
        {
            TimeSpan delta = when - DateTimeOffset.UtcNow;
            if (delta <= TimeSpan.Zero)
            {
                return "soon";
            }

            if (delta.TotalDays >= 1)
            {
                return $"in {(int)delta.TotalDays}d {delta.Hours}h";
            }

            if (delta.TotalHours >= 1)
            {
                return $"in {(int)delta.TotalHours}h {delta.Minutes}m";
            }

            return $"in {delta.Minutes}m";
        }

        return iso;
    }

    private static MarkupString RenderMarkdown(string text)
    {
        return new MarkupString(MarkdownRenderer.ToHtml(text));
    }

    // A read-image entry carries the local path; serve it through /local-file.
    private static string LocalFileUrl(string path)
    {
        return "/local-file?path=" + Uri.EscapeDataString(path);
    }

    private void RemoveAttachment(PendingAttachment attachment)
    {
        attachments.Remove(attachment);
    }

    // Called from composerAttach.js after a pasted/dropped blob is saved to uploads/. Same result
    // as the file picker's OnFilesSelectedAsync: add it as a pending attachment and re-render.
    // InvokeAsync marshals back onto the render thread (the JS callback is off it).
    [JSInvokable]
    public Task AddUploadedAttachment(string name, string path)
    {
        return InvokeAsync(() =>
        {
            uploadError = null;
            attachments.Add(new PendingAttachment(name, path));
            StateHasChanged();
        });
    }

    [JSInvokable]
    public Task ReportUploadError(string message)
    {
        return InvokeAsync(() =>
        {
            uploadError = message;
            StateHasChanged();
        });
    }

    private async Task SubmitAsync()
    {
        if (Working || (string.IsNullOrWhiteSpace(draft) && attachments.Count == 0))
        {
            return;
        }

        string prompt = ComposePrompt(draft, attachments);
        draft = string.Empty;
        attachments.Clear();
        uploadError = null;
        await Session.SendAsync(prompt, autoApprove);
    }

    private static string ComposePrompt(string draft, IReadOnlyList<PendingAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return draft;
        }

        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(draft))
        {
            builder.Append(draft).Append("\n\n");
        }

        // A folder attachment is an extracted archive: hand over the folder + a manifest so the agent
        // explores it lazily (Glob/Grep/Read), rather than us attaching every file. Plain files are
        // still listed for a direct Read.
        List<PendingAttachment> files = attachments.Where(a => !Directory.Exists(a.Path)).ToList();
        List<PendingAttachment> folders = attachments.Where(a => Directory.Exists(a.Path)).ToList();

        if (files.Count > 0)
        {
            builder.Append("[Operator attached ").Append(files.Count)
                .Append(files.Count == 1 ? " file" : " files")
                .Append(" — read each with the Read tool:]\n");
            foreach (PendingAttachment file in files)
            {
                builder.Append("- ").Append(file.Path).Append('\n');
            }
        }

        foreach (PendingAttachment folder in folders)
        {
            AppendFolderHandoff(builder, folder.Path);
        }

        return builder.ToString();
    }

    // Describe an extracted-archive folder for the agent: the readable root plus a capped manifest of
    // its files. The agent uses Glob/Grep to explore and Read to open what it needs.
    private static void AppendFolderHandoff(StringBuilder builder, string folderPath)
    {
        const int manifestCap = 200;
        List<string> relative = new();
        int total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                total++;
                if (relative.Count < manifestCap)
                {
                    relative.Add(Path.GetRelativePath(folderPath, file).Replace('\\', '/'));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append("[Operator attached an extracted archive folder: ").Append(folderPath)
            .Append(" (").Append(total).Append(total == 1 ? " file" : " files")
            .Append("). It is readable — explore it with Glob/Grep and Read the files you need. Paths below are relative to that folder:]\n");
        foreach (string entry in relative)
        {
            builder.Append("- ").Append(entry).Append('\n');
        }

        if (total > relative.Count)
        {
            builder.Append("- … and ").Append(total - relative.Count).Append(" more (use Glob to list all).\n");
        }
    }

    private async Task StopAsync()
    {
        await Session.StopAsync();
    }

    // True from the moment New Thread is clicked until its session reset finishes. Disables the New
    // button for the whole operation (not just during a turn) so a second click can't race the reset
    // still in flight — the click would otherwise be swallowed and no dialog would open.
    private bool newThreadInProgress;

    private async Task NewThreadAsync()
    {
        if (Working || newThreadInProgress)
        {
            return;
        }

        newThreadInProgress = true;
        try
        {
            await RunNewThreadAsync();
        }
        finally
        {
            newThreadInProgress = false;
        }
    }

    private async Task RunNewThreadAsync()
    {
        // The conversation being left. If it's still on a machine default name, offer to name it now
        // in the popup (otherwise it lingers in Conversations as conversation-YYYY-MM-DD-N). Captured
        // BEFORE the reset, which clears the live session id.
        ConversationRecord? leaving = ConversationStore.ActiveConversation();
        string? leavingDefaultName = leaving is not null && ConversationRepository.IsDefaultName(leaving.Name)
            ? leaving.Name
            : null;

        // Popup to (optionally) rename the conversation being left AND name the upcoming one. Cancel
        // (null) aborts. The new name (possibly blank -> the default) is held and applied to the
        // thread that autosaves on the first turn.
        object? chosen = await Dialogs.OpenAsync<NewThreadDialog>(
            "Start a new conversation",
            new Dictionary<string, object?>
            {
                [nameof(NewThreadDialog.LeavingName)] = leavingDefaultName,
                [nameof(NewThreadDialog.NewDefaultName)] = ConversationStore.PeekNextDefaultName(),
            },
            new DialogOptions { Width = "440px", Resizable = false, Draggable = false });
        if (chosen is not NewThreadDialog.Names names)
        {
            return;
        }

        // The conversation being left (only offered when it's still on a default name): the operator
        // ticked Keep or not. Keep -> rename it if they gave it a better name (else leave the default).
        // Don't keep -> discard it, reclaiming its runtime row + mirror, so Conversations doesn't fill
        // with junk conversation-YYYY-MM-DD-N threads. (Cancel returns null above and skips all of this.)
        if (leaving is not null && leavingDefaultName is not null)
        {
            if (names.KeepLeaving)
            {
                if (!string.IsNullOrWhiteSpace(names.LeavingName)
                    && !string.Equals(names.LeavingName, leaving.Name, StringComparison.Ordinal))
                {
                    ConversationStore.Rename(leaving.ConversationId, names.LeavingName);
                }
            }
            else
            {
                ConversationStore.DeleteConversation(leaving.ConversationId);
            }
        }

        // Persist the new conversation immediately so it's the Active thread and shows on the top bar
        // and in Conversations at once; its first turn adopts this row.
        ConversationStore.StartNamedConversation(names.NewName);

        // Auto-approve is per-thread; a fresh thread returns to the default (on).
        autoApprove = true;
        await Session.NewThreadAsync();
    }

    private async Task CopyAsync()
    {
        if (resizeModule is null || !HasTranscript)
        {
            return;
        }

        await resizeModule.InvokeVoidAsync("copyTextToClipboard", BuildTranscriptText());
    }

    // Copy a single message's text (the raw text; for an assistant message that is the markdown
    // source). Distinct from CopyAsync, which copies the whole transcript.
    private async Task CopyMessageAsync(string text)
    {
        if (resizeModule is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        await resizeModule.InvokeVoidAsync("copyTextToClipboard", text);
    }

    // Activity is an on-demand modal opened from the composer toolbar (it is no longer a tab):
    // a raw view of the sidecar event stream for the current run, useful when the engine is
    // still evolving. See the activity-tab-fate note.
    private async Task OpenActivityAsync()
    {
        await Dialogs.OpenAsync<ActivityTab>(
            "Activity",
            options: new DialogOptions
            {
                Width = "72vw",
                Height = "72vh",
                Resizable = true,
                Draggable = true,
            });
    }


    private async Task PopOutAsync()
    {
        if (resizeModule is null || !HasTranscript)
        {
            return;
        }

        await resizeModule.InvokeVoidAsync("openHtmlDocument", BuildTranscriptHtml(), "ClaudeWorkbench Chat History");
    }

    private string BuildTranscriptText()
    {
        StringBuilder builder = new();
        foreach (TranscriptEntry entry in Session.Transcript)
        {
            string line = entry.Kind switch
            {
                TranscriptKind.ToolCall => $"-> {entry.Text}",
                TranscriptKind.Image => $"[image: {entry.Text}]",
                _ => entry.Text,
            };
            builder.AppendLine(line);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string BuildTranscriptHtml()
    {
        StringBuilder builder = new();
        foreach (TranscriptEntry entry in Session.Transcript)
        {
            if (entry.Kind == TranscriptKind.ToolCall)
            {
                builder.Append("<p><code>-> ").Append(System.Net.WebUtility.HtmlEncode(entry.Text)).Append("</code></p>");
            }
            else if (entry.Kind == TranscriptKind.Image)
            {
                builder.Append("<p><img style=\"max-width:100%\" src=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(LocalFileUrl(entry.Text)))
                    .Append("\" alt=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(System.IO.Path.GetFileName(entry.Text)))
                    .Append("\" /></p>");
            }
            else
            {
                builder.Append("<section>").Append(MarkdownRenderer.ToHtml(entry.Text)).Append("</section>");
            }
        }

        return builder.ToString();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js?v=6");
        if (firstRender)
        {
            await resizeModule.InvokeVoidAsync(
                "attachAssistantSplitter",
                assistantLayout,
                chatComposer,
                transcriptPanel,
                assistantSplitter);
            await resizeModule.InvokeVoidAsync("attachComposerAutoScroll", chatInput);

            // Paste/drop attachments: capture raw image/text data in the composer and POST it to
            // /uploads/paste, then AddUploadedAttachment marshals the saved path back here.
            selfRef ??= DotNetObjectReference.Create(this);
            attachModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/composerAttach.js");
            await attachModule.InvokeVoidAsync("initComposerAttach", composerPasteZone, chatInput, selfRef);
        }

        // Transcript-wide JS runs ONLY when the transcript changed (streamed/new message via
        // OnChanged), never on plain composer keystrokes. The composer textarea binds oninput, so
        // it re-renders on every character; running scroll-to-bottom + highlight + mermaid per key
        // yanked the view down and re-scanned the whole transcript each keystroke (the "screen
        // resetting" while typing). Gate on transcriptDirty so typing is cheap and stays put.
        if (firstRender || transcriptDirty)
        {
            transcriptDirty = false;
            await resizeModule.InvokeVoidAsync("scrollElementToBottom", transcriptView);
            await resizeModule.InvokeVoidAsync("highlightCodeBlocks", transcriptView);
            await resizeModule.InvokeVoidAsync("addCodeCopyButtons", transcriptView);
            await resizeModule.InvokeVoidAsync("renderMermaidBlocks", transcriptView);
        }
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
        ConversationStore.ConversationCreated -= OnConversationCreated;
        ConversationStore.ConversationsChanged -= OnConversationsChanged;
    }

    public async ValueTask DisposeAsync()
    {
        if (resizeModule is not null)
        {
            try
            {
                await resizeModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        if (attachModule is not null)
        {
            try
            {
                await attachModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        selfRef?.Dispose();
    }
}
