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
// Local-file handling: the browser cannot load file:// URIs (or bare C:\ paths) from a page
// served over http://localhost -- links are blocked and <img> tags show the broken glyph. So
// after parsing we walk the AST and rewrite any link/image URL that points at a local file to
// the /local-file endpoint (see LocalFileEndpoints), which streams it. A plain LINK to a local
// image is upgraded to an inline image, since agents commonly write [name](path) instead of
// ![name](path); links that stay links (local non-image OR external) open in a new tab.
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // Model output is UNTRUSTED (it launders file/web content via indirect prompt injection)
        // and we inject it as HTML, so raw HTML the model types is escaped, not rendered — no
        // <script>, <iframe>, or <img onerror>. Markdown images still render as <img> (that path
        // is not "raw HTML"); external image sources are neutralized in RewriteLocalLinks below.
        .DisableHtml()
        .Build();

    // Rooted Windows path ("C:\..." or "C:/...") or UNC ("\\server\share\...").
    private static readonly Regex LocalPathPattern = new(
        @"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

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
            string? localPath = ResolveLocalPath(link.Url);
            if (localPath is not null)
            {
                link.Url = "/local-file?path=" + Uri.EscapeDataString(localPath);

                // A link that points at a local IMAGE renders inline, even when the agent
                // wrote [name](path) rather than ![name](path).
                if (ImageExtensions.Contains(Path.GetExtension(localPath)))
                {
                    link.IsImage = true;
                }
            }
            else if (link.IsImage && IsExternalHttp(link.Url))
            {
                // An EXTERNAL image auto-fetches on render — a tracking/exfil pixel if the model was
                // steered by untrusted input it read. Downgrade it to a click-through link so nothing
                // loads until the operator chooses; download_url is the way to actually show a web
                // image (it lands local, then renders inline and safely).
                link.IsImage = false;
            }

            // Anything that stays a link (local non-image, or an external URL) opens in a new
            // tab; navigating the app tab away would DROP THE BLAZOR CIRCUIT (looks like a crash).
            if (!link.IsImage && (localPath is not null || IsExternalHttp(link.Url)))
            {
                HtmlAttributes attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener");
            }
        }
    }

    // The local filesystem path a URL refers to (file:// URI, rooted Windows path, or UNC),
    // or null when the URL is not a local-file reference.
    private static string? ResolveLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.LocalPath : null;
        }

        return LocalPathPattern.IsMatch(url) ? url : null;
    }

    private static bool IsExternalHttp(string? url)
    {
        return url is not null
            && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
