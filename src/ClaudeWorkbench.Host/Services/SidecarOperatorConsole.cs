using AIMonitor.McpServer;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// Sidecar-backed adapter for the turn/session seam (IOperatorConsole) and the
// blocked-work seam (IApprovalQueue). The one place aware of the sidecar event
// shapes; the approval-queue half is in the SidecarOperatorConsole.Approvals
// partial. Swap this adapter to retarget the UI at a different backend.
public sealed partial class SidecarOperatorConsole : IOperatorConsole, IApprovalQueue, IDisposable
{
    private readonly SidecarEventStream stream;
    private readonly SidecarClient client;
    private readonly WorkspaceManager workspace;
    private readonly AgentSettingsService agentSettings;
    private readonly AuthStatusProbe authProbe;

    public SidecarOperatorConsole(
        SidecarEventStream stream,
        SidecarClient client,
        WorkspaceManager workspace,
        AgentSettingsService agentSettings,
        AuthStatusProbe authProbe)
    {
        this.stream = stream;
        this.client = client;
        this.workspace = workspace;
        this.agentSettings = agentSettings;
        this.authProbe = authProbe;
        this.stream.Changed += Relay;
        this.authProbe.Changed += Relay;
    }

    public event Action? Changed;

    public string WorkspacePath => workspace.WatchedSolutionPath ?? "(no watched workspace)";

    public ConsoleStatus Status => new(stream.Connected, stream.ActiveTurn is not null);

    public AuthStatus Auth => authProbe.Current;

    public IReadOnlyList<TranscriptEntry> Transcript
    {
        get
        {
            return stream.SnapshotEvents()
                .Where(evt => evt.Type is "assistant_text" or "tool_call_started" or "user_prompt")
                .Select(evt => evt.Type switch
                {
                    "tool_call_started" => ToolOrImageEntry(evt),
                    "user_prompt" => new TranscriptEntry(TranscriptKind.User, evt.Text ?? string.Empty, FormatTime(evt.Ts)),
                    _ => new TranscriptEntry(TranscriptKind.Assistant, evt.Text ?? string.Empty, FormatTime(evt.Ts)),
                })
                .ToArray();
        }
    }

    public IReadOnlyList<ActivityEntry> Activity
    {
        get
        {
            return stream.SnapshotEvents()
                .Reverse()
                .Select(ToActivity)
                .ToArray();
        }
    }

    public async Task SendAsync(string prompt, bool autoApprove)
    {
        AgentToolPolicy policy = agentSettings.Current;
        object toolPolicy = new
        {
            allowNativeReads = policy.AllowNativeReads,
            strictMcpConfig = policy.StrictMcpConfig,
            enabledTools = policy.EnabledOptionalTools.ToArray(),
            autoApprove,
            model = policy.Model,
            effort = policy.Effort,
        };
        await client.PromptAsync(prompt, toolPolicy);
    }

    public async Task StopAsync()
    {
        await client.StopAsync();
    }

    public Task<UsageSnapshot> GetUsageAsync()
    {
        return client.GetUsageAsync();
    }

    public async Task NewThreadAsync()
    {
        await client.NewThreadAsync();
    }

    public void Dispose()
    {
        stream.Changed -= Relay;
        authProbe.Changed -= Relay;
    }

    private void Relay()
    {
        Changed?.Invoke();
    }

    private static ActivityEntry ToActivity(SidecarEvent evt)
    {
        string detail = evt.Type switch
        {
            "assistant_text" => Truncate(evt.Text),
            "tool_call_started" => ApprovalFormatter.ShortLabel(evt.Tool ?? string.Empty, evt.Input),
            "tool_call_finished" => evt.CallId ?? string.Empty,
            "gate_request" => $"{evt.Tool} {evt.FilePath}".Trim(),
            "gate_resolved" => $"{evt.GateId} -> {evt.Decision}",
            "usage" => $"in {evt.InputTokens} / out {evt.OutputTokens}",
            "turn_finished" => evt.StopReason ?? string.Empty,
            "error" => evt.Message ?? string.Empty,
            _ => string.Empty,
        };
        return new ActivityEntry(evt.Type, detail);
    }

    private static string FormatTime(long? ts)
    {
        return ts is long milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : string.Empty;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif",
    };

    // When the agent reads an image file that exists on disk, show it inline instead of a
    // "-> Read" line; /local-file serves it (the read already put it in the touched-file set).
    private static TranscriptEntry ToolOrImageEntry(SidecarEvent evt)
    {
        if (string.Equals(evt.Tool, "Read", StringComparison.OrdinalIgnoreCase))
        {
            string? path = FilePathOf(evt.Input);
            if (path is not null
                && ImageExtensions.Contains(Path.GetExtension(path))
                && FileExists(path))
            {
                return new TranscriptEntry(TranscriptKind.Image, path, FormatTime(evt.Ts));
            }
        }

        return new TranscriptEntry(TranscriptKind.ToolCall, ApprovalFormatter.ShortLabel(evt.Tool ?? string.Empty, evt.Input), FormatTime(evt.Ts));
    }

    private static bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static string? FilePathOf(System.Text.Json.JsonElement? input)
    {
        if (input is not System.Text.Json.JsonElement element
            || element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in new[] { "file_path", "path", "sourceFilePath", "filePath" })
        {
            if (element.TryGetProperty(key, out System.Text.Json.JsonElement value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string? path = value.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= 120 ? text : text.Substring(0, 117) + "...";
    }
}
