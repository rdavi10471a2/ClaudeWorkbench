namespace ClaudeWorkbench.Host.Threads;

// Locates and hard-deletes Claude Agent SDK transcript files, which live OUTSIDE this app at
// ~/.claude/projects/<encoded-cwd>/<sessionId>.jsonl. The SDK's cwd-encoding scheme is an
// implementation detail we do not want to hard-code, so lookup is encoding-agnostic: scan the
// project folders for a file named <sessionId>.jsonl. This is the disk that a thread's hard
// delete must reclaim (transcripts observed at 11-28 MB each).
public sealed class ClaudeTranscriptStore
{
    private readonly string projectsRoot;

    public ClaudeTranscriptStore()
        : this(DefaultProjectsRoot())
    {
    }

    public ClaudeTranscriptStore(string projectsRoot)
    {
        this.projectsRoot = Path.GetFullPath(projectsRoot);
    }

    public static string DefaultProjectsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    // Every transcript file matching this session id, across all project folders (usually one).
    public IReadOnlyList<string> Locate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(projectsRoot))
        {
            return [];
        }

        string fileName = sessionId + ".jsonl";
        List<string> matches = [];
        foreach (string projectDir in Directory.EnumerateDirectories(projectsRoot))
        {
            string candidate = Path.Combine(projectDir, fileName);
            if (File.Exists(candidate))
            {
                matches.Add(candidate);
            }
        }

        return matches;
    }

    // Hard-delete the transcript(s) for a session to reclaim disk. Best-effort: returns how many
    // files were removed; a locked/absent file is skipped, not thrown.
    public int DeleteTranscripts(string sessionId)
    {
        int deleted = 0;
        foreach (string path in Locate(sessionId))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
