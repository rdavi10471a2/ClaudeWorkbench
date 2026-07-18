using System.Net.Http.Json;
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
    private readonly Dictionary<string, JsonElement> elicitations = new(StringComparer.Ordinal);
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

    public IReadOnlyList<ElicitationInfo> PendingElicitations()
    {
        lock (sync)
        {
            return elicitations.Select(pair => new ElicitationInfo(pair.Key, pair.Value)).ToArray();
        }
    }

    public void RemoveElicitation(string elicitationId)
    {
        bool removed;
        lock (sync)
        {
            removed = elicitations.Remove(elicitationId);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    // Drop a gate the sidecar no longer knows about (e.g. a resolve returned 404
    // because the gate's promise is gone). Lets the UI self-heal a stale gate.
    public void RemoveGate(string gateId)
    {
        bool removed;
        lock (sync)
        {
            removed = gates.Remove(gateId);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    // Authoritative pending-gate snapshot from the live registry. Called on every
    // (re)connect so stale gates from a prior connection or a sidecar restart are
    // dropped and only live gates remain.
    private async Task SeedGatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            GateInfo[]? live = await client.GetFromJsonAsync<GateInfo[]>(
                options.BaseUrl + "/gates",
                json,
                cancellationToken);
            lock (sync)
            {
                gates.Clear();
                if (live is not null)
                {
                    foreach (GateInfo gate in live)
                    {
                        gates[gate.GateId] = gate;
                    }
                }
            }

            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Best-effort; live gate_request events will still populate the set.
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
        await SeedGatesAsync(cancellationToken);
        await SeedElicitationsAsync(cancellationToken);

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

    private async Task SeedElicitationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            JsonElement[]? live = await client.GetFromJsonAsync<JsonElement[]>(
                options.BaseUrl + "/elicitations",
                json,
                cancellationToken);
            lock (sync)
            {
                elicitations.Clear();
                if (live is not null)
                {
                    foreach (JsonElement entry in live)
                    {
                        if (entry.TryGetProperty("elicitationId", out JsonElement id)
                            && id.ValueKind == JsonValueKind.String
                            && entry.TryGetProperty("questions", out JsonElement questions))
                        {
                            elicitations[id.GetString()!] = questions.Clone();
                        }
                    }
                }
            }

            Changed?.Invoke();
        }
        catch (Exception)
        {
            // best-effort; live elicitation_request events still populate the set.
        }
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
                case "thread_reset":
                    events.Clear();
                    gates.Clear();
                    elicitations.Clear();
                    ActiveTurn = null;
                    break;
                case "elicitation_request" when evt.ElicitationId is not null && evt.Questions is JsonElement questions:
                    elicitations[evt.ElicitationId] = questions;
                    break;
                case "elicitation_resolved" when evt.ElicitationId is not null:
                    elicitations.Remove(evt.ElicitationId);
                    break;
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
