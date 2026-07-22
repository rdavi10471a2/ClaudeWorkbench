using System.Net.Http.Json;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// Typed client for the Node sidecar's control HTTP surface (/prompt, /gates).
// The event stream is consumed separately by SidecarEventStream over SSE.
public sealed class SidecarClient
{
    private readonly HttpClient http;

    public SidecarClient(HttpClient http)
    {
        this.http = http;
    }

    public async Task<string?> PromptAsync(string prompt, object toolPolicy, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync("/prompt", new { prompt, toolPolicy }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        PromptResponse? payload = await response.Content.ReadFromJsonAsync<PromptResponse>(cancellationToken);
        return payload?.TurnId;
    }

    // Returns false when the sidecar no longer has the gate (404) so the caller
    // can drop the stale gate from the UI instead of silently no-op'ing.
    public async Task<bool> ResolveGateAsync(string gateId, string decision, string? reason = null, bool remember = false, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync($"/gates/{gateId}", new { decision, reason, remember }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Answer the agent's AskUserQuestion: answers maps each question text to the
    // operator's chosen label (or free text). Returns false if the elicitation is gone.
    public async Task<bool> AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync($"/elicitations/{elicitationId}", new { answers }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Interrupt the in-flight turn (aborts the SDK query on the sidecar).
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await http.PostAsJsonAsync("/stop", new { }, cancellationToken);
    }

    // Start a fresh conversation thread: the sidecar drops the resumed session id
    // and clears its event history so the agent no longer remembers prior turns.
    public async Task<bool> NewThreadAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync("/new-thread", new { }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // Echo a merge-review outcome (build + index result) to the agent: the sidecar
    // surfaces it in the transcript and prepends it to the agent's next prompt.
    // Returns false when the agent was NOT told (sidecar down, or it rejected the
    // post). This is the only channel that reports a failed build back to the agent,
    // so a silent drop is a bug — never let the transport throw past the caller, but
    // do report the failure so the operator can be warned.
    public async Task<bool> PostReviewOutcomeAsync(string summary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        try
        {
            HttpResponseMessage response = await http.PostAsJsonAsync("/review-outcome", new { summary }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // Live usage off the sidecar's Query handle (/usage). Lenient parse — the SDK
    // methods behind it are experimental, so any missing field just stays null.
    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement root = await http.GetFromJsonAsync<JsonElement>("/usage", cancellationToken);
            JsonElement context = root.TryGetProperty("context", out JsonElement c) ? c : default;
            JsonElement subscription = root.TryGetProperty("subscription", out JsonElement s) ? s : default;
            bool haveContext = context.ValueKind == JsonValueKind.Object;
            bool haveSubscription = subscription.ValueKind == JsonValueKind.Object;
            if (!haveContext && !haveSubscription)
            {
                return UsageSnapshot.Empty;
            }

            double? weekly = null;
            string? weeklyResets = null;
            double? fiveHour = null;
            string? fiveHourResets = null;
            double? monthly = null;
            if (haveSubscription && subscription.TryGetProperty("rate_limits", out JsonElement limits)
                && limits.ValueKind == JsonValueKind.Object)
            {
                if (limits.TryGetProperty("seven_day", out JsonElement wk) && wk.ValueKind == JsonValueKind.Object)
                {
                    weekly = ReadNumber(wk, "utilization");
                    weeklyResets = ReadString(wk, "resets_at");
                }

                if (limits.TryGetProperty("five_hour", out JsonElement fh) && fh.ValueKind == JsonValueKind.Object)
                {
                    fiveHour = ReadNumber(fh, "utilization");
                    fiveHourResets = ReadString(fh, "resets_at");
                }

                if (limits.TryGetProperty("extra_usage", out JsonElement eu) && eu.ValueKind == JsonValueKind.Object)
                {
                    monthly = ReadNumber(eu, "utilization");
                }
            }

            return new UsageSnapshot(
                true,
                ReadNumber(context, "percentage"),
                ReadLong(context, "totalTokens"),
                ReadLong(context, "maxTokens"),
                ReadLong(context, "autoCompactThreshold"),
                ReadString(subscription, "subscription_type"),
                weekly,
                weeklyResets,
                fiveHour,
                fiveHourResets,
                monthly);
        }
        catch (Exception)
        {
            return UsageSnapshot.Empty;
        }
    }

    private static double? ReadNumber(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record PromptResponse(string TurnId);
}
