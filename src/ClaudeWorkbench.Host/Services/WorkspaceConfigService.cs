using System.Text.Json;
using AIMonitor.McpServer;

namespace ClaudeWorkbench.Host.Services;

// Loads/saves the per-solution .claudeworkbench.json (see WorkspaceConfig). Keyed to the CURRENT watched
// solution via WorkspaceManager, so it follows workspace switches automatically — the file always lives
// beside the active solution. Operator-driven and host-side; the agent is not involved.
public sealed class WorkspaceConfigService
{
    public const string FileName = ".claudeworkbench.json";
    public const string SchemaUrl = "https://claude.ai/schemas/claudeworkbench.v1.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceManager workspace;

    public WorkspaceConfigService(WorkspaceManager workspace) => this.workspace = workspace;

    // Full path to the current solution's config file, or null when no workspace is open.
    public string? ConfigPath => workspace.HasWorkspace
        ? Path.Combine(workspace.Settings.WatchedProjectFolder, FileName)
        : null;

    // The current config, or all-defaults when the file is absent/unreadable. Never throws.
    public WorkspaceConfig Load()
    {
        string? path = ConfigPath;
        if (path is null || !File.Exists(path))
        {
            return new WorkspaceConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspaceConfig>(File.ReadAllText(path), Json) ?? new WorkspaceConfig();
        }
        catch (Exception)
        {
            // A hand-broken file shouldn't wedge the app: fall back to defaults.
            return new WorkspaceConfig();
        }
    }

    // Write the config beside the solution. Stamps the schema/version so the file is self-describing.
    // Returns true on success. Never throws.
    public bool Save(WorkspaceConfig config)
    {
        string? path = ConfigPath;
        if (path is null)
        {
            return false;
        }

        try
        {
            config.Schema ??= SchemaUrl;
            if (config.Version <= 0)
            {
                config.Version = 1;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
