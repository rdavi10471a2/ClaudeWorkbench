using System.Collections.Concurrent;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Console;
using ClaudeWorkbench.Host.Console.Models;

namespace ClaudeWorkbench.Host.Services;

// In-process adapter over the AIMonitor workflow engine. Replaces the external
// diff-tool step entirely: review is in-app (DiffPlex). GATE 1 runs as a readiness
// check, and on accept the authoritative GATE 2 build runs FIRST — only if it passes
// are the staged bytes written to the watched file here (host-side, operator-authorized)
// and the decision recorded. The agent never writes watched source.
public sealed class EngineReviewWorkflow : IReviewWorkflow
{
    private readonly WorkspaceManager workspace;
    private readonly IMonitorLogger logger;
    private readonly ConcurrentDictionary<string, string[]> diagnosticsCache = new();

    public EngineReviewWorkflow(WorkspaceManager workspace, IMonitorLogger logger)
    {
        this.workspace = workspace;
        this.logger = logger;
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
                    "Pre-merge validation reported errors. Use Accept With Validation Override to merge despite the failure.",
                    overrideAvailable: true);
            }

            PreMergeValidationResult revalidation = new PreMergeValidationService().Validate(workspace.Settings, record);
            workspace.EditService.RecordPreMergeValidation(stagedRecordId, revalidation, forceApproved: true);
            record = workspace.EditService.GetStagedRecord(stagedRecordId);
        }

        // --- H1: validate the staged record BEFORE writing watched source ---
        // The record must still be pending. If it was superseded by a newer candidate or
        // already decided, accepting it would overwrite watched source with stale/wrong
        // bytes and record nothing. (RecordDecision enforces this too, but only AFTER the
        // write in the old ordering — the write must not happen first.)
        if (!IsPending(record))
        {
            return new ReviewActionResult(
                $"Cannot accept {record.RelativePath}: this staged record is no longer pending (superseded or already decided). Re-stage the current candidate.");
        }

        if (!File.Exists(record.StagedFilePath))
        {
            return new ReviewActionResult($"Staged candidate file is missing: {record.StagedFilePath}");
        }

        // The diff-stable guarantee: the staged snapshot must be byte-identical to what was
        // hashed at staging time, so "what was reviewed is what gets written". Re-hash and
        // verify BEFORE touching watched source.
        if (!FileHash.Compute(record.StagedFilePath).Equals(record.StagedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewActionResult(
                $"Cannot accept {record.RelativePath}: the staged candidate changed after staging. Refresh, reapply the edit, and stage again.");
        }

        // Terminal = last pending file in the session. Only the terminal accept runs the
        // full build + reindex over every accepted file in the session.
        bool terminal = true;
        PostAcceptIndexRefreshPlan? refreshPlan = null;
        List<StagedEditRecord> overlayRecords = new() { record };
        if (!string.IsNullOrWhiteSpace(record.SessionId))
        {
            IReadOnlyList<StagedEditRecord> sessionRecords = workspace.EditService.ListStagedRecords(record.SessionId);
            terminal = !sessionRecords.Any(other =>
                !string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal) && IsPending(other));
            StagedEditRecord[] acceptedOthers = sessionRecords
                .Where(other => !string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal)
                    && (other.Classification is "accepted" or "accepted-normalized"))
                .ToArray();
            if (terminal)
            {
                refreshPlan = new PostAcceptIndexRefreshPlan
                {
                    ChangedFilePaths = acceptedOthers
                        .Select(other => other.WatchedFilePath)
                        .Append(record.WatchedFilePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
                overlayRecords.AddRange(acceptedOthers);
            }
        }

        // --- H2 + write-before-build: run the authoritative GATE-2 build BEFORE writing ---
        // On the terminal accept, compile the combined session overlay (a real dotnet build,
        // not the hash-only readiness check GATE 1 runs). A failed build is a hard stop
        // unless the operator overrode validation. The old in-app path never ran this build.
        PreMergeValidationResult? terminalBuild = null;
        if (terminal)
        {
            try
            {
                terminalBuild = new PreMergeValidationService().Validate(workspace.Settings, record, overlayRecords);
            }
            catch (Exception exception)
            {
                return new ReviewActionResult($"Terminal build validation could not run for {record.RelativePath}: {exception.Message}");
            }

            if (terminalBuild.IsError && !record.PreMergeValidationForceApproved && !forceApproveValidation)
            {
                string detail = terminalBuild.DiagnosticCount > 0
                    ? string.Join(" | ", terminalBuild.Diagnostics.Take(5))
                    : terminalBuild.Message;
                return new ReviewActionResult(
                    $"Build FAILED for the edit session ({terminalBuild.DiagnosticCount} error(s)); not accepted. {detail} Use Accept With Validation Override to merge despite the failure.",
                    overrideAvailable: true);
            }
        }

        // --- atomic, operator-authorized write of the reviewed staged bytes ---
        // Write to a sibling temp file then atomically rename, so a crash/disk-full mid-write
        // cannot leave watched source truncated. All guards above have passed by here.
        string tempPath = record.WatchedFilePath + ".cwb-accept-tmp";
        try
        {
            string? watchedDirectory = Path.GetDirectoryName(record.WatchedFilePath);
            if (!string.IsNullOrEmpty(watchedDirectory))
            {
                Directory.CreateDirectory(watchedDirectory);
            }

            File.Copy(record.StagedFilePath, tempPath, overwrite: true);
            File.Move(tempPath, record.WatchedFilePath, overwrite: true);

            // Record the decision + reindex. The terminal build already ran above (pre-write),
            // so we do NOT pass terminalValidationRecords here (it would re-run the build).
            ReviewDecisionWithIndexRefreshResult decisionResult = new StagedDecisionWorkflow().Record(
                workspace.Settings,
                logger,
                workspace.EditService,
                stagedRecordId,
                "accepted",
                record.StagedHash,
                "ClaudeWorkbench",
                deferIndexRefresh: !terminal,
                refreshPlan: refreshPlan);

            string message = terminal
                ? $"Accepted. {record.RelativePath} written; index rebuilt for the edit session."
                : $"Accepted. {record.RelativePath} written; index rebuild deferred to the final file.";
            return new ReviewActionResult(message, BuildOutcomeSummary(decisionResult, terminal, terminalBuild));
        }
        catch (Exception exception)
        {
            TryDeleteTemp(tempPath);
            return new ReviewActionResult($"Accept failed while writing {record.RelativePath}: {exception.Message}");
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of the accept temp file.
        }
    }

    // Concise compilation + index outcome echoed back to the agent. Only the
    // terminal accept ran the build and index rebuild, so only it reports.
    private static string? BuildOutcomeSummary(
        ReviewDecisionWithIndexRefreshResult result,
        bool terminal,
        PreMergeValidationResult? terminalBuild)
    {
        if (!terminal)
        {
            return null;
        }

        List<string> parts = new() { $"Accepted {result.RelativePath} ({result.Classification})." };
        // terminalBuild is the pre-write GATE-2 build this class now runs; fall back to the
        // engine's own terminal validation if a caller ever supplies it there instead.
        if ((terminalBuild ?? result.TerminalPreMergeValidation) is PreMergeValidationResult build)
        {
            parts.Add(build.IsError
                ? $"Build FAILED with {build.DiagnosticCount} error(s): {string.Join(" | ", build.Diagnostics.Take(5))}"
                : "Build passed.");
        }

        if (result.IndexRefresh is PostAcceptIndexRefreshResult index)
        {
            parts.Add(index.IsError
                ? $"Index refresh failed: {index.Message}"
                : $"Index refreshed ({index.DocumentCount} document(s), {index.ProjectCount} project(s)).");
        }

        return string.Join(" ", parts);
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

        // The agent MUST be told, or it believes its candidate is still pending review and
        // waits (or worse, reasons as though the change landed). Accept has always reported
        // back; reject reported nothing, so a rejection was invisible to the agent.
        string agentSummary = alsoRejected > 0
            ? $"Rejected {record.RelativePath}. The edit session was stopped and {alsoRejected + 1} staged file(s) were rejected; none of them were written to watched source. Address the operator's feedback, then start a new session and re-stage."
            : $"Rejected {record.RelativePath}; it was NOT written to watched source. Address the operator's feedback, then re-stage.";

        return new ReviewActionResult(
            alsoRejected > 0
                ? $"Rejected. Edit session stopped; {alsoRejected + 1} staged file(s) rejected."
                : "Rejected.",
            agentSummary);
    }

    private void EnsureValidatedAndLaunched(StagedEditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PreMergeValidationStatus))
        {
            workspace.EditService.PrepareReviewFileForLaunch(record.StagedRecordId);
            // GATE 1 is a fast staged-overlay readiness check (no dotnet build). The
            // authoritative full build runs once at accept (GATE 2, in
            // StagedDecisionWorkflow) — running a build here too double-built every file.
            PreMergeValidationResult validation = new PreMergeValidationService().ValidateStagedOverlay(record, [record]);
            workspace.EditService.RecordPreMergeValidation(record.StagedRecordId, validation, forceApproved: false);
            diagnosticsCache[record.StagedRecordId] = validation.Diagnostics;
        }
        else
        {
            // The record already carries a verdict — staging stamped the candidate's overlay
            // COMPILE result. Prepare the review file, but do not run GATE 1 over the top:
            // its readiness check would report "staged-file-ready" and erase the compile error.
            workspace.EditService.PrepareReviewFileForLaunch(record.StagedRecordId);
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
