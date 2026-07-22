using System.Collections.Concurrent;

namespace ClaudeWorkbench.Host.Services;

// The set of files the agent has read or written this thread. Every path here came from a
// tool call the operator saw (and, for writes, approved at the gate), so it is safe to
// serve those exact files back to chat -- even when they sit outside the uploads folder.
// Populated by SidecarEventStream from tool_call_started/gate_request; consulted by
// LocalFileEndpoints alongside the uploads-root check; cleared on New Thread.
public sealed class AgentFileAccess
{
    private readonly ConcurrentDictionary<string, byte> paths = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            paths.TryAdd(Path.GetFullPath(path), 0);
        }
        catch (Exception)
        {
            // A malformed path from a tool input is simply not tracked.
        }
    }

    public bool Contains(string fullPath) => paths.ContainsKey(fullPath);

    public void Clear() => paths.Clear();
}
