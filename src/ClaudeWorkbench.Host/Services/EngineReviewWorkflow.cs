using System.Collections.Concurrent;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;

namespace ClaudeWorkbench.Host.Services;

// In-process adapter over the AIMonitor workflow engine. Replaces the external
// WinMerge step: GATE 1 pre-merge validation runs in-app, and on accept the staged
// bytes are written to the watched file here (host-side, operator-authorized) before
// the decision is recorded. The agent never writes watched source.
public sealed class EngineReviewWorkflow : IReviewWorkflow
{
    private readonly WorkspaceManager workspace;
    private readonly ConcurrentDictionary<string, string[]> diagnosticsCache = new();

    public EngineReviewWorkflow(WorkspaceManager workspace)
    {
        this.workspace = workspace;
    }

    public IReadOnlyList<ReviewQueueItem> ListPending()
    {
        List<ReviewQueueItem> items = new();
        foreach (StagedEditRecord record in workspace.EditService.ListStagedRecords(null))
        {
            if (IsPending(record))
            {
                items.Add(ToQueueItem(record));
            }
        }

        return items
            .OrderBy(item => item.CreatedAtUtc, StringComparer.Ordinal)
            .ToList();
    }

    public ReviewRecordModel Load(string stagedRecordId)
    {
        StagedEditRecord record = workspace.EditService.GetStagedRecord(stagedRecordId);
        EnsureValidatedAndLaunched(record);
        record = workspace.EditService.GetStagedRecord(stagedRecordId);
        return ToModel(record);
    }

    public ReviewRecordModel LoadNextForSession(string sessionId)
    {
        StagedEditRecord? next = null;
        foreach (StagedEditRecord record in workspace.EditService.ListStagedRecords(sessionId))
        {
            if (IsPending(record) && (next is null || string.CompareOrdinal(record.CreatedAtUtc, next.CreatedAtUtc) < 0))
            {
                next = record;
            }
        }

        if (next is null)
        {
            return ReviewRecordModel.SessionComplete();
        }

        return Load(next.StagedRecordId);
    }

    public ReviewActionResult Accept(string stagedRecordId, bool forceApproveValidation)
    {
        StagedEditRecord record = workspace.EditService.GetStagedRecord(stagedRecordId);
        EnsureValidatedAndLaunched(record);
        record = workspace.EditService.GetStagedRecord(stagedRecordId);

        if (record.PreMergeValidationIsError && !record.PreMergeValidationForceApproved)
        {
            if (!forceApproveValidation)
            {
                return new ReviewActionResult(
                    "Pre-merge validation reported errors. Use Accept With Validation Override to merge despite the failure.");
            }

            PreMergeValidationResult revalidation = new PreMergeValidationService().Validate(workspace.Settings, record);
            workspace.EditService.RecordPreMergeValidation(stagedRecordId, revalidation, forceApproved: true);
        }

        if (!File.Exists(record.StagedFilePath))
        {
            return new ReviewActionResult($"Staged candidate file is missing: {record.StagedFilePath}");
        }

        // Operator-authorized write of the reviewed staged bytes into the watched tree.
        // This is the in-app equivalent of the WinMerge "take proposed" save.
        string? watchedDirectory = Path.GetDirectoryName(record.WatchedFilePath);
        if (!string.IsNullOrEmpty(watchedDirectory))
        {
            Directory.CreateDirectory(watchedDirectory);
        }

        File.Copy(record.StagedFilePath, record.WatchedFilePath, overwrite: true);

        workspace.EditService.RecordDecision(stagedRecordId, "accepted", record.StagedHash);
        return new ReviewActionResult($"Accepted. {record.RelativePath} written to the watched solution.");
    }

    public ReviewActionResult Reject(string stagedRecordId)
    {
        StagedEditRecord record = workspace.EditService.GetStagedRecord(stagedRecordId);
        workspace.EditService.RecordDecision(stagedRecordId, "rejected");

        int alsoRejected = 0;
        if (!string.IsNullOrWhiteSpace(record.SessionId))
        {
            foreach (StagedEditRecord other in workspace.EditService.ListStagedRecords(record.SessionId))
            {
                if (!string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal) && IsPending(other))
                {
                    workspace.EditService.RecordDecision(other.StagedRecordId, "rejected");
                    alsoRejected++;
                }
            }
        }

