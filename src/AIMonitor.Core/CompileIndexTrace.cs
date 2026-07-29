using System.Text;

namespace AIMonitor.Core;

/// <summary>
/// Append-only, timestamped provenance trace of the compile→index pipeline. It exists to make two facts
/// PROVABLE at runtime instead of argued from memory:
///   1. exactly which source/workspace PATH each compile and the index actually read, and
///   2. the ORDER of events — specifically whether the index runs after a build, and after WHICH build.
/// One line per event with a process-monotonic sequence and a UTC timestamp, written to
/// <c>&lt;watched-solution-workspace&gt;/compile-index-trace.log</c>. Reading that file top-to-bottom shows,
/// e.g., <c>gate-build.*</c> against the persistent validation workspace followed by <c>index-compile.*</c>
/// against the real watched tree — the two-compile reality, in order, with the paths named.
///
/// Best-effort by contract: an IO failure here must NEVER break the build or index it observes, so every
/// failure is swallowed.
/// </summary>
public static class CompileIndexTrace
{
    private static readonly object Gate = new();
    private static long sequence;

    /// <summary>
    /// Optional live sink for the same events, so the host can surface each step to the operator (a spinner
    /// status line, the chat/activity stream) as it happens — not only to the on-disk file. The host wires this
    /// once at startup; AIMonitor.Core stays UI-agnostic. Receives a short one-line human summary per event.
    /// Best-effort: exceptions from the sink are swallowed like the file write.
    /// </summary>
    public static Action<string>? Echo { get; set; }

    /// <summary>The trace file for a given watched solution. One file per watched-solution workspace.</summary>
    public static string GetTraceFilePath(MonitorSettings settings)
    {
        return Path.Combine(
            MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings),
            "compile-index-trace.log");
    }

    /// <summary>
    /// Append one event. <paramref name="phase"/> is a short dotted verb (e.g. "gate-build.start",
    /// "index-compile.done"); <paramref name="path"/> is the source/workspace the event acted on — the
    /// thing we are proving; <paramref name="detail"/> is free-form context.
    /// </summary>
    public static void Record(MonitorSettings settings, string phase, string path, string detail)
    {
        try
        {
            string file = GetTraceFilePath(settings);
            string? directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                long seq = ++sequence;
                string line = string.Format(
                    "[{0:D5}] {1:O} {2,-22} path={3} | {4}{5}",
                    seq,
                    DateTime.UtcNow,
                    phase,
                    string.IsNullOrEmpty(path) ? "-" : path,
                    detail ?? string.Empty,
                    Environment.NewLine);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Provenance trace is best-effort. An IO failure must never break a build or index.
        }

        try
        {
            Action<string>? echo = Echo;
            echo?.Invoke($"{phase} · {(string.IsNullOrEmpty(path) ? "-" : path)} · {detail}");
        }
        catch
        {
            // The live sink is best-effort too — a UI/echo failure must never break a build or index.
        }
    }
}
