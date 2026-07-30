using System.Text.Json.Serialization;

namespace ClaudeWorkbench.Host.Services;

// Per-watched-solution operator configuration, persisted as `.claudeworkbench.json` at the solution
// root (beside the .slnx) — the same root-level, committed, shared convention as .vscode/settings.json
// and .editorconfig. It is edited in-app by the operator (never through the agent/merge-review workflow)
// and is COMMITTED, so changes to it show up as an ordinary Git-page change and travel with the repo.
//
// Scope note: this is per-solution, so it lives WITH the solution — not in global config/ (shared across
// all solutions) and not in runtime/ (per-solution but disposable, rebuilt state). A missing file means
// "all defaults".
public sealed class WorkspaceConfig
{
    // Optional JSON Schema reference for editor validation/versioning (kept first so it heads the file).
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    // Config format version — lets us migrate the shape later without guessing.
    public int Version { get; set; } = 1;

    public FilesTreeSettings FilesTree { get; set; } = new();

    public GitSettings Git { get; set; } = new();

    public sealed class FilesTreeSettings
    {
        // Extra directory names excluded from the Source "Files" filesystem fallback (merged with the
        // built-in bin/obj/.git/node_modules set). Only consulted when git isn't the source of truth.
        public List<string> ExcludeDirectories { get; set; } = [];
    }

    public sealed class GitSettings
    {
        // Default branch name written into a freshly-initialized repo (git init.defaultBranch). Null = the
        // machine/git default.
        public string? DefaultBranch { get; set; }
    }
}
