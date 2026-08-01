using System.Formats.Tar;
using System.IO.Compression;
using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Tests;

// ArchiveExtractor unzips operator uploads into a fresh uploads/ folder. These tests cover the happy
// path (zip + tar.gz), the two security guards (zip-slip, zip-bomb via entry count), and the
// unique-folder collision rule. Everything runs in a throwaway temp dir.
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly List<string> tempRoots = new();

    private string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempRoots.Add(dir);
        return dir;
    }

    private static string MakeZip(string dir, string name, Action<ZipArchive> build)
    {
        string path = Path.Combine(dir, name);
        using FileStream fs = File.Create(path);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create);
        build(archive);
        return path;
    }

    private static void AddEntry(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public void Extracts_a_zip_into_a_named_folder_with_manifest()
    {
        string root = NewRoot();
        string zip = MakeZip(root, "proj.zip", a =>
        {
            AddEntry(a, "readme.md", "hello");
            AddEntry(a, "src/app.cs", "class C {}");
        });

        ArchiveExtractor.Result result = ArchiveExtractor.Extract(zip, root);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("proj", Path.GetFileName(result.FolderPath));
        Assert.Equal(2, result.FileCount);
        Assert.Contains("readme.md", result.RelativeFiles);
        Assert.Contains("src/app.cs", result.RelativeFiles);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(result.FolderPath!, "readme.md")));
    }

    [Fact]
    public void Collision_yields_a_new_unique_folder()
    {
        string root = NewRoot();
        string zip = MakeZip(root, "dup.zip", a => AddEntry(a, "a.txt", "1"));

        ArchiveExtractor.Result first = ArchiveExtractor.Extract(zip, root);
        // Re-make the archive (the first extraction consumes nothing, but the caller normally deletes it).
        string zip2 = MakeZip(root, "dup.zip", a => AddEntry(a, "a.txt", "2"));
        ArchiveExtractor.Result second = ArchiveExtractor.Extract(zip2, root);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.FolderPath, second.FolderPath);
        Assert.Equal("dup", Path.GetFileName(first.FolderPath));
        Assert.Equal("dup (2)", Path.GetFileName(second.FolderPath));
    }

    [Fact]
    public void Rejects_zip_slip_entries_and_removes_the_partial_folder()
    {
        string root = NewRoot();
        string zip = MakeZip(root, "evil.zip", a => AddEntry(a, "../escape.txt", "pwned"));

        ArchiveExtractor.Result result = ArchiveExtractor.Extract(zip, root);

        Assert.False(result.Ok);
        Assert.Contains("escape", result.Error);
        // The escaped file must NOT have been written outside the target folder.
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        // The partial extraction folder is cleaned up.
        Assert.False(Directory.Exists(Path.Combine(root, "evil")));
    }

    [Fact]
    public void Enforces_the_entry_count_cap()
    {
        string root = NewRoot();
        string zip = MakeZip(root, "many.zip", a =>
        {
            for (int i = 0; i <= ArchiveExtractor.MaxEntries; i++)
            {
                AddEntry(a, $"f{i}.txt", "x");
            }
        });

        ArchiveExtractor.Result result = ArchiveExtractor.Extract(zip, root);

        Assert.False(result.Ok);
        Assert.Contains("file limit", result.Error);
        Assert.False(Directory.Exists(Path.Combine(root, "many")));
    }

    [Fact]
    public void Extracts_a_tar_gz()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "payload");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "note.txt"), "tarred");

        string tgz = Path.Combine(root, "bundle.tar.gz");
        using (FileStream fs = File.Create(tgz))
        using (GZipStream gz = new(fs, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(source, gz, includeBaseDirectory: false);
        }

        ArchiveExtractor.Result result = ArchiveExtractor.Extract(tgz, root);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("bundle", Path.GetFileName(result.FolderPath));
        Assert.Contains("note.txt", result.RelativeFiles);
        Assert.Equal("tarred", File.ReadAllText(Path.Combine(result.FolderPath!, "note.txt")));
    }

    [Fact]
    public void Unsupported_type_is_refused()
    {
        string root = NewRoot();
        string path = Path.Combine(root, "photo.png");
        File.WriteAllText(path, "not really a png");

        ArchiveExtractor.Result result = ArchiveExtractor.Extract(path, root);

        Assert.False(result.Ok);
        Assert.Contains("Unsupported", result.Error);
    }

    public void Dispose()
    {
        foreach (string dir in tempRoots)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
