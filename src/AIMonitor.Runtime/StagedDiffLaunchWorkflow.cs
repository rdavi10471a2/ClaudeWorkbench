using AIMonitor.Core;
using AIMonitor.Logging;
using AIMonitor.Workflow;

namespace AIMonitor.Runtime;

public sealed class StagedDiffLaunchWorkflow
{
    public StagedDiffLaunchWorkflowResult Launch(
        MonitorSettings settings,
        IMonitorLogger logger,
        WorkflowEditService workflowService,
        string stagedRecordId,
        string source,
        string? diffToolPath = null,
        bool forceValidation = false,
        bool deferBuildValidationUntilAccept = false,
        bool verbose = false)
    {
        StagedEditRecord record = workflowService.GetStagedRecord(stagedRecordId);
        WorkflowEditService.EnsureRecordNotDecided(record);

        IReadOnlyList<StagedEditRecord> stagedOverlayRecords = GetStagedOverlayRecords(workflowService, record);
        PreMergeValidationService validationService = new();
        // Fidelity fix (option A): when the launch is for a planned session whose
        // batch is fully staged (deferBuildValidationUntilAccept is only set true by the
        // caller once every planned file is decided-or-staged), run the FULL overlay build
        // here so the staged batch is build-validated BEFORE any WinMerge merge. The terminal
        // real-tree build still runs at the final accept. The single-file (non-deferred) path
        // also runs the full overlay build, so both launch paths now build before merge.
        PreMergeValidationResult validation = validationService.Validate(settings, record, stagedOverlayRecords);
        string validationPrompt = "";
        if (validation.IsError && !forceValidation && PreMergeValidationOverridePrompt.CanShow())
        {
            forceValidation = PreMergeValidationOverridePrompt.Prompt(validation.Diagnostics);
            validationPrompt = forceValidation ? "approved" : "cancelled";
        }

        record = workflowService.RecordPreMergeValidation(record.StagedRecordId, validation, forceValidation);
        logger.Write(
            validation.IsError ? MonitorLogLevel.Warning : MonitorLogLevel.Information,
            source,
            "premerge.validation.completed",
            validation.Message,
            new Dictionary<string, string>
            {
                ["stagedRecordId"] = record.StagedRecordId,
                ["watchedFilePath"] = record.WatchedFilePath,
                ["relativePath"] = record.RelativePath,
                ["validationStatus"] = validation.Status,
                ["diagnosticCount"] = validation.DiagnosticCount.ToString(),
                ["validationWorkspacePath"] = validation.ValidationWorkspacePath,
                ["forceValidation"] = forceValidation.ToString().ToLowerInvariant(),
                ["validationPrompt"] = validationPrompt,
                ["isError"] = validation.IsError.ToString().ToLowerInvariant()
            });

        if (validation.IsError && !forceValidation)
        {
            StagedEditRecord blocked = workflowService.RecordDiffLaunch(
                record.StagedRecordId,
                launched: false,
                "Pre-merge validation failed. WinMerge launch is blocked unless force validation is used after human approval.");
            return new StagedDiffLaunchWorkflowResult
            {
                StagedRecordSummary = workflowService.CreateSummary(blocked),
                StagedRecord = verbose ? blocked : null,
                PreMergeValidation = validation,
                DiffLaunch = new DiffLaunchResult
                {
                    Launched = false,
                    Tool = "WinMerge",
                    ToolPath = string.Empty,
                    ProcessId = 0,
                    Message = "Pre-merge validation failed. Human approval is required before force-launching WinMerge."
                },
                NextStep = PreMergeValidationOverridePrompt.CanShow()
                    ? "Human cancelled validation override. Fix and restage before launching WinMerge."
                    : "Validation failed and no interactive dialog is available. Ask the user whether to override; rerun with force validation only after explicit approval."
            };
        }

        record = workflowService.PrepareReviewFileForLaunch(record.StagedRecordId);
        DiffLaunchResult launch = new WinMergeDiffToolLauncher().Launch(new DiffLaunchRequest
        {
            OriginalFilePath = string.IsNullOrWhiteSpace(record.ReviewBaselineFilePath)
                ? record.WatchedFilePath
                : record.ReviewBaselineFilePath,
            ProposedFilePath = record.StagedFilePath,
            ExplicitToolPath = diffToolPath,
            CandidateToolPaths = settings.WinMergeCandidatePaths
        });
        StagedEditRecord updated = workflowService.RecordDiffLaunch(record.StagedRecordId, launch.Launched, launch.Message);
        return new StagedDiffLaunchWorkflowResult
        {
            StagedRecordSummary = workflowService.CreateSummary(updated),
            StagedRecord = verbose ? updated : null,
            PreMergeValidation = validation,
            DiffLaunch = launch,
            NextStep = record.IsNewFile
                ? "After WinMerge review, save the staged candidate into watched source for accept, or leave watched source absent for reject. Then record the diff decision."
                : "After WinMerge review, save the staged candidate into the watched source for accept, or leave watched source unchanged for reject. Then record the diff decision."
        };
    }

    private static IReadOnlyList<StagedEditRecord> GetStagedOverlayRecords(
        WorkflowEditService workflowService,
        StagedEditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SessionId))
        {
            return [record];
        }

        return workflowService.ListStagedRecords(record.SessionId)
            .Where(IsActiveOverlayRecord)
            .Append(record)
            .GroupBy(item => Path.GetFullPath(item.WatchedFilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.CreatedAtUtc, StringComparer.Ordinal).First())
            .ToArray();
    }

    private static bool IsActiveOverlayRecord(StagedEditRecord record)
    {
        return string.IsNullOrWhiteSpace(record.Decision)
            && string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId)
            && !record.Status.Equals("superseded", StringComparison.OrdinalIgnoreCase)
            && !record.Classification.Equals("superseded", StringComparison.OrdinalIgnoreCase);
    }
}
