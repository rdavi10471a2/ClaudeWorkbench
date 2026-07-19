namespace ClaudeWorkbench.Host.Services;

// How the host launches the Node sidecar as a managed child process, so an
// installed app is a single start. Overridable via the Sidecar:* config section.
public sealed class SidecarLaunchOptions
{
    public bool AutoStart { get; init; } = true;

    public string NodeExecutable { get; init; } = "node";

    public string SidecarDirectory { get; init; } = string.Empty;

    public int Port { get; init; } = 6110;

    public string McpUrl { get; init; } = "http://localhost:6100/mcp";
}
