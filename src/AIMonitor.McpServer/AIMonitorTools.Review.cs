using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Runtime;
using AIMonitor.Workflow;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    [McpServerTool]
    [Description("Stage the current Working mirror candidate for review. This creates one immutable staged record from the completed candidate.")]
    public AIMonitorStageCandidateResult StageCandidateForReview(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string path,
        [Description("Optional compact ledger summary.")] string? ledgerSummary = null,
        [Description("Optional durable session handle.")] string? sessionId = null,
        [Description("Optional JSON manifest expressing model intent.")] string? manifestJson = null,
        [Description("Return the full staged record inline for debugging. Defaults to compact response.")] bool verbose = false)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        StagedEditRecord record = workflowService.Stage(fullPath, ledgerSummary, sessionId);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "stage-candidate-for-review", record.StagedRecordId, JsonSerializer.Serialize(record, JsonOptions));
        }

        StagedEditSummary summary = workflowService.CreateSummary(record);
        return new AIMonitorStageCandidateResult(
            summary.StagedRecordId,
            summary.StagedHash,
            summary.Status,
            summary.Classification,
            summary.RecordPath,
            summary,
            verbose ? record : null,
            "Candidate staged. Use get_staged_record for full details or launch_staged_diff for review.");
    }

    [McpServerTool]
    [Description("Legacy MCP/CLI path: classify a completed review for a staged edit. Accepted decisions require the expected staged hash. The ClaudeWorkbench host records decisions itself from the in-app Merge Review.")]
    public ReviewDecisionWithIndexRefreshResult RecordDiffDecision(
        [Description("Staged edit record id returned by stage_candidate_for_review.")] string stagedRecordId,
        [Description("Operator-reported outcome: accepted or rejected.")] string decision,
        [Description("Expected staged hash for accepted decisions.")] string? expectedStagedHash = null,
        [Description("Return the full staged record inline for debugging. Defaults to compact response.")] bool verbose = false)
    {
        runtimeState.Touch();
        PlannedSessionDecisionOptions decisionOptions = BuildPlannedSessionDecisionOptions(stagedRecordId, decision);
        return new StagedDecisionWorkflow().Record(
            settings,
            logger,
            workflowService,
            stagedRecordId,
            decision,
            expectedStagedHash,
            "AIMonitor.McpServer",
            decisionOptions.DeferIndexRefresh,
            decisionOptions.RefreshPlan,
            verbose,
            decisionOptions.TerminalValidationRecords);
    }

    [McpServerTool]
    [Description("Legacy MCP/CLI path: run pre-merge validation, then launch the configured external diff tool for a staged edit record and return review paths. The ClaudeWorkbench host reviews in-app and does not use this.")]
    public AIMonitorStagedDiffLaunchResult LaunchStagedDiff(
        [Description("Staged edit record id returned by stage_candidate_for_review.")] string stagedRecordId,
        [Description("Explicit diff tool executable path.")] string? diffToolPath = null,
        [Description("Force launch after an explicit human validation override.")] bool forceValidation = false,
        [Description("Return the full staged record inline for debugging. Defaults to compact response.")] bool verbose = false)
    {
        runtimeState.Touch();
        StagedEditRecord stagedRecord = workflowService.GetStagedRecord(stagedRecordId);
        bool deferBuildValidationUntilAccept = ShouldDeferBuildValidationUntilAccept(stagedRecord);
        StagedDiffLaunchWorkflowResult result = new StagedDiffLaunchWorkflow().Launch(
            settings,
            logger,
            workflowService,
            stagedRecordId,
            "AIMonitor.McpServer",
            diffToolPath,
            forceValidation,
            deferBuildValidationUntilAccept,
            verbose);
        return new AIMonitorStagedDiffLaunchResult(
            result.StagedRecordSummary,
            result.StagedRecord,
            result.PreMergeValidation,
            result.DiffLaunch,
            result.NextStep);
    }

    [McpServerTool]
    [Description("Return the full persisted staged edit record by id. Use after compact stage/launch/decision replies when debug detail is needed.")]
    public StagedEditRecord GetStagedRecord(
        [Description("Staged edit record id returned by stage_candidate_for_review.")] string stagedRecordId)
    {
        runtimeState.Touch();
        return workflowService.GetStagedRecord(stagedRecordId);
    }

    [McpServerTool]
    [Description("Create a proposed compare snapshot for a monitor Working file and return review paths.")]
    public CompareSnapshotResult CompareFile(
        [Description("Source file path, absolute or relative to the watched solution folder.")] string sourceFilePath,
        [Description("Optional compact ledger summary to append to the monitor-owned ledger.")] string? ledgerSummary = null,
        [Description("Refresh from source first if the Working copy is missing.")] bool refreshIfMissing = true,
        [Description("Optional durable session handle for ownership/telemetry.")] string? sessionId = null)
    {
        runtimeState.Touch();
        string path = ResolveWatchedPath(sourceFilePath);
        if (refreshIfMissing && !workflowService.GetStatus(path).WorkingFileExists)
        {
            workflowService.Refresh(path);
        }

        CompareSnapshotResult result = workflowService.Compare(path, ledgerSummary);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, "compare-file", result.WorkingFilePath, JsonSerializer.Serialize(result, JsonOptions));
        }

        return result;
    }

}
