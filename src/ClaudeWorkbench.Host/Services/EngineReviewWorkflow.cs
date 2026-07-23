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
// check, and on the TERMINAL accept the authoritative GATE 2 build runs FIRST — only if it
// passes are the staged bytes written to the watched files here (host-side,
// operator-authorized) and the decisions recorded. The agent never writes watched source.
//
// ADR-0005 (docs/decisions/0005-edit-session-is-atomic.md): the edit SESSION is the atomic
// unit of decision. A per-file accept before the last file is an approval that writes
// nothing; the terminal accept writes every approved file in the session together, after the
// combined-overlay build passes; and a single reject voids the session, leaving watched
// source untouched. Half a refactor is not a smaller refactor — it is broken source.
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

    public ReviewActionResult Accept(string stagedRecordId, bool forceApproveValidation, bool rebuildIndex = true)
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

            // Force-approval records the operator's deliberate choice to merge despite the
            // ALREADY-stamped validation failure. Do NOT re-run the build here: the record already
            // carries its validation result, and the terminal accept re-validates the whole set (the
            // authoritative gate). Re-building per force-approved file made every override click run a
            // full dotnet build — a redundant accept-spinner flash on each file of the session.
            record = workspace.EditService.ForceApprovePreMergeValidation(stagedRecordId);
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

        // Terminal = last pending file in the session. ADR-0005: the edit session is the
        // atomic unit, so ONLY the terminal accept runs the build and ONLY the terminal accept
        // writes — and it writes every approved-but-unwritten file in the session at once.
        // A non-terminal accept is an approval and touches no file on disk.
        bool terminal = true;
        int remainingPending = 0;
        // Ordered oldest-first so the write order is deterministic and matches review order.
        List<StagedEditRecord> pendingWrites = new() { record };
        if (!string.IsNullOrWhiteSpace(record.SessionId))
        {
            IReadOnlyList<StagedEditRecord> sessionRecords = workspace.EditService.ListStagedRecords(record.SessionId);
            remainingPending = sessionRecords.Count(other =>
                !string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal) && IsPending(other));
            terminal = remainingPending == 0;
            if (terminal)
            {
                // The write set: everything the operator approved earlier in this session that
                // has not yet reached watched source, plus this file. Written-ness is read from
                // the recorded WrittenAtUtc stamp, never inferred from the decision, so a retry
                // after a partial write resumes instead of rewriting.
                pendingWrites = sessionRecords
                    .Where(other => !string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal)
                        && IsApprovedAndUnwritten(other))
                    .Append(record)
                    .OrderBy(other => other.CreatedAtUtc, StringComparer.Ordinal)
                    .ToList();
            }
        }

        if (!terminal)
        {
            // Approval only: record the decision so the review advances to the next file, and
            // write NOTHING. There is deliberately no agent summary here — nothing has
            // happened to the agent's code yet, and the terminal accept reports for the set.
            try
            {
                workspace.EditService.RecordSessionApproval(stagedRecordId, record.StagedHash);
            }
            catch (Exception exception)
            {
                return new ReviewActionResult($"Accept failed while approving {record.RelativePath}: {exception.Message}");
            }

            // Keep the leading "Accepted" verb: this IS the operator's accept, and callers key
            // off it. The rest of the sentence has to be unambiguous that no bytes moved.
            return new ReviewActionResult(
                $"Accepted {record.RelativePath} — NOTHING has been written to source yet. "
                + $"The edit session writes every approved file together after the last one passes the build; "
                + $"{remainingPending} file(s) still to review.");
        }

        // --- H2 + write-before-build: run the authoritative GATE-2 build BEFORE writing ---
        // Compile the combined session overlay (a real dotnet build, not the hash-only
        // readiness check GATE 1 runs). A failed build is a hard stop unless the operator
        // overrode validation. The overlay is now exactly the set about to be written, so
        // what is validated is what is written.
        PreMergeValidationResult terminalBuild;
        try
        {
            terminalBuild = new PreMergeValidationService().Validate(workspace.Settings, record, pendingWrites);
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

        // A sessionless record still carries no refresh plan, exactly as before — the plan is a
        // session concept, and the index service has its own single-file path for the rest.
        PostAcceptIndexRefreshPlan? refreshPlan = string.IsNullOrWhiteSpace(record.SessionId)
            ? null
            : new PostAcceptIndexRefreshPlan
            {
                ChangedFilePaths = pendingWrites
                    .Select(pending => pending.WatchedFilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

        // --- operator-authorized write of the whole approved session ---
        // Writing N files is NOT one transaction and this does not pretend to be: each file is
        // written to a sibling temp then atomically renamed, so no single file can be left
        // truncated and the window in which the set is half-applied is as small as it can be.
        // If a write fails partway, we stop and report exactly which files reached source.
        List<string> writtenPaths = new();
        foreach (StagedEditRecord pending in pendingWrites)
        {
            string tempPath = pending.WatchedFilePath + ".cwb-accept-tmp";
            try
            {
                string? watchedDirectory = Path.GetDirectoryName(pending.WatchedFilePath);
                if (!string.IsNullOrEmpty(watchedDirectory))
                {
                    Directory.CreateDirectory(watchedDirectory);
                }

                File.Copy(pending.StagedFilePath, tempPath, overwrite: true);
                File.Move(tempPath, pending.WatchedFilePath, overwrite: true);
                // Stamp written-ness immediately, before anything else can fail, so the record
                // never claims less than what is on disk.
                workspace.EditService.MarkWrittenToWatchedSource(pending.StagedRecordId);
                writtenPaths.Add(pending.RelativePath);
            }
            catch (Exception exception)
            {
                TryDeleteTemp(tempPath);
                string[] notWritten = pendingWrites
                    .Select(other => other.RelativePath)
                    .Where(path => !writtenPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                return new ReviewActionResult(
                    $"Accept FAILED while writing {pending.RelativePath}: {exception.Message} "
                    + $"This was not a transaction. WRITTEN to source: {DescribePaths(writtenPaths)}. "
                    + $"NOT written: {DescribePaths(notWritten)}. "
                    + "Accept the final file again to write the remainder, or restore the written files from source control.");
            }
        }

        try
        {
            // Record the decisions now that the bytes are on disk — RecordDecision classifies by
            // comparing watched source to the staged candidate, so it must run after the write.
            // The terminal build already ran above (pre-write), so we do NOT pass
            // terminalValidationRecords here (it would re-run the build). Earlier-approved files
            // defer their index refresh; the terminal record carries the plan for the whole set.
            foreach (StagedEditRecord pending in pendingWrites)
            {
                if (string.Equals(pending.StagedRecordId, stagedRecordId, StringComparison.Ordinal))
                {
                    continue;
                }

                new StagedDecisionWorkflow().Record(
                    workspace.Settings,
                    logger,
                    workspace.EditService,
                    pending.StagedRecordId,
                    "accepted",
                    pending.StagedHash,
                    "ClaudeWorkbench",
                    deferIndexRefresh: true);
            }

            // The terminal accept is the ONLY place the session's index refresh happens. When the
            // operator unchecks "rebuild index" (honored only here, on the terminal file), defer it:
            // the bytes are already on disk, but the index is left stale until the next reindex.
            // deferIndexRefresh drives BOTH paths — the session refreshPlan and the single-file path.
            ReviewDecisionWithIndexRefreshResult decisionResult = new StagedDecisionWorkflow().Record(
                workspace.Settings,
                logger,
                workspace.EditService,
                stagedRecordId,
                "accepted",
                record.StagedHash,
                "ClaudeWorkbench",
                deferIndexRefresh: !rebuildIndex,
                refreshPlan: rebuildIndex ? refreshPlan : null);

            string indexNote = rebuildIndex
                ? "index rebuilt for the edit session"
                : "index refresh DEFERRED — the change is on disk but the index is stale until the next reindex";
            string message = writtenPaths.Count == 1
                ? $"Accepted. {record.RelativePath} written; {indexNote}."
                : $"Accepted. Edit session complete: {writtenPaths.Count} file(s) written ({DescribePaths(writtenPaths)}); {indexNote}.";
            return new ReviewActionResult(message, BuildOutcomeSummary(decisionResult, writtenPaths, terminalBuild, rebuildIndex));
        }
        catch (Exception exception)
        {
            // The bytes ARE on disk at this point; only the bookkeeping failed. Say so.
            return new ReviewActionResult(
                $"Accept wrote {DescribePaths(writtenPaths)} to source, but recording the decision failed: {exception.Message} "
                + "Watched source is changed; the staged records may not reflect it and the index may be stale.");
        }
    }

    // A file the operator approved earlier in this session whose bytes have not reached
    // watched source. Written-ness comes from the recorded stamp, not from the decision.
    private static bool IsApprovedAndUnwritten(StagedEditRecord record)
    {
        return record.Decision.Equals("approved", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(record.WrittenAtUtc)
            && string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId);
    }

    private static string DescribePaths(IReadOnlyCollection<string> paths)
    {
        return paths.Count == 0 ? "(none)" : string.Join(", ", paths);
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

    // Concise compilation + index outcome echoed back to the agent. Only the terminal accept
    // ran the build and index rebuild, so only it reports — and because the session now writes
    // as a set, it reports the WHOLE set that reached watched source, not just the last file.
    private static string? BuildOutcomeSummary(
        ReviewDecisionWithIndexRefreshResult result,
        IReadOnlyCollection<string> writtenPaths,
        PreMergeValidationResult? terminalBuild,
        bool rebuildIndex)
    {
        List<string> parts = new()
        {
            writtenPaths.Count <= 1
                ? $"Accepted {result.RelativePath} ({result.Classification})."
                : $"Accepted the edit session: {writtenPaths.Count} file(s) written to source together ({DescribePaths(writtenPaths)}); final file {result.RelativePath} ({result.Classification})."
        };
        // terminalBuild is the pre-write GATE-2 build this class now runs; fall back to the
        // engine's own terminal validation if a caller ever supplies it there instead.
        if ((terminalBuild ?? result.TerminalPreMergeValidation) is PreMergeValidationResult build)
        {
            parts.Add(build.IsError
                ? $"Build FAILED with {build.DiagnosticCount} error(s): {string.Join(" | ", build.Diagnostics.Take(5))}"
                : "Build passed.");
        }

        // The operator unchecked "rebuild index", so the refresh was intentionally deferred — the
        // decision result carries a no-op IndexRefresh, but reporting it as "refreshed" would lie.
        if (!rebuildIndex)
        {
            parts.Add("Index refresh DEFERRED (operator choice) — this file is stale in the index until the next reindex.");
        }
        else if (result.IndexRefresh is PostAcceptIndexRefreshResult index)
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

        // ADR-0005: a single reject VOIDS THE WHOLE SESSION. That means the still-pending
        // files (never reviewed) AND the files the operator approved earlier in this session.
        // The approved ones were never written — approval defers the write to the terminal
        // accept — so there is nothing to roll back here, only a decision to flip.
        int alsoRejected = 0;
        if (!string.IsNullOrWhiteSpace(record.SessionId))
        {
            foreach (StagedEditRecord other in workspace.EditService.ListStagedRecords(record.SessionId))
            {
                if (string.Equals(other.StagedRecordId, stagedRecordId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsPending(other) || IsApprovedAndUnwritten(other))
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
