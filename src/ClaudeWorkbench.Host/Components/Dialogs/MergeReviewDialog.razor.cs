using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.AspNetCore.Components;

namespace ClaudeWorkbench.Host.Components.Dialogs;

public partial class MergeReviewDialog : IDisposable
{
    [Inject]
    private IReviewWorkflow Review { get; set; } = default!;

    [Inject]
    private IApprovalQueue Approvals { get; set; } = default!;

    private IReadOnlyList<ReviewQueueItem> pendingItems = [];
    private ReviewRecordModel? selectedModel;
    private SideBySideDiffModel? diffModel;
    private string? selectedRecordId;
    private string? errorMessage;
    private bool actionBusy;
    private bool dismissed;
    private int lastPendingCount = -1;

    private int LineCount => Math.Max(diffModel?.NewText.Lines.Count ?? 0, diffModel?.OldText.Lines.Count ?? 0);

    // Never overlap the permission/elicitation dialog; give it precedence.
    private bool GateBusy => Approvals.PendingApprovals.Count > 0 || Approvals.PendingElicitations.Count > 0;

    private bool ShouldShow => pendingItems.Count > 0 && !dismissed && !GateBusy;

    private bool CanAcceptNormally => selectedModel is not null
        && !selectedModel.IsDecided
        && (!selectedModel.PreMergeValidationIsError || selectedModel.PreMergeValidationForceApproved);

    private bool CanForceAccept => selectedModel is not null
        && !selectedModel.IsDecided
        && selectedModel.PreMergeValidationIsError
        && !selectedModel.PreMergeValidationForceApproved;

    protected override void OnInitialized()
    {
        Approvals.Changed += OnChanged;
        LoadReview(preserveSelection: false);
    }

    private void OnChanged()
    {
        InvokeAsync(() =>
        {
            LoadReview(preserveSelection: true);
            // New staging activity re-opens a dialog the operator had closed.
            if (pendingItems.Count != lastPendingCount)
            {
                if (pendingItems.Count > lastPendingCount)
                {
                    dismissed = false;
                }

                lastPendingCount = pendingItems.Count;
            }

            StateHasChanged();
        });
    }

    private void RefreshReview()
    {
        LoadReview(preserveSelection: true);
    }

    private void CloseDialog()
    {
        dismissed = true;
    }

    private void SelectRecord(string stagedRecordId)
    {
        try
        {
            selectedModel = Review.Load(stagedRecordId);
            selectedRecordId = stagedRecordId;
            errorMessage = null;
        }
        catch (Exception ex)
        {
            selectedModel = null;
            selectedRecordId = null;
            errorMessage = ex.Message;
        }

        BuildDiff();
    }

    private Task AcceptSelectedAsync()
    {
        return AcceptSelectedCoreAsync(forceApproveValidation: false);
    }

    private Task AcceptSelectedWithOverrideAsync()
    {
        return AcceptSelectedCoreAsync(forceApproveValidation: true);
    }

    private async Task AcceptSelectedCoreAsync(bool forceApproveValidation)
    {
        if (string.IsNullOrWhiteSpace(selectedRecordId) || actionBusy)
        {
            return;
        }

        actionBusy = true;
        try
        {
            await InvokeAsync(StateHasChanged);
            string recordId = selectedRecordId;
            ReviewActionResult result = await Task.Run(() => Review.Accept(recordId, forceApproveValidation));
            errorMessage = result.Message;
            LoadReview(preserveSelection: false);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            actionBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RejectSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(selectedRecordId) || actionBusy)
        {
            return;
        }

        actionBusy = true;
        try
        {
            await InvokeAsync(StateHasChanged);
            string recordId = selectedRecordId;
            ReviewActionResult result = await Task.Run(() => Review.Reject(recordId));
            errorMessage = result.Message;
            LoadReview(preserveSelection: false);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            actionBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void LoadReview(bool preserveSelection)
    {
        try
        {
            pendingItems = Review.ListPending();
            errorMessage = null;

            string? recordIdToLoad = preserveSelection
                ? pendingItems.FirstOrDefault(item => item.StagedRecordId == selectedRecordId)?.StagedRecordId
                : null;
            recordIdToLoad ??= pendingItems.FirstOrDefault()?.StagedRecordId;

            if (string.IsNullOrWhiteSpace(recordIdToLoad))
            {
                selectedModel = null;
                selectedRecordId = null;
                BuildDiff();
                return;
            }

            selectedModel = Review.Load(recordIdToLoad);
            selectedRecordId = recordIdToLoad;
            BuildDiff();
        }
        catch (Exception ex)
        {
            pendingItems = [];
            selectedModel = null;
            selectedRecordId = null;
            errorMessage = ex.Message;
            BuildDiff();
        }
    }

    private void BuildDiff()
    {
        SideBySideDiffBuilder builder = new(new Differ());
        diffModel = builder.BuildDiffModel(selectedModel?.CurrentText ?? string.Empty, selectedModel?.ProposedText ?? string.Empty);
    }

    private DiffPiece? GetProposedLine(int index)
    {
        if (diffModel is null || index >= diffModel.NewText.Lines.Count)
        {
            return null;
        }

        return diffModel.NewText.Lines[index];
    }

    private DiffPiece? GetCurrentLine(int index)
    {
        if (diffModel is null || index >= diffModel.OldText.Lines.Count)
        {
            return null;
        }

        return diffModel.OldText.Lines[index];
    }

    private static string NormalizeLabel(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "pending" : value;
    }

    private static string GetLineNumber(DiffPiece? piece)
    {
        if (piece is null || piece.Position is null)
        {
            return string.Empty;
        }

        return piece.Position.Value.ToString();
    }

    private static string GetLineText(DiffPiece? piece)
    {
        return piece?.Text ?? string.Empty;
    }

    private static string GetLineClass(DiffPiece? piece)
    {
        if (piece is null)
        {
            return "imaginary";
        }

        return piece.Type switch
        {
            ChangeType.Inserted => "inserted",
            ChangeType.Deleted => "deleted",
            ChangeType.Modified => "modified",
            ChangeType.Imaginary => "imaginary",
            _ => "unchanged"
        };
    }

    public void Dispose()
    {
        Approvals.Changed -= OnChanged;
    }
}
