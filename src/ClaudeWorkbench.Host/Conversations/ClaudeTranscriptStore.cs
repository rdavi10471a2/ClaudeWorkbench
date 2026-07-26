using System.Text;

namespace ClaudeWorkbench.Host.Conversations;

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

    // Copy the primary ~/.claude transcript for a session into destDir under the caller-chosen readable
    // fileName (e.g. "AddDapperReposito.jsonl"). Overwrites so the mirror always reflects the latest
    // turn. Best-effort: returns the destination path if copied, null if there was nothing to copy or
    // the copy failed.
    public string? MirrorTo(string sessionId, string fileName, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return null;
        }

        string source = Locate(sessionId).FirstOrDefault() ?? string.Empty;
        if (source.Length == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            string destination = Path.Combine(destinationDirectory, fileName);
            File.Copy(source, destination, overwrite: true);
            return destination;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Restore the SDK's primary transcript FROM the app-owned mirror so resume reads exactly what we
    // saved. The runtime mirror is authoritative: Claude may sweep (retention) or compact its own
    // ~/.claude copy, so before a resume we overwrite the primary with ours. If a primary already
    // exists we overwrite it in place (encoding-free); otherwise we recreate it under the SDK's
    // per-cwd project dir. Best-effort: returns true if the primary now reflects the mirror.
    public bool RestoreFromMirror(string sessionId, string cwd, string mirrorPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(mirrorPath) || !File.Exists(mirrorPath))
        {
            return false;
        }

        try
        {
            // Prefer the exact existing path (no cwd-encoding guess); fall back to the computed
            // project dir only when the SDK's copy is entirely gone.
            string destination = Locate(sessionId).FirstOrDefault()
                ?? Path.Combine(projectsRoot, EncodeCwd(cwd), sessionId + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(mirrorPath, destination, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Reproduce the claude CLI's per-cwd project-folder name: the absolute cwd with path separators
    // and drive punctuation folded to '-' (e.g. C:\App\Sln -> C--App-Sln). Only used when the SDK's
    // own copy is gone, so the folder must be recreated from scratch.
    internal static string EncodeCwd(string cwd)
    {
        string full = Path.GetFullPath(cwd);
        StringBuilder builder = new(full.Length);
        foreach (char character in full)
        {
            builder.Append(character is ':' or '\\' or '/' or '.' ? '-' : character);
        }

        return builder.ToString();
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
