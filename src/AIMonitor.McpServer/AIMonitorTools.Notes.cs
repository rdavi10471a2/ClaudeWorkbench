using AIMonitor.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace AIMonitor.McpServer;

// The agent's ungoverned scratchpad. Unlike watched source (read-only to the agent; changes
// flow only through the staging workflow -> operator Accept), these notes live under the
// per-workspace runtime at agent-notes\, physically OUTSIDE watched source. The agent writes
// them freely: plans, working notes, anything it wants to remember across the turn. Every path
// is confined to the notes root by the tool itself (no rooted paths, no '..' escape), so the
// agent can never reach source or any other runtime folder through these tools. No operator
// gate — nothing here can mutate governed source.
public sealed partial class AIMonitorTools
{
    private string NotesRoot => Path.Combine(
        MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings),
        "agent-notes");

    [McpServerTool]
    [Description("Write a freeform note to the agent scratchpad (runtime\\<workspace>\\agent-notes). This is the ONLY place the agent may write directly: it is outside watched source and ungoverned. Use it for plans, working notes, and anything to remember. relativePath is confined to the notes folder (no absolute paths, no '..'). Set append=true to add to an existing note instead of overwriting.")]
    public AgentNoteWriteResult WriteNote(
        [Description("Path of the note relative to the agent-notes folder, e.g. 'plan.md' or 'research/findings.md'. Must stay inside the folder.")] string relativePath,
        [Description("The note content to write.")] string content,
        [Description("Append to the note instead of overwriting it. Default false.")] bool append = false)
    {
        runtimeState.Touch();
        string fullPath = ResolveNotePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string text = content ?? string.Empty;
        if (append)
        {
            File.AppendAllText(fullPath, text);
        }
        else
        {
            File.WriteAllText(fullPath, text);
        }

        return new AgentNoteWriteResult(
            Path.GetRelativePath(NotesRoot, fullPath),
            fullPath,
            Encoding.UTF8.GetByteCount(text),
            append);
    }

    [McpServerTool]
    [Description("List the notes in the agent scratchpad (runtime\\<workspace>\\agent-notes), newest first.")]
    public IReadOnlyList<AgentNoteInfo> ListNotes(
        [Description("Maximum notes to return.")] int maxEntries = 200)
    {
        runtimeState.Touch();
        string root = Path.GetFullPath(NotesRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(maxEntries)
            .Select(info => new AgentNoteInfo(
                Path.GetRelativePath(root, info.FullName),
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc))
            .ToArray();
    }

    [McpServerTool]
    [Description("Read a note from the agent scratchpad (runtime\\<workspace>\\agent-notes). relativePath is confined to the notes folder.")]
    public AgentNoteReadResult ReadNote(
        [Description("Path of the note relative to the agent-notes folder.")] string relativePath)
    {
        runtimeState.Touch();
        string fullPath = ResolveNotePath(relativePath);
        bool exists = File.Exists(fullPath);
        return new AgentNoteReadResult(
            Path.GetRelativePath(NotesRoot, fullPath),
            fullPath,
            exists,
            exists ? File.ReadAllText(fullPath) : string.Empty);
    }

    [McpServerTool]
    [Description("Delete a note from the agent scratchpad (runtime\\<workspace>\\agent-notes) to reclaim space. relativePath is confined to the notes folder.")]
    public AgentNoteDeleteResult DeleteNote(
        [Description("Path of the note relative to the agent-notes folder.")] string relativePath)
    {
        runtimeState.Touch();
        string fullPath = ResolveNotePath(relativePath);
        bool existed = File.Exists(fullPath);
        if (existed)
        {
            File.Delete(fullPath);
        }

        return new AgentNoteDeleteResult(
            Path.GetRelativePath(NotesRoot, fullPath),
            fullPath,
            existed);
    }

    // Confine every note path to the notes root. Rejects rooted paths and any '..' escape:
    // the resolved absolute path must sit strictly under the notes root. Same containment
    // discipline WorkflowEditService uses for watched paths — the guarantee lives in the tool,
    // not in a caller's good behaviour.
    private string ResolveNotePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A note path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Note path must be relative to the agent-notes folder, not absolute: " + relativePath);
        }

        string root = Path.GetFullPath(NotesRoot);
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        bool insideRoot = fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!insideRoot)
        {
            throw new InvalidOperationException("Note path must stay inside the agent-notes folder: " + relativePath);
        }

        return fullPath;
    }
}
