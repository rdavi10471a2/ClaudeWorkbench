using Microsoft.AspNetCore.StaticFiles;

namespace ClaudeWorkbench.Host.Services;

// Serves upload-folder files referenced in chat markdown so the browser can load them: a
// page served over http://localhost cannot open file:// URIs (or bare C:\ paths), so
// MarkdownRenderer rewrites local references to /local-file?path=... and this endpoint
// streams the bytes.
//
// Security: a file is served only if it is under the workspace uploads/ folder OR one the
// agent read/wrote this thread (AgentFileAccess -- each such path was surfaced to, and for
// writes gated by, the operator). Anything else is 403, so this can never become an
// arbitrary-file-read hole.
public static class LocalFileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private const long MaxUploadBytes = 25L * 1024 * 1024;

    private static readonly HashSet<string> UploadImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif", ".svg",
    };

    // Images plus the common text/data types an operator pastes or drops. Anything else is refused
    // so the paste zone can't be used to smuggle arbitrary files into the workspace uploads folder.
    private static readonly HashSet<string> AllowedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif", ".svg",
        ".txt", ".log", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml",
    };

    public static void MapLocalFiles(this WebApplication app)
    {
        app.MapGet("/local-file", (string path, UploadService uploads, AgentFileAccess fileAccess) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest();
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return Results.BadRequest();
            }

            string? root = uploads.UploadsDirectory;
            bool underUploads = root is not null && IsUnderRoot(full, root);

            // Serve only files under uploads/ or ones the agent read/wrote this thread; any
            // other path (including system files) is refused.
            if (!underUploads && !fileAccess.Contains(full))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(full))
            {
                return Results.NotFound();
            }

            // A file authorized only by the uploads prefix must still RESOLVE inside uploads: a
            // symlink/junction planted in uploads/ would otherwise smuggle an out-of-tree file
            // past the prefix check (Path.GetFullPath normalizes '..' but does not follow links).
            // Files authorized via AgentFileAccess are intentionally allowed to live outside
            // uploads, so they are exempt from this recheck.
            if (underUploads && !IsUnderRoot(ResolveRealPath(full), root!))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!ContentTypes.TryGetContentType(full, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(full, contentType, enableRangeProcessing: true);
        });

        // Pasted/dropped raw image or text data from the composer. The client POSTs the blob body
        // with ?name=; we land it in the SAME uploads/ folder the file picker and download_url use
        // (already agent-readable via additionalDirectories and served by /local-file above), then
        // return the saved path so the composer adds it as a normal attachment. DisableAntiforgery:
        // this is a localhost same-origin fetch of a raw body (no form), and the earlier /local-file
        // GET is likewise tokenless; the extension allowlist + size cap are the real guards.
        app.MapPost("/uploads/paste", async (HttpRequest request, UploadService uploads, CancellationToken cancellationToken) =>
        {
            if (!uploads.Available)
            {
                return Results.BadRequest(new { error = "No watched workspace is selected." });
            }

            if (request.ContentLength is long length && length > MaxUploadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            string fileName = BuildUploadFileName(request.Query["name"].ToString(), request.ContentType);
            if (!AllowedUploadExtensions.Contains(Path.GetExtension(fileName)))
            {
                return Results.BadRequest(new { error = "Unsupported attachment type." });
            }

            try
            {
                string saved = await uploads.SaveAsync(fileName, request.Body, cancellationToken);
                return Results.Ok(new
                {
                    name = Path.GetFileName(saved),
                    path = saved,
                    isImage = UploadImageExtensions.Contains(Path.GetExtension(saved)),
                });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();
    }

    // The client sends the pasted/dropped name in ?name= (file.name for a drop, a synthesized
    // "pasted-..." for a clipboard blob). Fall back to a name derived from the Content-Type if it
    // is ever missing. UploadService sanitizes and de-duplicates the final name.
    private static string BuildUploadFileName(string? requestedName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName;
        }

        string extension = (contentType?.Split(';')[0].Trim().ToLowerInvariant()) switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "image/avif" => ".avif",
            "text/markdown" => ".md",
            "text/csv" => ".csv",
            "application/json" => ".json",
            _ => ".txt",
        };
        return $"pasted-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}";
    }

    // GetFullPath has already resolved any '..' in the request, so a prefix check against
    // the canonicalized uploads root is enough to keep serving inside it.
    private static bool IsUnderRoot(string fullPath, string root)
    {
        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception)
        {
            return false;
        }

        return string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Resolve symlinks/junctions to the real on-disk target so a link inside uploads/ cannot point
    // out of the tree. Covers the file itself being a link and its immediate parent being a junction
    // (the practical layouts: uploads/link.png and uploads/<junction>/file). Falls back to the
    // original path when nothing is a reparse point or resolution fails.
    private static string ResolveRealPath(string fullPath)
    {
        try
        {
            if (File.ResolveLinkTarget(fullPath, returnFinalTarget: true) is FileSystemInfo fileTarget)
            {
                return Path.GetFullPath(fileTarget.FullName);
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is not null
                && Directory.ResolveLinkTarget(directory, returnFinalTarget: true) is FileSystemInfo directoryTarget)
            {
                return Path.GetFullPath(Path.Combine(directoryTarget.FullName, Path.GetFileName(fullPath)));
            }

            return fullPath;
        }
        catch (Exception)
        {
            return fullPath;
        }
    }
}
