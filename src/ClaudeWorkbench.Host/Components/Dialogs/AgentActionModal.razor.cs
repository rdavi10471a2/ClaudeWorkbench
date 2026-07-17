using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Dialogs;

public partial class AgentActionModal : IDisposable
{
    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    private readonly Dictionary<string, string> answers = new(StringComparer.Ordinal);
    private string? boundElicitationId;

    private ApprovalRequest? CurrentApproval => Approvals.PendingApprovals.FirstOrDefault();

    private Elicitation? CurrentElicitation =>
        CurrentApproval is null ? Approvals.PendingElicitations.FirstOrDefault() : null;

    protected override void OnInitialized()
    {
        Approvals.Changed += OnChanged;
    }

    private void OnChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    protected override void OnParametersSet()
    {
        SyncElicitationDefaults();
    }

    private void SyncElicitationDefaults()
    {
        Elicitation? elicitation = CurrentElicitation;
        if (elicitation is null)
        {
            boundElicitationId = null;
            return;
        }

        if (boundElicitationId == elicitation.Id)
        {
            return;
        }

        boundElicitationId = elicitation.Id;
        answers.Clear();
        foreach (ElicitationField field in elicitation.Fields)
        {
            answers[field.Name] = field.Kind switch
            {
                ElicitationFieldKind.Boolean => "false",
                ElicitationFieldKind.Enum => field.Options.FirstOrDefault()?.Value ?? string.Empty,
                _ => string.Empty,
            };
        }
    }

    private string Get(string field)
    {
        return answers.TryGetValue(field, out string? value) ? value : string.Empty;
    }

    private void Set(string field, string? value)
    {
        answers[field] = value ?? string.Empty;
    }

    private bool IsTrue(string field)
    {
        return Get(field).Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResolveApprovalAsync(bool approve)
    {
        ApprovalRequest? approval = CurrentApproval;
        if (approval is null)
        {
            return;
        }

        await Approvals.ResolveApprovalAsync(approval.Id, approve);
    }

    private async Task SubmitElicitationAsync()
    {
        Elicitation? elicitation = CurrentElicitation;
        if (elicitation is null)
        {
            return;
        }

        await Approvals.AnswerElicitationAsync(elicitation.Id, new Dictionary<string, string>(answers, StringComparer.Ordinal));
    }

    public void Dispose()
    {
        Approvals.Changed -= OnChanged;
    }
}
