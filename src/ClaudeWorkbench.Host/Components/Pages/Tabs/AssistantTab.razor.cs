using System.Text;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

public partial class AssistantTab : IDisposable, IAsyncDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private UploadService Uploads { get; set; } = default!;

    private ElementReference assistantLayout;
    private ElementReference chatComposer;
    private ElementReference assistantSplitter;
    private ElementReference transcriptPanel;
    private ElementReference transcriptView;
    private ElementReference chatInput;
    private IJSObjectReference? resizeModule;
    private string draft = string.Empty;
    private bool autoApprove;
    private bool usageOpen;
    private bool wasWorking;
    private UsageSnapshot? usage;
    private readonly List<PendingAttachment> attachments = new();
    private bool uploading;
    private string? uploadError;

    private sealed record PendingAttachment(string Name, string Path);

    private bool Working => Session.Status.Working;

    private bool HasTranscript => Session.Transcript.Count > 0;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
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

    private async Task OnFilesSelectedAsync(InputFileChangeEventArgs args)
    {
        uploadError = null;
        uploading = true;
        StateHasChanged();
        try
        {
            foreach (IBrowserFile file in args.GetMultipleFiles(maximumFileCount: 20))
            {
                using (Stream stream = file.OpenReadStream(maxAllowedSize: 50L * 1024 * 1024))
                {
                    string saved = await Uploads.SaveAsync(file.Name, stream, CancellationToken.None);
                    attachments.Add(new PendingAttachment(file.Name, saved));
                }
            }
        }
        catch (Exception ex)
        {
            uploadError = ex.Message;
        }
        finally
        {
            uploading = false;
            StateHasChanged();
        }
    }

    private void RemoveAttachment(PendingAttachment attachment)
    {
        attachments.Remove(attachment);
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

        builder.Append("[Operator attached ").Append(attachments.Count)
            .Append(attachments.Count == 1 ? " file" : " files")
            .Append(" — read each with the Read tool:]\n");
        foreach (PendingAttachment attachment in attachments)
        {
            builder.Append("- ").Append(attachment.Path).Append('\n');
        }

        return builder.ToString();
    }

    private async Task StopAsync()
    {
        await Session.StopAsync();
    }

    private async Task NewThreadAsync()
    {
        if (Working)
        {
            return;
        }

        // Auto-approve is per-thread; a fresh thread starts back at the gate.
        autoApprove = false;
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
            builder.AppendLine(entry.Kind == TranscriptKind.ToolCall ? $"-> {entry.Text}" : entry.Text);
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
            else
            {
                builder.Append("<section>").Append(MarkdownRenderer.ToHtml(entry.Text)).Append("</section>");
            }
        }

        return builder.ToString();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        resizeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/sourceResize.js");
        if (firstRender)
        {
            await resizeModule.InvokeVoidAsync(
                "attachAssistantSplitter",
                assistantLayout,
                chatComposer,
                transcriptPanel,
                assistantSplitter);
            await resizeModule.InvokeVoidAsync("attachComposerAutoScroll", chatInput);
        }

        await resizeModule.InvokeVoidAsync("scrollElementToBottom", transcriptView);
    }

    public void Dispose()
    {
        Session.Changed -= OnChanged;
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
    }
}
