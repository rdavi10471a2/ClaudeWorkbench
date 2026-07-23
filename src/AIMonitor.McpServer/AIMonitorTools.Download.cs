using AIMonitor.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    // One shared client for the occasional gated download; the timeout keeps a hung host from
    // wedging the tool call. Downloads run in the Host process (MCP is mapped there).
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const long MaxDownloadBytes = 25L * 1024 * 1024;

    private static readonly HashSet<string> DownloadImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    [McpServerTool]
    [Description("Download a file (e.g. an image) from an http/https URL into the workspace uploads folder. Returns savedPath and a ready-to-embed `markdown` field. IMPORTANT: to actually SHOW the image to the user you MUST include the returned `markdown` value verbatim in your chat reply — that is the only way it appears in the chat. The operator approves each download at the gate.")]
    public async Task<object> DownloadUrl(
        [Description("The http:// or https:// URL to download.")] string url,
        [Description("Optional file name to save as; a safe name is derived from the URL when omitted.")] string? fileName = null)
    {
        runtimeState.Touch();

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return DownloadError("A valid http:// or https:// URL is required.", url);
        }

        try
        {
            // Land it in the workspace uploads folder, which the host already serves over
            // /local-file (so the image renders when the markdown below is embedded).
            string uploadsDir = Path.Combine(MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings), "uploads");
            Directory.CreateDirectory(uploadsDir);

            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            // Many image hosts 403 a request with no User-Agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeWorkbench", "1.0"));
            using HttpResponseMessage response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return DownloadError($"The host {uri.Host} returned {(int)response.StatusCode} {response.ReasonPhrase}. Try a different direct file URL.", url);
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxDownloadBytes)
            {
                return DownloadError($"Download exceeds the {MaxDownloadBytes / (1024 * 1024)} MB limit.", url);
            }

            string name = DownloadFileName(uri, fileName, response.Content.Headers.ContentType?.MediaType);
            string target = UniqueDownloadPath(uploadsDir, name);

            long written;
            {
                await using FileStream file = File.Create(target);
                await using Stream source = await response.Content.ReadAsStreamAsync();
                written = await CopyWithLimitAsync(source, file, MaxDownloadBytes);
            }

            if (written == 0)
            {
                TryDeleteFile(target);
                return DownloadError("The download was empty (no bytes).", url);
            }

            // Forward slashes: a raw C:\ path gets mangled inside a markdown link, so the display
            // path (and the ready markdown) use '/', which the renderer + /local-file both accept.
            string displayPath = target.Replace('\\', '/');
            string savedName = Path.GetFileName(target);
            bool isImage = DownloadImageExtensions.Contains(Path.GetExtension(target));
            string markdown = isImage
                ? $"![{savedName}]({displayPath})"
                : $"[{savedName}]({displayPath})";

            return new AIMonitorDownloadResult(
                displayPath,
                savedName,
                written,
                response.Content.Headers.ContentType?.ToString(),
                isImage,
                markdown);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return DownloadError($"Download failed: {ex.Message}", url);
        }
    }

    private static AIMonitorToolErrorResult DownloadError(string message, string url)
        => new(true, message, "Provide a direct http(s) URL to a file (e.g. one ending in .jpg or .png).", url);

    // A safe file name: the requested name or the URL's last segment, sanitized; when it has no
    // extension, borrow one from the content type (image/png -> .png) so images render.
    private static string DownloadFileName(Uri uri, string? requested, string? mediaType)
    {
        string candidate = !string.IsNullOrWhiteSpace(requested)
            ? Path.GetFileName(requested)
            : Path.GetFileName(uri.LocalPath);
        candidate = Sanitize(string.IsNullOrWhiteSpace(candidate) ? "download" : candidate);

        if (!Path.HasExtension(candidate) && !string.IsNullOrWhiteSpace(mediaType))
        {
            candidate += mediaType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/bmp" => ".bmp",
                "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
                "image/avif" => ".avif",
                _ => string.Empty,
            };
        }

        return candidate;
    }

    private static string UniqueDownloadPath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; ; index++)
        {
            string next = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, Stream destination, long maxBytes)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new IOException($"Download exceeds the {maxBytes / (1024 * 1024)} MB limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read));
        }

        return total;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort cleanup of a failed/empty download.
        }
    }
}

// Result of download_url: where the file landed, what it is, and ready-to-embed markdown that
// (for an image) renders inline when the agent includes it verbatim in its reply.
public sealed record AIMonitorDownloadResult(
    string SavedPath,
    string FileName,
    long Bytes,
    string? ContentType,
    bool IsImage,
    string Markdown);