        return new ReviewActionResult(alsoRejected > 0
            ? $"Rejected. Edit session stopped; {alsoRejected + 1} staged file(s) rejected."
            : "Rejected.");
    }

    private void EnsureValidatedAndLaunched(StagedEditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PreMergeValidationStatus))
        {
            workspace.EditService.PrepareReviewFileForLaunch(record.StagedRecordId);
            PreMergeValidationResult validation = new PreMergeValidationService().Validate(workspace.Settings, record);
            workspace.EditService.RecordPreMergeValidation(record.StagedRecordId, validation, forceApproved: false);
            diagnosticsCache[record.StagedRecordId] = validation.Diagnostics;
        }

        StagedEditRecord current = workspace.EditService.GetStagedRecord(record.StagedRecordId);
        if (!string.Equals(current.LaunchStatus, "launched", StringComparison.OrdinalIgnoreCase))
        {
            workspace.EditService.RecordDiffLaunch(record.StagedRecordId, launched: true, "in-app merge review");
        }
    }

    private string[] DiagnosticsFor(StagedEditRecord record)
    {
        if (diagnosticsCache.TryGetValue(record.StagedRecordId, out string[]? cached))
        {
            return cached;
        }

        if (record.PreMergeValidationIsError)
        {
            PreMergeValidationResult validation = new PreMergeValidationService().Validate(workspace.Settings, record);
            diagnosticsCache[record.StagedRecordId] = validation.Diagnostics;
            return validation.Diagnostics;
        }

        return [];
    }

    private static bool IsPending(StagedEditRecord record)
    {
        return string.IsNullOrWhiteSpace(record.Decision)
            && string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId);
    }

    private static ReviewQueueItem ToQueueItem(StagedEditRecord record)
    {
        return new ReviewQueueItem
        {
            StagedRecordId = record.StagedRecordId,
            RelativePath = record.RelativePath,
            SessionId = record.SessionId,
            CreatedAtUtc = record.CreatedAtUtc,
            LaunchStatus = record.LaunchStatus,
            PreMergeValidationStatus = record.PreMergeValidationStatus,
        };
    }

    private ReviewRecordModel ToModel(StagedEditRecord record)
    {
        string proposedText = File.Exists(record.StagedFilePath)
            ? File.ReadAllText(record.StagedFilePath)
            : string.Empty;

        return new ReviewRecordModel
        {
            PreMergeValidationDiagnostics = DiagnosticsFor(record),
            StagedRecordId = record.StagedRecordId,
            RelativePath = record.RelativePath,
            SessionId = record.SessionId,
            DecisionStatus = string.IsNullOrWhiteSpace(record.Decision)
                ? (string.IsNullOrWhiteSpace(record.Status) ? "pending" : record.Status)
                : record.Decision,
            CurrentText = ReadCurrentText(record),
            ProposedText = proposedText,
            IsDecided = !string.IsNullOrWhiteSpace(record.Decision),
            IsNewFile = record.IsNewFile,
            IsSessionComplete = false,
            PreMergeValidationIsError = record.PreMergeValidationIsError,
            PreMergeValidationForceApproved = record.PreMergeValidationForceApproved,
            PreMergeValidationDiagnosticCount = record.PreMergeValidationDiagnosticCount,
            PreMergeValidationStatus = record.PreMergeValidationStatus,
        };
    }

    private static string ReadCurrentText(StagedEditRecord record)
    {
        if (!record.IsNewFile && File.Exists(record.ReviewBaselineFilePath))
        {
            return File.ReadAllText(record.ReviewBaselineFilePath);
        }

        return File.Exists(record.WatchedFilePath) ? File.ReadAllText(record.WatchedFilePath) : string.Empty;
    }
}
