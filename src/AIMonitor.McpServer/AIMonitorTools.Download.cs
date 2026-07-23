using AIMonitor.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    // One shared client for the occasional gated download; the timeout keeps a hung host from
    // wedging the tool call. Downloads run in the Host process (MCP is mapped there).
    //
    // AllowAutoRedirect is OFF on purpose: we follow redirects by hand (SendFollowingSafeRedirectsAsync)
    // so EVERY hop is re-checked against the SSRF guard. A default HttpClient would silently chase a
    // 302 from an operator-approved URL into a loopback/link-local/metadata address the operator never
    // saw at the gate.
    private static readonly HttpClient DownloadHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private const int MaxDownloadRedirects = 5;

    private const long MaxDownloadBytes = 25L * 1024 * 1024;

    private static readonly HashSet<string> DownloadImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    [McpServerTool]
    [Description("Download a file (e.g. an image) from an http/https URL into the workspace uploads folder. Returns `savedPath` and a ready-to-embed `markdown` field. TO SHOW THE IMAGE: copy the returned `markdown` value into your reply VERBATIM — do NOT retype the path, do NOT re-encode it (no %20), do NOT wrap it, do NOT 'fix' the spaces. The returned path is already clean and URL-safe; any hand-built ![alt](path) will break the inline render. Use `markdown` exactly as returned — that is the ONLY reliable way the image appears in the chat. The operator approves each download at the gate.")]
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

            using HttpResponseMessage response = await SendFollowingSafeRedirectsAsync(uri);
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

    // Fetch the URL, following redirects manually so the SSRF guard runs on the initial target AND
    // every redirect hop. Blocks (loopback/private target, too many hops, non-http redirect) throw
    // HttpRequestException, which DownloadUrl's catch turns into a clean tool error.
    private static async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(Uri uri)
    {
        Uri current = uri;
        for (int hop = 0; ; hop++)
        {
            await GuardAgainstSsrfAsync(current);

            using HttpRequestMessage request = new(HttpMethod.Get, current);
            // Many image hosts 403 a request with no User-Agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeWorkbench", "1.0"));
            HttpResponseMessage response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return response;
            }

            Uri next = new(current, response.Headers.Location); // resolve relative Location against current
            response.Dispose();

            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException($"Refusing to follow a redirect to a non-http(s) target ({next.Scheme}).");
            }

            if (hop >= MaxDownloadRedirects)
            {
                throw new HttpRequestException($"Too many redirects (more than {MaxDownloadRedirects}).");
            }

            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    // SSRF guard: resolve the host and refuse if ANY resolved address is loopback, link-local,
    // private, or otherwise internal. Rejecting when any address is blocked (not just the first)
    // closes the DNS-rebinding gap where a host advertises one public and one internal address.
    private static async Task GuardAgainstSsrfAsync(Uri uri)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host);
            }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Could not resolve host {uri.Host}: {ex.Message}");
            }
        }

        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new HttpRequestException(
                $"Refusing to fetch {uri.Host}: it resolves to a loopback, link-local, or private address (SSRF guard).");
        }
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 0                                        // 0.0.0.0/8   this-network
                || b[0] == 10                                       // 10.0.0.0/8  private
                || b[0] == 127                                      // 127.0.0.0/8 loopback
                || (b[0] == 169 && b[1] == 254)                     // 169.254/16  link-local (incl. cloud metadata)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)        // 172.16/12   private
                || (b[0] == 192 && b[1] == 168)                     // 192.168/16  private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);      // 100.64/10   CGNAT
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback);
        }

        return true; // unknown family -> refuse
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
        candidate = SlugifyDownloadName(string.IsNullOrWhiteSpace(candidate) ? "download" : candidate);

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

    // Downloads land in a markdown ![](path) link, so the file name must be URL-safe: spaces,
    // parens, brackets and other link-breaking punctuation get collapsed to '-'. (Sanitize only
    // removes filesystem-invalid chars, which lets spaces/parens through — exactly what mangles
    // the embedded link. This is why "conan-best-in-life (2).gif" would not render.)
    private static string SlugifyDownloadName(string value)
    {
        string cleaned = Sanitize(value);
        string stem = Path.GetFileNameWithoutExtension(cleaned);
        string extension = Path.GetExtension(cleaned);

        char[] slug = stem.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-').ToArray();
        string joined = new string(slug);
        while (joined.Contains("--"))
        {
            joined = joined.Replace("--", "-");
        }

        joined = joined.Trim('-', '.');
        if (string.IsNullOrWhiteSpace(joined))
        {
            joined = "download";
        }

        return joined + extension;
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
            // '-2' not ' (2)': the name goes into a markdown link, so keep it space/paren-free.
            string next = Path.Combine(directory, $"{stem}-{index}{extension}");
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
