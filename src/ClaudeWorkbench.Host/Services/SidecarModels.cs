using System.Text.Json;

namespace ClaudeWorkbench.Host.Services;

// Mirror of the sidecar's neutral SidecarEvent contract (camelCase over SSE).
// All fields optional; Type discriminates.
public sealed record SidecarEvent
{
    public string Type { get; init; } = "";
    public string? TurnId { get; init; }
    public string? Text { get; init; }
    public string? Tool { get; init; }
    public string? CallId { get; init; }
    public string? GateId { get; init; }
    public string? Decision { get; init; }
    public string? Reason { get; init; }
    public string? StopReason { get; init; }
    public string? Message { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public string? FilePath { get; init; }
    public JsonElement? Input { get; init; }
}

public sealed record GateInfo(string GateId, string Tool, string? FilePath, JsonElement? Input);

public sealed class SidecarOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6110";
}
