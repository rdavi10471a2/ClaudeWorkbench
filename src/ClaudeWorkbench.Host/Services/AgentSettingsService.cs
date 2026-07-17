using System.Text.Json;
using AIMonitor.Core;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// Holds the operator's AgentToolPolicy, persisted to runtime so it survives host
// restarts. Singleton; raises Changed when updated so the UI can refresh.
public sealed class AgentSettingsService
{
    private readonly string path;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object sync = new();
    private AgentToolPolicy policy;

    public AgentSettingsService(MonitorSettings settings)
    {
        path = Path.Combine(settings.RuntimeRoot, "agent-settings.json");
        policy = Load();
    }

    public event Action? Changed;

    public AgentToolPolicy Current
    {
        get
        {
            lock (sync)
            {
                return policy.Clone();
            }
        }
    }

    public void Update(AgentToolPolicy updated)
    {
        lock (sync)
        {
            policy = updated.Clone();
            Save(policy);
        }

        Changed?.Invoke();
    }

    private AgentToolPolicy Load()
    {
        try
        {
            if (File.Exists(path))
            {
                AgentToolPolicy? loaded = JsonSerializer.Deserialize<AgentToolPolicy>(File.ReadAllText(path), json);
                if (loaded is not null)
                {
                    loaded.EnabledOptionalTools = new HashSet<string>(loaded.EnabledOptionalTools, StringComparer.Ordinal);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // fall through to defaults on any read/parse error
        }

        return new AgentToolPolicy();
    }

    private void Save(AgentToolPolicy value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, json));
        }
        catch (Exception)
        {
            // best-effort persistence; policy still applies in-memory this session
        }
    }
}
