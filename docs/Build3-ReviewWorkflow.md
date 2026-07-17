# Build 3 — governed review workflow (design, ready to implement)

Replaces the WinMerge review step with an **in-app** review of a **queue** of staged
edits. The agent stages (via the claude-workbench MCP); the operator reviews the diff
per file and accepts/rejects; accept applies the staged bytes to watched source and
records the decision in-process.

## Engine surface (verified in WorkflowEditService)
- `ListStagedRecords(null)` → ALL staged records across sessions (the queue). Filter to
  undecided (`Decision` empty, not superseded).
- `GetStagedRecord(id)` → `StagedEditRecord` with `WatchedFilePath`, `StagedFilePath`,
  `ReviewBaselineFilePath`, `WorkingFilePath`, `IsNewFile`, `StagedHash`, `Status`,
  `LaunchStatus`, `PreMergeValidation*`, `SessionId`, `RelativePath`, `Decision`.
- `PreMergeValidationService.Validate(settings, record)` → `PreMergeValidationResult`
  (`Status`, `IsError`, `DiagnosticCount`, `Diagnostics[]`, `Message`) — GATE 1 overlay,
  **no WinMerge**.
- `RecordPreMergeValidation(id, result, forceApproved)` + `RecordDiffLaunch(id, launched, msg)`
  — the state `RecordDecision` requires before an accept is allowed (`LaunchStatus=launched`
  + `PreMergeValidationStatus` set).
- `RecordDecision(id, decision, expectedStagedHash)` — classifies; **does not write**. It
  hashes the *reviewed* file (the watched file) and expects it to already equal the staged
  content (WinMerge's job). For `accepted` it also enforces: staged hash matches, staged
  file unchanged, launched, validation completed (and not a failed validation unless
  force-approved).

## The one semantic to confirm: what "Accept" does
In WinMerge, the operator can hand-merge before saving. **In-app v1 = accept applies the
staged bytes verbatim** to the watched file (the operator reviewed the diff and approved
the staged content — equivalent to WinMerge "accept all / take right"), then classifies.
No hand-editing during merge (that preserves GATE-1 validity: watched ends byte-equal to
the validated staged candidate). Editable in-browser merge is a later enhancement.

## Flow (per staged record, in-app)
1. **List** the queue (grouped by session).
2. **Diff**: DiffPlex `SideBySideDiffBuilder(baselineText, stagedText)`, where
   baseline = `ReviewBaselineFilePath` (or empty for new files) and staged =
   `StagedFilePath` contents.
3. **GATE 1 overlay**: `Validate` → `RecordPreMergeValidation` → `RecordDiffLaunch(launched)`.
   Show status/diagnostics in the pre-merge overlay.
4. **Accept**: write `StagedFilePath` → `WatchedFilePath` (create dirs for new files),
   then `RecordDecision(id, "accepted", StagedHash)`. **Reject**: `RecordDecision(id,
   "rejected")` (no write).
5. Post-accept: refresh the solution index for the changed file (GATE 2 build/refresh is a
   follow-up; MCP path uses StagedDecisionWorkflow for the planned-session index refresh —
   we can fold that in once the single-file path is proven).

## Layering
- `Console/`: `IReviewWorkflow` + neutral models `ReviewItem`, `ReviewDiff`,
  `PreMergeValidation` (no engine types in the UI).
- `Services/EngineReviewWorkflow`: in-process adapter over `WorkspaceManager.EditService`
  + `PreMergeValidationService`. The only place that writes watched source (on accept).
- UI: a **Review** surface (queue list) + `MergeReviewDialog` (DiffPlex) +
  `PreMergeValidationOverlay`. Approvals stays the gate for the *stage* call; Review is the
  accept/reject on staged records.

## Testing
Requires an actual edit turn against a **safe** watched solution (e.g. `Copy (2)`, NOT the
workbench) so staged records land in the queue — this is why edit + elicitation need to work
together to exercise the loop end-to-end.
