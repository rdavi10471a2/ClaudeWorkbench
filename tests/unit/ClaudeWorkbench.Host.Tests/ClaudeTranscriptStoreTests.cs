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
