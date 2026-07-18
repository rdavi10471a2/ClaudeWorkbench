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

    // Answer the agent's AskUserQuestion: answers maps each question text to the
    // operator's chosen label (or free text). Returns false if the elicitation is gone.
    public async Task<bool> AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync($"/elicitations/{elicitationId}", new { answers }, cancellationToken);
        return response.IsSuccessStatusCode;
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
    public async Task PostReviewOutcomeAsync(string summary, CancellationToken cancellationToken = default)
    {
        await http.PostAsJsonAsync("/review-outcome", new { summary }, cancellationToken);
    }

    private sealed record PromptResponse(string TurnId);
}
