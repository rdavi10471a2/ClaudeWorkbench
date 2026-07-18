using ClaudeWorkbench.Host.Console;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Dialogs;

public partial class AgentActionModal : IDisposable
{
    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    // Per-question state for the current elicitation: chosen option labels (a set so
    // multi-select works) and the always-available "Other" free-text override.
    private readonly Dictionary<string, HashSet<string>> selections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> otherText = new(StringComparer.Ordinal);
    private string? boundElicitationId;

    private int ActiveQuestion { get; set; }

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
        selections.Clear();
        otherText.Clear();
        ActiveQuestion = 0;
    }

    private bool IsAnswered(ElicitationQuestion question)
    {
        return (selections.TryGetValue(question.Question, out HashSet<string>? chosen) && chosen.Count > 0)
            || !string.IsNullOrWhiteSpace(GetOther(question.Question));
    }

    private bool IsChosen(string question, string label)
    {
        return selections.TryGetValue(question, out HashSet<string>? chosen) && chosen.Contains(label);
    }

    private void Choose(string question, string label, bool multiSelect)
    {
        if (!selections.TryGetValue(question, out HashSet<string>? chosen))
        {
            chosen = new HashSet<string>(StringComparer.Ordinal);
            selections[question] = chosen;
        }

        if (multiSelect)
        {
            if (!chosen.Remove(label))
            {
                chosen.Add(label);
            }
        }
        else
        {
            chosen.Clear();
            chosen.Add(label);
        }

        // Picking an option supersedes any half-typed "Other".
        otherText.Remove(question);
    }

    // Focusing the Other box deselects that question's chosen card(s) immediately,
    // so the operator's free-text is clearly the active answer.
    private void BeginOther(string question)
    {
        selections.Remove(question);
    }

    private string GetOther(string question)
    {
        return otherText.TryGetValue(question, out string? value) ? value : string.Empty;
    }

    private void SetOther(string question, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            otherText.Remove(question);
            return;
        }

        otherText[question] = value;
        // Free text supersedes any chosen options.
        selections.Remove(question);
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

        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        foreach (ElicitationQuestion question in elicitation.Questions)
        {
            string other = GetOther(question.Question);
            if (!string.IsNullOrWhiteSpace(other))
            {
                answers[question.Question] = other.Trim();
            }
            else if (selections.TryGetValue(question.Question, out HashSet<string>? chosen) && chosen.Count > 0)
            {
                answers[question.Question] = string.Join(", ", chosen);
            }
        }

        await Approvals.AnswerElicitationAsync(elicitation.Id, answers);
    }

    public void Dispose()
    {
        Approvals.Changed -= OnChanged;
    }
}
