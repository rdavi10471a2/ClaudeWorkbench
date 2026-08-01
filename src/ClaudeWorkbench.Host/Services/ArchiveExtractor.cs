using System.Formats.Tar;
using System.IO.Compression;

namespace ClaudeWorkbench.Host.Services;

// Extracts an operator-uploaded archive into a fresh folder under uploads/, so the agent can be handed
// ONE folder path (already agent-readable via additionalDirectories) and explore it with Glob/Grep/Read
// on demand — rather than the operator attaching hundreds of files or Claude trying (and failing) to
// Read a binary archive. See docs/plans/archive-upload-extraction.md.
//
// In-box only: .zip (System.IO.Compression), .tar / .tar.gz / .tgz (System.Formats.Tar), and a bare
// .gz single file (GZipStream). .7z/.rar would need a third-party library and are not accepted.
//
// Two guards, always on: ZIP-SLIP (an entry that resolves outside the target folder is refused) and
// ZIP-BOMB (total uncompressed bytes and entry count are capped; the partial folder is removed on trip).
public static class ArchiveExtractor
{
    // Uncompressed ceilings. Generous enough for a real zipped codebase, low enough to stop a bomb.
    public const long MaxTotalUncompressedBytes = 500L * 1024 * 1024;
    public const int MaxEntries = 5000;

    public sealed record Result(bool Ok, string? FolderPath, int FileCount, IReadOnlyList<string> RelativeFiles, string? Error)
    {
        public static Result Fail(string error) => new(false, null, 0, [], error);
    }

    // True when the extension is one this extractor handles (matches the upload allowlist archives).
    public static bool IsSupportedArchive(string path)
    {
        string name = path.ToLowerInvariant();
        return name.EndsWith(".zip", StringComparison.Ordinal)
            || name.EndsWith(".tar", StringComparison.Ordinal)
            || name.EndsWith(".tar.gz", StringComparison.Ordinal)
            || name.EndsWith(".tgz", StringComparison.Ordinal)
            || name.EndsWith(".gz", StringComparison.Ordinal);
    }

    // Extract archivePath into a NEW unique folder beside it in destinationParent (uploads/). Never
    // overwrites an existing folder; never throws — failures come back as Result.Fail. On any guard
    // trip or error the partial folder is deleted.
    public static Result Extract(string archivePath, string destinationParent)
    {
        if (!File.Exists(archivePath))
        {
            return Result.Fail("Archive not found.");
        }

        if (!IsSupportedArchive(archivePath))
        {
            return Result.Fail("Unsupported archive type.");
        }

        string folder = CreateUniqueFolder(destinationParent, ArchiveStem(archivePath));

        try
        {
            List<string> files = [];
            long totalBytes = 0;

            string lower = archivePath.ToLowerInvariant();
            if (lower.EndsWith(".zip", StringComparison.Ordinal))
            {
                ExtractZip(archivePath, folder, files, ref totalBytes);
            }
            else if (lower.EndsWith(".tar", StringComparison.Ordinal))
            {
                using FileStream tar = File.OpenRead(archivePath);
                ExtractTar(tar, folder, files, ref totalBytes);
            }
            else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            {
                using FileStream raw = File.OpenRead(archivePath);
                using GZipStream gz = new(raw, CompressionMode.Decompress);
                ExtractTar(gz, folder, files, ref totalBytes);
            }
            else // bare .gz — a single compressed file
            {
                ExtractGzipSingle(archivePath, folder, files, ref totalBytes);
            }

            return new Result(true, folder, files.Count, files, null);
        }
        catch (ArchiveGuardException guard)
        {
            TryDeleteFolder(folder);
            return Result.Fail(guard.Message);
        }
        catch (InvalidDataException)
        {
            TryDeleteFolder(folder);
            return Result.Fail("The archive is corrupt or not a valid archive.");
        }
        catch (IOException exception)
        {
            TryDeleteFolder(folder);
            return Result.Fail(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDeleteFolder(folder);
            return Result.Fail(exception.Message);
        }
    }

    private static void ExtractZip(string archivePath, string root, List<string> files, ref long totalBytes)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            // Directory entries have an empty Name (FullName ends in '/'); create and move on.
            if (entry.Name.Length == 0)
            {
                continue;
            }

            string destination = SafeDestination(root, entry.FullName);
            EnforceEntryCount(files.Count);
            EnforceTotalBytes(totalBytes + entry.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (Stream source = entry.Open())
            using (FileStream target = File.Create(destination))
            {
                totalBytes += CopyCapped(source, target, totalBytes);
            }

            files.Add(RelativePath(root, destination));
        }
    }

