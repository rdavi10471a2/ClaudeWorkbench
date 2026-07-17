using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace ClaudeWorkbench.Host.Services;

// Long-lived background reader of the sidecar's SSE /events stream. Maintains a
// bounded event history, the pending-gate set, connection state, and the active
// turn, and raises Changed so UI components can re-render. Reconnects on drop.
public sealed class SidecarEventStream : BackgroundService
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly SidecarOptions options;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly LinkedList<SidecarEvent> events = new();
    private readonly Dictionary<string, GateInfo> gates = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private const int MaxEvents = 500;

    public SidecarEventStream(IHttpClientFactory httpClientFactory, SidecarOptions options)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
    }

    public event Action? Changed;

    public bool Connected { get; private set; }

    public string? ActiveTurn { get; private set; }

    public IReadOnlyList<SidecarEvent> SnapshotEvents()
    {
        lock (sync)
        {
            return events.ToArray();
        }
    }

    public IReadOnlyList<GateInfo> PendingGates()
    {
        lock (sync)
        {
            return gates.Values.ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadStreamAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                SetConnected(false);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task ReadStreamAsync(CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using HttpResponseMessage response = await client.GetAsync(
            options.BaseUrl + "/events",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        SetConnected(true);

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line.Substring(5).Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            SidecarEvent? evt = JsonSerializer.Deserialize<SidecarEvent>(payload, json);
            if (evt is not null)
            {
                Apply(evt);
                Changed?.Invoke();
            }
        }

        SetConnected(false);
    }

    private void Apply(SidecarEvent evt)
    {
        lock (sync)
        {
            events.AddLast(evt);
            while (events.Count > MaxEvents)
            {
                events.RemoveFirst();
            }

            switch (evt.Type)
            {
                case "turn_started":
                    ActiveTurn = evt.TurnId;
                    break;
                case "turn_finished":
                    ActiveTurn = null;
                    break;
                case "gate_request" when evt.GateId is not null:
                    gates[evt.GateId] = new GateInfo(evt.GateId, evt.Tool ?? string.Empty, evt.FilePath, evt.Input);
                    break;
                case "gate_resolved" when evt.GateId is not null:
                    gates.Remove(evt.GateId);
                    break;
                default:
                    break;
            }
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected != value)
        {
            Connected = value;
            Changed?.Invoke();
        }
    }
}
