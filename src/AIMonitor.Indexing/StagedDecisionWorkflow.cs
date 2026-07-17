using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.Workflow;

namespace AIMonitor.Indexing;

public sealed class StagedDecisionWorkflow
{
    public ReviewDecisionWithIndexRefreshResult Record(
        MonitorSettings settings,
        IMonitorLogger logger,
        WorkflowEditService workflowService,
        string stagedRecordId,
        string decision,
        string? expectedStagedHash,
        string source,
        bool deferIndexRefresh = false,
        PostAcceptIndexRefreshPlan? refreshPlan = null,
        bool verbose = false,
        IReadOnlyList<StagedEditRecord>? terminalValidationRecords = null)
    {
        StagedEditRecord existing = workflowService.GetStagedRecord(stagedRecordId);
        WorkflowEditService.EnsureRecordNotDecided(existing);
        PreMergeValidationResult? terminalValidation = ValidateTerminalPlannedOverlay(
            settings,
            existing,
            refreshPlan,
            deferIndexRefresh,
            terminalValidationRecords);

        StagedEditRecord record = workflowService.RecordDecision(stagedRecordId, decision, expectedStagedHash);
        PostAcceptIndexRefreshResult? indexRefresh = null;
        if (record.Classification is "accepted" or "accepted-normalized")
        {
            indexRefresh = deferIndexRefresh
                ? PostAcceptIndexRefreshService.DeferredUntilPlannedFilesComplete()
                : new PostAcceptIndexRefreshService().RebuildAfterAcceptedDecision(
                    settings,
                    logger,
                    record,
                    source,
                    refreshPlan);
        }
        else if (!deferIndexRefresh && refreshPlan is not null && refreshPlan.ChangedFilePaths.Count > 0)
        {
            indexRefresh = new PostAcceptIndexRefreshService().RebuildAfterAcceptedDecision(
                settings,
                logger,
                record,
                source,
                refreshPlan);
        }

        StagedEditSummary summary = workflowService.CreateSummary(record);
        return new ReviewDecisionWithIndexRefreshResult
        {
            StagedRecordId = record.StagedRecordId,
            WatchedFilePath = record.WatchedFilePath,
            RelativePath = record.RelativePath,
            Decision = record.Decision,
            Classification = record.Classification,
            Status = record.Status,
            Message = record.Message,
            StagedRecordSummary = summary,
            StagedRecordPath = summary.RecordPath,
            StagedRecord = verbose ? record : null,
            IndexRefresh = indexRefresh,
            TerminalPreMergeValidation = terminalValidation,
            NextStep = CreateNextStep(record, indexRefresh)
        };
    }

    private static PreMergeValidationResult? ValidateTerminalPlannedOverlay(
        MonitorSettings settings,
        StagedEditRecord currentRecord,
        PostAcceptIndexRefreshPlan? refreshPlan,
        bool deferIndexRefresh,
        IReadOnlyList<StagedEditRecord>? terminalValidationRecords)
    {
        if (deferIndexRefresh
            || refreshPlan is null
            || refreshPlan.ChangedFilePaths.Count == 0
            || terminalValidationRecords is null
            || terminalValidationRecords.Count == 0)
        {
            return null;
        }

        PreMergeValidationResult validation = new PreMergeValidationService().Validate(
            settings,
            currentRecord,
            terminalValidationRecords);
        if (validation.IsError && !currentRecord.PreMergeValidationForceApproved)
        {
            // Parity with the single-file launch gate (WorkflowEditService accept check): a failed pre-merge build is a
            // hard stop UNLESS the operator explicitly approved the override before launch. When force-approved, the
            // failure is recorded (carried on the returned result) rather than thrown so the terminal decision can proceed.
            throw new InvalidOperationException(
                "Terminal planned pre-merge validation failed before recording the final session decision: "
                + validation.Message);
        }

        return validation;
    }

    private static string CreateNextStep(StagedEditRecord record, PostAcceptIndexRefreshResult? indexRefresh)
    {
        if (indexRefresh?.IsError == true)
        {
            return "Accept recorded, but the index rebuild failed. Index rows are stale. Re-run refresh_solution_index before trusting index queries.";
        }

        if (indexRefresh?.Status.Equals("deferred", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Decision recorded. Index refresh is deferred until all declared session edit files are decided.";
        }

        return record.Classification is "accepted" or "accepted-normalized"
            ? "Index was rebuilt after accept. Run edit refresh before further edits to this watched file."
            : "Decision recorded. Do not rely on changed index rows unless an accepted decision rebuilt the index.";
    }
}
