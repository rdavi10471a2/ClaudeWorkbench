using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClaudeWorkbench.Host.Services;

// Assistant text arrives as Markdown; render it to HTML for the transcript body.
// One shared pipeline (advanced extensions, matching the original chat history).
//
// Local-file handling: the browser cannot load file:// URIs (or bare C:\ paths) from a
// page served over http://localhost -- links are blocked and <img> tags show the broken
// glyph. So after parsing we walk the AST and rewrite any link/image URL that points at a
// local file to the /local-file endpoint (see LocalFileEndpoints), which streams it -- and
// only from the uploads/ folder, so a rewritten path outside there resolves to a 403.
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // Rooted Windows path ("C:\..." or "C:/...") or UNC ("\\server\share\...").
    private static readonly Regex LocalPathPattern = new(
        @"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        RewriteLocalLinks(document);

        using StringWriter writer = new();
        HtmlRenderer renderer = new(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static void RewriteLocalLinks(MarkdownDocument document)
    {
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            string? rewritten = TryRewrite(link.Url);
            if (rewritten is null)
            {
                continue;
            }

            link.Url = rewritten;

            // Local non-image links open in a new tab; navigating the app tab away would
            // drop the Blazor circuit.
            if (!link.IsImage)
            {
                HtmlAttributes attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener");
            }
        }
    }

    // Returns the rewritten URL for local-file references, or null to leave the URL as-is.
    private static string? TryRewrite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string? localPath = null;

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                localPath = uri.LocalPath;
            }
        }
        else if (LocalPathPattern.IsMatch(url))
        {
            localPath = url;
        }

        return localPath is null
            ? null
            : "/local-file?path=" + Uri.EscapeDataString(localPath);
    }
}
