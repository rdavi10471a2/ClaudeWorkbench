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

    public async Task<string?> PromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync("/prompt", new { prompt }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        PromptResponse? payload = await response.Content.ReadFromJsonAsync<PromptResponse>(cancellationToken);
        return payload?.TurnId;
    }

    public async Task ResolveGateAsync(string gateId, string decision, string? reason = null, CancellationToken cancellationToken = default)
    {
        await http.PostAsJsonAsync($"/gates/{gateId}", new { decision, reason }, cancellationToken);
    }

    private sealed record PromptResponse(string TurnId);
}
