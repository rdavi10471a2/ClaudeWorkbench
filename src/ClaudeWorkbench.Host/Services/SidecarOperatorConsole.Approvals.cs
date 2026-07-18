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

    public IReadOnlyList<Elicitation> PendingElicitations => [];

    public async Task ResolveApprovalAsync(string approvalId, bool approve, string? reason = null)
    {
        await client.ResolveGateAsync(approvalId, approve ? "allow" : "deny", reason);
    }

    public Task AnswerElicitationAsync(string elicitationId, IReadOnlyDictionary<string, string> values)
    {
        // No elicitation transport yet; wired in Build 5 (request_operator_input tool).
        return Task.CompletedTask;
    }
}
