using System.Text;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ClaudeWorkbench.Host.Components.Pages.Tabs;

public partial class AssistantTab : IDisposable, IAsyncDisposable
{
    [Inject]
    private IOperatorConsole Session { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private ElementReference assistantLayout;
    private ElementReference chatComposer;
    private ElementReference assistantSplitter;
    private ElementReference transcriptPanel;
    private ElementReference transcriptView;
    private ElementReference chatInput;
    private IJSObjectReference? resizeModule;
    private string draft = string.Empty;
    private bool autoApprove;

    private bool Working => Session.Status.Working;

    private bool HasTranscript => Session.Transcript.Count > 0;

    protected override void OnInitialized()
    {
        Session.Changed += OnChanged;
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private static MarkupString RenderMarkdown(string text)
    {
        return new MarkupString(MarkdownRenderer.ToHtml(text));
    }

    private async Task SubmitAsync()
    {
        if (Working || string.IsNullOrWhiteSpace(draft))
        {
            return;
        }

        string prompt = draft;
        draft = string.Empty;
        await Session.SendAsync(prompt, autoApprove);
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
                "attachSourceSplitter",
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
