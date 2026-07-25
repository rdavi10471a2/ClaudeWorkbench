using ClaudeWorkbench.Host.Threads;

namespace ClaudeWorkbench.Host.Tests;

// The transcript store locates and hard-deletes the SDK's ~/.claude JSONL files. These tests use a
// throwaway "projects root" so nothing touches the real ~/.claude. Lookup is encoding-agnostic: it
// scans project folders for <sessionId>.jsonl, so it must find the file regardless of the folder name.
public sealed class ClaudeTranscriptStoreTests : IDisposable
{
    private readonly List<string> tempDirs = new();

    private string NewProjectsRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-transcripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    private static string WriteTranscript(string projectsRoot, string encodedCwd, string sessionId, string content = "{}")
    {
        string projectDir = Path.Combine(projectsRoot, encodedCwd);
        Directory.CreateDirectory(projectDir);
        string path = Path.Combine(projectDir, sessionId + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Locate_finds_the_transcript_regardless_of_project_folder_name()
    {
        string root = NewProjectsRoot();
        string path = WriteTranscript(root, "c--Some-Weird-Encoded-Cwd", "sess-1");
        ClaudeTranscriptStore store = new(root);

        IReadOnlyList<string> found = store.Locate("sess-1");

        Assert.Single(found);
        Assert.Equal(path, found[0], ignoreCase: true);
    }

    [Fact]
    public void Delete_transcripts_removes_the_file_and_reports_the_count()
    {
        string root = NewProjectsRoot();
        string path = WriteTranscript(root, "proj", "sess-1");
        ClaudeTranscriptStore store = new(root);

        int deleted = store.DeleteTranscripts("sess-1");

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Mirror_to_copies_the_transcript_into_the_destination_as_session_jsonl()
    {
        string root = NewProjectsRoot();
        WriteTranscript(root, "proj", "sess-1", "{\"m\":1}");
        ClaudeTranscriptStore store = new(root);
        string dest = Path.Combine(Path.GetTempPath(), "cwb-mirror-" + Guid.NewGuid().ToString("N"));
        tempDirs.Add(dest);

        string? mirror = store.MirrorTo("sess-1", dest);

        Assert.NotNull(mirror);
        Assert.Equal(Path.Combine(dest, "sess-1.jsonl"), mirror);
        Assert.Equal("{\"m\":1}", File.ReadAllText(mirror!));
    }

    [Fact]
    public void Mirror_to_returns_null_when_there_is_no_transcript()
    {
        string root = NewProjectsRoot();
        ClaudeTranscriptStore store = new(root);
        Assert.Null(store.MirrorTo("missing", Path.Combine(Path.GetTempPath(), "cwb-mirror-none")));
    }

    [Fact]
    public void Restore_from_mirror_overwrites_the_existing_primary_in_place()
    {
        string root = NewProjectsRoot();
        string primary = WriteTranscript(root, "some-encoded-cwd", "sess-1", "OLD");
        ClaudeTranscriptStore store = new(root);
        string mirror = NewMirrorFile("sess-1", "NEW");

        Assert.True(store.RestoreFromMirror("sess-1", @"C:\whatever", mirror));
        Assert.Equal("NEW", File.ReadAllText(primary)); // same path Locate found — overwritten in place
    }

    [Fact]
    public void Restore_from_mirror_recreates_the_primary_under_the_encoded_cwd_when_gone()
    {
        string root = NewProjectsRoot(); // no primary at all
        ClaudeTranscriptStore store = new(root);
        string mirror = NewMirrorFile("sess-1", "SAVED");

        Assert.True(store.RestoreFromMirror("sess-1", @"C:\App\Sln", mirror));
        string expected = Path.Combine(root, "C--App-Sln", "sess-1.jsonl");
        Assert.True(File.Exists(expected));
        Assert.Equal("SAVED", File.ReadAllText(expected));
    }

    [Fact]
    public void Restore_from_mirror_returns_false_when_there_is_no_mirror()
    {
        string root = NewProjectsRoot();
        ClaudeTranscriptStore store = new(root);
        Assert.False(store.RestoreFromMirror("sess-1", @"C:\App", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".jsonl")));
    }

    private string NewMirrorFile(string sessionId, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-mirrorsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        string path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Unknown_session_locates_nothing_and_deletes_nothing()
    {
        string root = NewProjectsRoot();
        WriteTranscript(root, "proj", "sess-1");
        ClaudeTranscriptStore store = new(root);

        Assert.Empty(store.Locate("missing"));
        Assert.Equal(0, store.DeleteTranscripts("missing"));
    }

    [Fact]
    public void Missing_projects_root_is_safe()
    {
        ClaudeTranscriptStore store = new(Path.Combine(Path.GetTempPath(), "cwb-does-not-exist-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(store.Locate("sess-1"));
        Assert.Equal(0, store.DeleteTranscripts("sess-1"));
    }

    public void Dispose()
    {
        foreach (string dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
