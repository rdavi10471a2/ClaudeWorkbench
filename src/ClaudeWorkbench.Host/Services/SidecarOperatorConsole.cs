using AIMonitor.Core;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// The one adapter that bridges the neutral IOperatorConsole the UI binds to and
// the sidecar/Claude specifics (SidecarEventStream + SidecarClient + SidecarEvent).
// All translation of wire shapes -> view models happens here and nowhere else.
public sealed class SidecarOperatorConsole : IOperatorConsole, IDisposable
{
    private readonly SidecarEventStream stream;
    private readonly SidecarClient client;
    private readonly MonitorSettings settings;

    public SidecarOperatorConsole(SidecarEventStream stream, SidecarClient client, MonitorSettings settings)
    {
        this.stream = stream;
        this.client = client;
        this.settings = settings;
        this.stream.Changed += Relay;
    }

    public event Action? Changed;

    public string WorkspacePath => settings.WatchedSolutionPath;

    public ConsoleStatus Status => new(stream.Connected, stream.ActiveTurn is not null);

    public IReadOnlyList<TranscriptEntry> Transcript
    {
        get
        {
            return stream.SnapshotEvents()
                .Where(evt => evt.Type is "assistant_text" or "tool_call_started")
                .Select(evt => evt.Type == "tool_call_started"
                    ? new TranscriptEntry(TranscriptKind.ToolCall, evt.Tool ?? string.Empty)
                    : new TranscriptEntry(TranscriptKind.Assistant, evt.Text ?? string.Empty))
                .ToArray();
        }
    }

    public IReadOnlyList<ApprovalRequest> PendingApprovals
    {
        get
        {
            return stream.PendingGates()
                .Select(gate => new ApprovalRequest(gate.GateId, gate.Tool, gate.FilePath))
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

    public async Task SendAsync(string prompt)
    {
        await client.PromptAsync(prompt);
    }

    public async Task ResolveAsync(string approvalId, bool approve, string? reason = null)
    {
        await client.ResolveGateAsync(approvalId, approve ? "allow" : "deny", reason);
    }

    public void Dispose()
    {
        stream.Changed -= Relay;
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
            "tool_call_started" => evt.Tool ?? string.Empty,
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

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= 120 ? text : text.Substring(0, 117) + "...";
    }
}
