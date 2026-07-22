using System.Text.Json;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// IApprovalQueue half of the sidecar adapter: maps the sidecar's pending gates
// to neutral ApprovalRequests, and resolves them via the control client.
// Elicitations are modelled but not yet raised by the backend (Build 5).
public sealed partial class SidecarOperatorConsole
{
    public IReadOnlyList<ApprovalRequest> PendingApprovals
    {
        get
        {
            return stream.PendingGates()
                .Select(gate =>
                {
                    (string title, IReadOnlyList<ApprovalDetail> details, string? prettyJson) =
                        ApprovalFormatter.Describe(gate.Tool, gate.FilePath, gate.Input?.ToString());
                    return new ApprovalRequest(
                        gate.GateId,
                        gate.Tool,
                        gate.FilePath,
                        title,
                        details,
                        prettyJson);
                })
                .ToArray();
        }
    }

    public IReadOnlyList<Elicitation> PendingElicitations =>
        stream.PendingElicitations().Select(MapElicitation).ToArray();

    public async Task ResolveApprovalAsync(string approvalId, bool approve, string? reason = null, bool remember = false)
    {
        bool resolved = await client.ResolveGateAsync(approvalId, approve ? "allow" : "deny", reason, remember);
        if (!resolved)
        {
            // The gate is gone on the sidecar (stale). Drop it so the UI clears
            // instead of leaving a dead dialog the operator keeps clicking.
            stream.RemoveGate(approvalId);
        }
    }

    public async Task AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> values)
    {
        bool ok = await client.AnswerElicitationAsync(elicitationId, values);
        if (!ok)
        {
            stream.RemoveElicitation(elicitationId);
        }
    }

    // Parse the SDK AskUserQuestion `questions` payload into the neutral model.
    private static Elicitation MapElicitation(ElicitationInfo info)
    {
        List<ElicitationQuestion> questions = new();
        if (info.Questions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement question in info.Questions.EnumerateArray())
            {
                List<ElicitationOption> options = new();
                if (question.TryGetProperty("options", out JsonElement optionArray)
                    && optionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement option in optionArray.EnumerateArray())
                    {
                        options.Add(new ElicitationOption(ReadString(option, "label"), ReadStringOrNull(option, "description")));
                    }
                }

                bool multiSelect = question.TryGetProperty("multiSelect", out JsonElement multi)
                    && multi.ValueKind == JsonValueKind.True;
                questions.Add(new ElicitationQuestion(
                    ReadString(question, "question"),
                    ReadString(question, "header"),
                    options,
                    multiSelect));
            }
        }

        return new Elicitation(info.Id, questions);
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? ReadStringOrNull(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
