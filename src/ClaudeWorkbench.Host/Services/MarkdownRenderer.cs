using Markdig;

namespace ClaudeWorkbench.Host.Services;

// Assistant text arrives as Markdown; render it to HTML for the transcript body.
// One shared pipeline (advanced extensions, matching the original chat history).
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string ToHtml(string? markdown)
    {
        return string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : Markdown.ToHtml(markdown, Pipeline);
    }
}
