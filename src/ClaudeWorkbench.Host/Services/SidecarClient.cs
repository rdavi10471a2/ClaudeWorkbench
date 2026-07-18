using System.Net.Http.Json;

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
    public async Task<bool> ResolveGateAsync(string gateId, string decision, string? reason = null, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync($"/gates/{gateId}", new { decision, reason }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private sealed record PromptResponse(string TurnId);
}
