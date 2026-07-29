using System.Text.Json;
using AIMonitor.Core;

namespace AIMonitor.Data;

/// <summary>
/// A tiny, persisted workspace-level marker recording that the index's update is BLOCKED because the build is
/// red. It exists because <see cref="MonitorStatusResult.StaleFileCount"/> alone can't tell "stale, just
/// reindex" from "stale AND stuck until the build is fixed" — the latter is what this flag names.
///
/// Deliberately a sibling file next to the index database, NOT a row inside it: a failed build must leave the
/// symbol index untouched (it is the last-good view), so the "blocked" fact is recorded out-of-band. Set on a
/// reindex that the build blocked; cleared on the next green reindex. Best-effort by contract — an IO failure
/// here must never break a build or index, so every failure is swallowed.
/// </summary>
public static class IndexHealthMarker
{
    /// <summary>The recorded block: the compiler/build errors that stopped the reindex, and when.</summary>
    public sealed record BlockedState(string BuildError, DateTimeOffset BlockedAtUtc);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Beside the index DB (…/data/index-health.json), so it travels with the per-workspace index state.
    private static string MarkerPath(MonitorSettings settings)
    {
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        string directory = Path.GetDirectoryName(databasePath) ?? string.Empty;
        return Path.Combine(directory, "index-health.json");
    }

    /// <summary>Record that a red build blocked the index update, with the errors to surface.</summary>
    public static void SetBlocked(MonitorSettings settings, string? buildError)
    {
        try
        {
            string path = MarkerPath(settings);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            BlockedState state = new(
                string.IsNullOrWhiteSpace(buildError) ? "The build failed." : buildError,
                DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // Best-effort: the flag is a convenience, never load-bearing for the build/index itself.
        }
    }

    /// <summary>Clear the block — the index advanced on a green build.</summary>
    public static void ClearBlocked(MonitorSettings settings)
    {
        try
        {
            string path = MarkerPath(settings);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>The current block, or null when the index is not blocked on a bad build.</summary>
    public static BlockedState? Read(MonitorSettings settings)
    {
        try
        {
            string path = MarkerPath(settings);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<BlockedState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
