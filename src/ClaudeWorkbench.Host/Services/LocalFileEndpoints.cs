using Microsoft.AspNetCore.StaticFiles;

namespace ClaudeWorkbench.Host.Services;

// Serves upload-folder files referenced in chat markdown so the browser can load them: a
// page served over http://localhost cannot open file:// URIs (or bare C:\ paths), so
// MarkdownRenderer rewrites local references to /local-file?path=... and this endpoint
// streams the bytes.
//
// Security: ONLY files under the workspace uploads/ folder are served; everything else is
// 403. That folder is where operator attachments (and any agent-produced images) live, so
// it is the whole surface we need -- keeping it to that single root is what stops this from
// being an arbitrary-file-read hole.
public static class LocalFileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapLocalFiles(this WebApplication app)
    {
        app.MapGet("/local-file", (string path, UploadService uploads) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest();
            }

            string? root = uploads.UploadsDirectory;
            if (root is null)
            {
                // No watched workspace -> no uploads folder -> nothing is servable.
                return Results.StatusCode(StatusCodes.Status403Forbidden);
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

            if (!IsUnderRoot(full, root))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(full))
            {
                return Results.NotFound();
            }

            if (!ContentTypes.TryGetContentType(full, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(full, contentType, enableRangeProcessing: true);
        });
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
}