    private static void ExtractTar(Stream tarStream, string root, List<string> files, ref long totalBytes)
    {
        using TarReader reader = new(tarStream);
        while (reader.GetNextEntry() is TarEntry entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue; // directories, links, devices — skip; regular files carry the content
            }

            string destination = SafeDestination(root, entry.Name);
            EnforceEntryCount(files.Count);
            EnforceTotalBytes(totalBytes + Math.Max(0, entry.Length));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (entry.DataStream is Stream data)
            {
                using FileStream target = File.Create(destination);
                totalBytes += CopyCapped(data, target, totalBytes);
            }
            else
            {
                File.Create(destination).Dispose();
            }

            files.Add(RelativePath(root, destination));
        }
    }

    private static void ExtractGzipSingle(string archivePath, string root, List<string> files, ref long totalBytes)
    {
        // A bare .gz holds one file; name it after the archive minus the .gz suffix.
        string inner = Path.GetFileNameWithoutExtension(archivePath);
        if (inner.Length == 0)
        {
            inner = "extracted";
        }

        string destination = SafeDestination(root, inner);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using FileStream raw = File.OpenRead(archivePath);
        using GZipStream gz = new(raw, CompressionMode.Decompress);
        using FileStream target = File.Create(destination);
        totalBytes += CopyCapped(gz, target, totalBytes);
        files.Add(RelativePath(root, destination));
    }

    // Stream copy that trips the bomb guard mid-flight (covers .gz where the uncompressed size is not
    // known up front). Returns bytes written.
    private static long CopyCapped(Stream source, Stream target, long alreadyWritten)
    {
        byte[] buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            EnforceTotalBytes(alreadyWritten + written);
            target.Write(buffer, 0, read);
        }

        return written;
    }

    // Resolve an entry's path under root and REFUSE anything that escapes it (zip-slip): '..', absolute
    // paths, or drive-relative paths all resolve outside and are rejected.
    private static string SafeDestination(string root, string entryName)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string combined = Path.GetFullPath(Path.Combine(normalizedRoot, entryName.Replace('\\', '/')));
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveGuardException($"Refused an archive entry that escapes the target folder: '{entryName}'.");
        }

        return combined;
    }

    private static void EnforceEntryCount(int current)
    {
        if (current + 1 > MaxEntries)
        {
            throw new ArchiveGuardException($"Archive exceeds the {MaxEntries}-file limit.");
        }
    }

    private static void EnforceTotalBytes(long total)
    {
        if (total > MaxTotalUncompressedBytes)
        {
            throw new ArchiveGuardException($"Archive exceeds the {MaxTotalUncompressedBytes / (1024 * 1024)} MB uncompressed limit.");
        }
    }

    // "foo.zip" -> "foo", "foo.tar.gz"/"foo.tgz" -> "foo", "foo.tar" -> "foo", "foo.gz" -> "foo".
    private static string ArchiveStem(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        string lower = name.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return name[..^7];
        }

        string stem = Path.GetFileNameWithoutExtension(name); // strips one extension (.zip/.tgz/.tar/.gz)
        return string.IsNullOrWhiteSpace(stem) ? "archive" : stem;
    }

    private static string CreateUniqueFolder(string parent, string stem)
    {
        Directory.CreateDirectory(parent);
        string candidate = Path.Combine(parent, stem);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        for (int index = 2; ; index++)
        {
            string next = Path.Combine(parent, $"{stem} ({index})");
            if (!Directory.Exists(next) && !File.Exists(next))
            {
                Directory.CreateDirectory(next);
                return next;
            }
        }
    }

    private static string RelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ArchiveGuardException : Exception
    {
        public ArchiveGuardException(string message) : base(message)
        {
        }
    }
}
