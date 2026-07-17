using AIMonitor.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMonitor.Workflow;

public sealed class WorkflowEditService
{
    private const string NewFileHash = "<new-file>";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WorkflowEditPaths paths;
    private readonly CandidateEditValidator editValidator;

    public WorkflowEditService(MonitorSettings settings)
    {
        paths = new WorkflowEditPaths(settings);
        editValidator = new CandidateEditValidator(settings);
    }

    public EditSessionStatus Refresh(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        if (!File.Exists(fullWatchedPath))
        {
            throw new FileNotFoundException("Watched file was not found.", fullWatchedPath);
        }

        EditSessionManifest? previousManifest = LoadManifest(fullWatchedPath);
        string originalHash = FileHash.Compute(fullWatchedPath);
        DateTimeOffset refreshedAtUtc = DateTimeOffset.UtcNow;
        string retrievalBackupPath = CreateRetrievalBackup(fullWatchedPath, originalHash, refreshedAtUtc);
        string workingFilePath = paths.GetWorkingFilePath(fullWatchedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(workingFilePath) ?? ".");
        File.Copy(fullWatchedPath, workingFilePath, overwrite: true);

        EditSessionManifest manifest = new()
        {
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = workingFilePath,
            RelativePath = paths.GetRelativeWatchedPath(fullWatchedPath),
            OriginalHash = originalHash,
            OriginalNormalizedHash = FileHash.ComputeNormalizedFile(fullWatchedPath),
            RequiresRefresh = false,
            IndexStale = previousManifest?.IndexStale ?? false,
            RefreshedAtUtc = refreshedAtUtc.ToString("O"),
            LastRetrievalBackupPath = retrievalBackupPath,
            LastRetrievalBackupHash = originalHash,
            LastRetrievalBackupAtUtc = refreshedAtUtc.ToString("O")
        };
        SaveManifest(fullWatchedPath, manifest);
        return GetStatus(fullWatchedPath);
    }

    public EditSessionStatus NewFile(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        paths.GetRelativeWatchedPath(fullWatchedPath);
        if (File.Exists(fullWatchedPath))
        {
            throw new InvalidOperationException("Watched file already exists. Run edit refresh for existing files.");
        }

        string workingFilePath = paths.GetWorkingFilePath(fullWatchedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(workingFilePath) ?? ".");
        if (!File.Exists(workingFilePath))
        {
            File.WriteAllText(workingFilePath, string.Empty);
        }

        EditSessionManifest manifest = new()
        {
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = workingFilePath,
            RelativePath = paths.GetRelativeWatchedPath(fullWatchedPath),
            OriginalHash = NewFileHash,
            OriginalNormalizedHash = NewFileHash,
            IsNewFile = true,
            RequiresRefresh = false,
            RefreshedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        SaveManifest(fullWatchedPath, manifest);
        return GetStatus(fullWatchedPath);
    }

    private string CreateRetrievalBackup(string fullWatchedPath, string originalHash, DateTimeOffset capturedAtUtc)
    {
        string backupDirectory = paths.GetRetrievalBackupDirectory(fullWatchedPath);
        Directory.CreateDirectory(backupDirectory);

        string fileNameWithoutExtension = MonitorWorkspacePaths.GetSafePathSegment(
            Path.GetFileNameWithoutExtension(fullWatchedPath));
        string extension = Path.GetExtension(fullWatchedPath);
        string timestamp = capturedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmssfff'Z'");
        string backupFileName = string.IsNullOrWhiteSpace(extension)
            ? $"{fileNameWithoutExtension}_{timestamp}.bak"
            : $"{fileNameWithoutExtension}_{timestamp}{extension}.bak";
        string backupPath = Path.Combine(backupDirectory, backupFileName);

        File.Copy(fullWatchedPath, backupPath, overwrite: false);
        return backupPath;
    }

    public EditSessionStatus GetStatus(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        string workingFilePath = paths.GetWorkingFilePath(fullWatchedPath);
        EditSessionManifest? manifest = LoadManifest(fullWatchedPath);

        EditSessionStatus status = new()
        {
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = workingFilePath,
            RelativePath = paths.GetRelativeWatchedPath(fullWatchedPath),
            HasSession = manifest is not null,
            WatchedFileExists = File.Exists(fullWatchedPath),
            WorkingFileExists = File.Exists(workingFilePath),
            IsNewFile = manifest?.IsNewFile ?? false,
            RequiresRefresh = manifest?.RequiresRefresh ?? false,
            IndexStale = manifest?.IndexStale ?? false,
            OriginalHash = manifest?.OriginalHash ?? string.Empty,
            LastRetrievalBackupPath = manifest?.LastRetrievalBackupPath ?? string.Empty,
            LastRetrievalBackupHash = manifest?.LastRetrievalBackupHash ?? string.Empty,
            LastRetrievalBackupAtUtc = manifest?.LastRetrievalBackupAtUtc ?? string.Empty,
            LastDecision = manifest?.LastDecision ?? string.Empty,
            LastDecisionAtUtc = manifest?.LastDecisionAtUtc ?? string.Empty,
            LastCompareRunId = manifest?.LastCompareRunId ?? string.Empty,
            LastCompareSnapshotPath = manifest?.LastCompareSnapshotPath ?? string.Empty,
            LastLedgerPath = manifest?.LastLedgerPath ?? string.Empty,
            LastStagedRecordId = manifest?.LastStagedRecordId ?? string.Empty,
            LastStagedRecordPath = manifest?.LastStagedRecordPath ?? string.Empty,
            ManifestJson = manifest?.ManifestJson ?? string.Empty,
            OperationCount = manifest?.OperationCount ?? 0,
            SyntaxValidation = manifest?.LastSyntaxValidation,
            OverlayValidation = manifest?.LastOverlayValidation
        };

        if (status.WatchedFileExists)
        {
            status.WatchedHash = FileHash.Compute(fullWatchedPath);
        }

        if (status.WorkingFileExists)
        {
            status.StagedHash = FileHash.Compute(workingFilePath);
        }

        if (manifest is null)
        {
            status.Classification = "no-session";
            status.Message = "No edit session exists for this file. Run edit refresh first.";
            return status;
        }

        if (manifest.RequiresRefresh)
        {
            status.Classification = "refresh-required";
            status.Message = "Previous decision was accepted. Run edit refresh before editing or staging again so hashes and saved line endings reflect watched source.";
            return status;
        }

        if (manifest.IndexStale)
        {
            status.Classification = "index-stale";
            status.Message = "The watched file was accepted but the post-accept index rebuild has not completed successfully. Rebuild the solution index before trusting index queries.";
            return status;
        }

        if (!status.WorkingFileExists)
        {
            status.Classification = "missing-working";
            status.Message = "The working candidate file is missing.";
            return status;
        }

        if (!status.WatchedFileExists && !status.IsNewFile)
        {
            status.Classification = "missing-watched";
            status.Message = "The watched source file is missing.";
            return status;
        }

        if (!status.WatchedFileExists && status.IsNewFile)
        {
            status.Classification = string.IsNullOrWhiteSpace(status.StagedHash)
                ? "new-file-empty-working"
                : "new-file-pending";
            status.Message = "New-file candidate exists only in the monitor-owned Working folder.";
            return status;
        }

        ReviewDecisionResult pending = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput(
                "rejected",
                manifest.OriginalHash,
                status.StagedHash,
                status.WatchedHash,
                FileHash.ComputeNormalizedFile(fullWatchedPath),
                FileHash.ComputeNormalizedFile(workingFilePath),
                manifest.IsNewFile,
                status.WatchedFileExists));
        if (pending.Classification == "rejected")
        {
            status.Classification = status.StagedHash.Equals(manifest.OriginalHash, StringComparison.OrdinalIgnoreCase)
                ? "unchanged"
                : "pending";
            status.Message = status.Classification == "unchanged"
                ? "Working candidate matches the original watched file."
                : "Working candidate differs from the watched file and has not been accepted.";
            return status;
        }

        ReviewDecisionResult accepted = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput(
                "accepted",
                manifest.OriginalHash,
                status.StagedHash,
                status.WatchedHash,
                FileHash.ComputeNormalizedFile(fullWatchedPath),
                FileHash.ComputeNormalizedFile(workingFilePath),
                manifest.IsNewFile,
                status.WatchedFileExists));
        status.Classification = accepted.Classification;
        status.Message = accepted.Message;
        return status;
    }

    public EditSessionStatus EnsureEditableSession(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        EditSessionStatus status = GetStatus(fullWatchedPath);
        if (!status.HasSession)
        {
            return File.Exists(fullWatchedPath)
                ? Refresh(fullWatchedPath)
                : NewFile(fullWatchedPath);
        }

        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        return GetStatus(fullWatchedPath);
    }

    public EditSessionStatus WriteWorkingCandidate(
        string watchedFilePath,
        string content,
        string? manifestJson = null,
        bool validateOverlay = true)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        return WriteCandidateContent(fullWatchedPath, manifest, content, manifestJson, validateOverlay);
    }

    public ReplaceTextResult ReplaceText(
        string watchedFilePath,
        string oldText,
        string newText,
        int? expectedMatches = null,
        string? expectedWorkingHash = null,
        int? occurrenceIndex = null,
        string? manifestJson = null,
        bool validateOverlay = true)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new InvalidOperationException("Replacement old text must not be empty.");
        }

        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        string originalWorkingHash = FileHash.Compute(manifest.WorkingFilePath);
        if (!string.IsNullOrWhiteSpace(expectedWorkingHash)
            && !originalWorkingHash.Equals(expectedWorkingHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Working candidate hash does not match --expected-working-hash. Refresh status before replacing text.");
        }

        string workingText = File.ReadAllText(manifest.WorkingFilePath);
        string lineEnding = DetectDominantLineEnding(workingText);
        string normalizedOldText = NormalizeLineEndingsForFile(oldText, lineEnding);
        string normalizedNewText = NormalizeLineEndingsForFile(newText, lineEnding);
        int matchCount = CountOccurrences(workingText, oldText);
        string textToFind = oldText;
        if (matchCount == 0 && !oldText.Equals(normalizedOldText, StringComparison.Ordinal))
        {
            matchCount = CountOccurrences(workingText, normalizedOldText);
            textToFind = normalizedOldText;
        }

        if (expectedMatches.HasValue && matchCount != expectedMatches.Value)
        {
            throw new InvalidOperationException($"Replacement match count was {matchCount}, expected {expectedMatches.Value}.");
        }

        if (matchCount == 0)
        {
            throw new InvalidOperationException("Replacement old text was not found in the working candidate.");
        }

        string updatedText = occurrenceIndex.HasValue
            ? ReplaceOccurrence(workingText, textToFind, normalizedNewText, occurrenceIndex.Value)
            : workingText.Replace(textToFind, normalizedNewText, StringComparison.Ordinal);
        EditSessionStatus updatedStatus = WriteCandidateContent(fullWatchedPath, manifest, updatedText, manifestJson, validateOverlay);
        return new ReplaceTextResult
        {
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = manifest.WorkingFilePath,
            RelativePath = manifest.RelativePath,
            OldWorkingHash = originalWorkingHash,
            NewWorkingHash = FileHash.Compute(manifest.WorkingFilePath),
            LineEnding = lineEnding == "\r\n" ? "CRLF" : "LF",
            ActualMatches = matchCount,
            TotalMatchCount = matchCount,
            ReplacementCount = occurrenceIndex.HasValue ? 1 : matchCount,
            ExpectedMatches = expectedMatches,
            Changed = true,
            Message = "Working candidate text was replaced.",
            OperationCount = updatedStatus.OperationCount,
            ManifestJson = updatedStatus.ManifestJson,
            SyntaxValidation = updatedStatus.SyntaxValidation,
            OverlayValidation = updatedStatus.OverlayValidation
        };
    }

    public TextSpanResult FindTextSpan(
        string watchedFilePath,
        string findText,
        int occurrenceIndex = 0,
        string? expectedWorkingHash = null)
    {
        if (string.IsNullOrEmpty(findText))
        {
            throw new InvalidOperationException("Find text must not be empty.");
        }

        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        ValidateWorkingHash(manifest.WorkingFilePath, expectedWorkingHash);
        string text = File.ReadAllText(manifest.WorkingFilePath);
        int occurrenceCount = CountOccurrences(text, findText);
        int index = FindOccurrence(text, findText, occurrenceIndex);
        TextPosition start = GetPosition(text, index);
        TextPosition end = GetPosition(text, index + findText.Length);
        return new TextSpanResult
        {
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = manifest.WorkingFilePath,
            Text = findText,
            OccurrenceCount = occurrenceCount,
            OccurrenceIndex = occurrenceIndex,
            StartLine = start.Line,
            StartColumn = start.Column,
            EndLine = end.Line,
            EndColumn = end.Column,
            TextHash = ComputeHash(findText)
        };
    }

    public EditSessionStatus ReplaceSpan(
        string watchedFilePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        string newText,
        string? expectedWorkingHash = null,
        string? expectedOldTextHash = null,
        string? expectedOldText = null,
        string? manifestJson = null,
        bool validateOverlay = true)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        ValidateWorkingHash(manifest.WorkingFilePath, expectedWorkingHash);
        string text = File.ReadAllText(manifest.WorkingFilePath);
        int startIndex = GetIndex(text, startLine, startColumn);
        int endIndex = GetIndex(text, endLine, endColumn);
        if (endIndex < startIndex)
        {
            throw new InvalidOperationException("End span must be after start span.");
        }

        string oldText = text[startIndex..endIndex];
        if (expectedOldText is not null && !oldText.Equals(expectedOldText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Extracted old span text did not match expectedOldText.");
        }

        if (!string.IsNullOrWhiteSpace(expectedOldTextHash)
            && !ComputeHash(oldText).Equals(expectedOldTextHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Extracted old span text hash did not match expectedOldTextHash.");
        }

        string lineEnding = DetectDominantLineEnding(text);
        string updatedText = text[..startIndex] + NormalizeLineEndingsForFile(newText, lineEnding) + text[endIndex..];
        return WriteCandidateContent(fullWatchedPath, manifest, updatedText, manifestJson, validateOverlay);
    }

    public EditSessionStatus SubmitFile(
        string watchedFilePath,
        string content,
        string? manifestJson = null,
        bool validateOverlay = true)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest? manifest = LoadManifest(fullWatchedPath);
        if (manifest is null)
        {
            string workingFilePath = paths.GetWorkingFilePath(fullWatchedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(workingFilePath) ?? ".");
            if (File.Exists(fullWatchedPath))
            {
                File.Copy(fullWatchedPath, workingFilePath, overwrite: true);
                manifest = new EditSessionManifest
                {
                    WatchedFilePath = fullWatchedPath,
                    WorkingFilePath = workingFilePath,
                    RelativePath = paths.GetRelativeWatchedPath(fullWatchedPath),
                    OriginalHash = FileHash.Compute(fullWatchedPath),
                    OriginalNormalizedHash = FileHash.ComputeNormalizedFile(fullWatchedPath),
                    RequiresRefresh = false,
                    RefreshedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
            }
            else
            {
                paths.GetRelativeWatchedPath(fullWatchedPath);
                if (!File.Exists(workingFilePath))
                {
                    File.WriteAllText(workingFilePath, string.Empty);
                }

                manifest = new EditSessionManifest
                {
                    WatchedFilePath = fullWatchedPath,
                    WorkingFilePath = workingFilePath,
                    RelativePath = paths.GetRelativeWatchedPath(fullWatchedPath),
                    OriginalHash = NewFileHash,
                    OriginalNormalizedHash = NewFileHash,
                    IsNewFile = true,
                    RequiresRefresh = false,
                    RefreshedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
            }

            SaveManifest(fullWatchedPath, manifest);
        }

        EnsureSessionCanEdit(manifest);
        return WriteCandidateContent(fullWatchedPath, manifest, content, manifestJson, validateOverlay);
    }

    private EditSessionStatus WriteCandidateContent(
        string fullWatchedPath,
        EditSessionManifest manifest,
        string content,
        string? manifestJson,
        bool validateOverlay)
    {
        EditSyntaxValidationResult syntaxValidation = editValidator.ValidateSyntaxIfCSharp(fullWatchedPath, content);
        if (syntaxValidation.HasErrors)
        {
            EditSyntaxDiagnostic first = syntaxValidation.Diagnostics[0];
            throw new InvalidOperationException(
                $"C# syntax validation failed for {manifest.RelativePath} at line {first.Line}, column {first.Column}: {first.Id} {first.Message}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(manifest.WorkingFilePath) ?? ".");
        string existingText = File.Exists(manifest.WorkingFilePath)
            ? File.ReadAllText(manifest.WorkingFilePath)
            : string.Empty;
        string lineEnding = string.IsNullOrEmpty(existingText)
            ? DetectDominantLineEnding(content)
            : DetectDominantLineEnding(existingText);
        File.WriteAllText(manifest.WorkingFilePath, NormalizeLineEndingsForFile(content, lineEnding));

        EditOverlayValidationResult overlayValidation = validateOverlay
            ? editValidator.ValidateCandidateOverlayCompilation(manifest, manifest.WorkingFilePath)
            : new EditOverlayValidationResult("planned-overlay-pending", false, 0, 0, []);
        manifest.OperationCount++;
        manifest.ManifestJson = manifestJson ?? string.Empty;
        manifest.LastSyntaxValidation = syntaxValidation;
        manifest.LastOverlayValidation = overlayValidation;
        SaveManifest(fullWatchedPath, manifest);
        return GetStatus(fullWatchedPath);
    }

    public CompareSnapshotResult Compare(string watchedFilePath, string? ledgerSummary = null)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        return CreateCompareSnapshot(
            fullWatchedPath,
            manifest,
            manifest.WorkingFilePath,
            ledgerSummary);
    }

    private CompareSnapshotResult CreateCompareSnapshot(
        string fullWatchedPath,
        EditSessionManifest manifest,
        string candidateFilePath,
        string? ledgerSummary)
    {
        string runId = $"{DateTimeOffset.Now:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..44];
        WorkflowRunRecorder recorder = new(paths.HistoryRoot, runId);
        recorder.Stage(runId, "compare-start", new Dictionary<string, string>
        {
            ["watchedFile"] = fullWatchedPath,
            ["workingFile"] = manifest.WorkingFilePath,
            ["candidateFile"] = candidateFilePath
        });

        string reviewBaselineFilePath = fullWatchedPath;
        if (manifest.IsNewFile)
        {
            reviewBaselineFilePath = CreateBlankReviewBaseline(manifest.RelativePath, runId);
        }

        if (!manifest.IsNewFile && FilesAreIdentical(fullWatchedPath, candidateFilePath))
        {
            recorder.Stage(runId, "compare-skipped-identical");
            return new CompareSnapshotResult
            {
                WatchedFilePath = reviewBaselineFilePath,
                WorkingFilePath = candidateFilePath,
                RunId = runId,
                RunLogPath = recorder.RunLogPath,
                TelemetryPath = recorder.TelemetryPath,
                Classification = "no-differences",
                Message = "No differences found between watched source and working candidate.",
                HasDifferences = false
            };
        }

        string historyDirectory = Path.Combine(paths.HistoryRoot, Path.GetDirectoryName(manifest.RelativePath) ?? string.Empty);
        Directory.CreateDirectory(historyDirectory);
        string baseName = Path.GetFileNameWithoutExtension(candidateFilePath);
        string extension = Path.GetExtension(candidateFilePath);
        string proposedSnapshotPath = Path.Combine(historyDirectory, $"{baseName}_{DateTimeOffset.Now:yyyyMMdd_HHmmssfff}_{Sanitize(runId)}{extension}");
        File.Copy(candidateFilePath, proposedSnapshotPath, overwrite: false);
        string ledgerPath = new FileLedgerWriter().AppendEntry(
            paths.HistoryRoot,
            manifest.RelativePath,
            fullWatchedPath,
            candidateFilePath,
            proposedSnapshotPath,
            ledgerSummary);

        manifest.LastCompareRunId = runId;
        manifest.LastCompareSnapshotPath = proposedSnapshotPath;
        manifest.LastLedgerPath = ledgerPath;
        SaveManifest(fullWatchedPath, manifest);

        recorder.Stage(runId, "compare-snapshot-created", new Dictionary<string, string>
        {
            ["snapshot"] = proposedSnapshotPath,
            ["ledger"] = ledgerPath
        });
        recorder.Stage(runId, "compare-ready");

        return new CompareSnapshotResult
        {
            WatchedFilePath = reviewBaselineFilePath,
            WorkingFilePath = candidateFilePath,
            ProposedSnapshotPath = proposedSnapshotPath,
            LedgerPath = ledgerPath,
            RunLogPath = recorder.RunLogPath,
            TelemetryPath = recorder.TelemetryPath,
            RunId = runId,
            Classification = "compare-ready",
            Message = "Compare snapshot created.",
            HasDifferences = true
        };
    }

    public StagedEditRecord Stage(string watchedFilePath, string? ledgerSummary = null, string? sessionId = null)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest manifest = LoadManifest(fullWatchedPath)
            ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
        EnsureSessionCanEdit(manifest);
        if (!File.Exists(manifest.WorkingFilePath))
        {
            throw new FileNotFoundException("Working candidate file was not found.", manifest.WorkingFilePath);
        }

        if (!File.Exists(fullWatchedPath) && !manifest.IsNewFile)
        {
            throw new FileNotFoundException("Watched file was not found.", fullWatchedPath);
        }

        if (manifest.IsNewFile && File.Exists(fullWatchedPath))
        {
            throw new InvalidOperationException("New-file target now exists in watched source. Refresh or choose another path before staging.");
        }

        string watchedHash = File.Exists(fullWatchedPath) ? FileHash.Compute(fullWatchedPath) : string.Empty;
        if (!manifest.IsNewFile && !watchedHash.Equals(manifest.OriginalHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Watched file changed since refresh. Refresh before staging.");
        }

        if (!manifest.IsNewFile && FilesAreIdentical(fullWatchedPath, manifest.WorkingFilePath))
        {
            throw new InvalidOperationException("Working candidate matches watched source. There is nothing to stage.");
        }

        string stagedRecordId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..42];
        string stagedFilePath = paths.GetStagedFilePath(stagedRecordId, manifest.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFilePath) ?? ".");
        File.Copy(manifest.WorkingFilePath, stagedFilePath, overwrite: false);

        CompareSnapshotResult compare = CreateCompareSnapshot(
            fullWatchedPath,
            manifest,
            stagedFilePath,
            ledgerSummary);
        StagedEditRecord record = new()
        {
            StagedRecordId = stagedRecordId,
            SessionId = sessionId ?? string.Empty,
            WatchedFilePath = fullWatchedPath,
            WorkingFilePath = manifest.WorkingFilePath,
            StagedFilePath = stagedFilePath,
            RelativePath = manifest.RelativePath,
            OriginalHash = manifest.OriginalHash,
            OriginalNormalizedHash = manifest.OriginalNormalizedHash,
            IsNewFile = manifest.IsNewFile,
            ReviewBaselineFilePath = manifest.IsNewFile
                ? compare.WatchedFilePath
                : fullWatchedPath,
            StagedHash = FileHash.Compute(stagedFilePath),
            StagedNormalizedHash = FileHash.ComputeNormalizedFile(stagedFilePath),
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Status = "staged",
            Message = "Working candidate was snapshotted for WinMerge review.",
            LastCompareRunId = compare.RunId,
            LastCompareSnapshotPath = compare.ProposedSnapshotPath,
            LastLedgerPath = compare.LedgerPath
        };
        SupersedeActiveRecordsForFile(fullWatchedPath, stagedRecordId);
        SaveStagedRecord(record);

        manifest.LastStagedRecordId = stagedRecordId;
        manifest.LastStagedRecordPath = paths.GetStagedRecordPath(stagedRecordId);
        manifest.LastCompareRunId = compare.RunId;
        manifest.LastCompareSnapshotPath = compare.ProposedSnapshotPath;
        manifest.LastLedgerPath = compare.LedgerPath;
        SaveManifest(fullWatchedPath, manifest);
        return record;
    }

    public StagedEditRecord GetStagedRecord(string stagedRecordId)
    {
        return LoadStagedRecord(stagedRecordId)
            ?? throw new InvalidOperationException($"Staged edit record was not found: {stagedRecordId}");
    }

    public StagedEditSummary GetStagedSummary(string stagedRecordId)
    {
        return CreateSummary(GetStagedRecord(stagedRecordId));
    }

    public StagedEditSummary CreateSummary(StagedEditRecord record)
    {
        return new StagedEditSummary
        {
            StagedRecordId = record.StagedRecordId,
            SessionId = record.SessionId,
            WatchedFilePath = record.WatchedFilePath,
            RelativePath = record.RelativePath,
            Status = record.Status,
            SupersededByStagedRecordId = record.SupersededByStagedRecordId,
            Decision = record.Decision,
            Classification = record.Classification,
            StagedHash = record.StagedHash,
            LaunchStatus = record.LaunchStatus,
            RecordPath = paths.GetStagedRecordPath(record.StagedRecordId),
            CreatedAtUtc = record.CreatedAtUtc,
            Message = record.Message
        };
    }

    public static void EnsureRecordNotDecided(StagedEditRecord record)
    {
        if (IsSuperseded(record))
        {
            throw new InvalidOperationException($"This staged record was superseded by {record.SupersededByStagedRecordId}. Use the latest staged record for this file.");
        }

        if (IsTerminalDecision(record.Decision) || IsTerminalDecision(record.Classification))
        {
            string decision = !string.IsNullOrWhiteSpace(record.Decision)
                ? record.Decision
                : record.Classification;
            throw new InvalidOperationException($"This staged record already has a final decision ({decision}). Refresh and stage a new candidate before launching or recording again.");
        }
    }

    private static bool IsTerminalDecision(string value)
    {
        return value.Equals("accepted", StringComparison.OrdinalIgnoreCase)
            || value.Equals("accepted-normalized", StringComparison.OrdinalIgnoreCase)
            || value.Equals("rejected", StringComparison.OrdinalIgnoreCase)
            || value.Equals("superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuperseded(StagedEditRecord record)
    {
        return record.Status.Equals("superseded", StringComparison.OrdinalIgnoreCase)
            || record.Classification.Equals("superseded", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId);
    }

    public StagedEditRecord RecordDiffLaunch(string stagedRecordId, bool launched, string message)
    {
        StagedEditRecord record = GetStagedRecord(stagedRecordId);
        EnsureRecordNotDecided(record);
        record.LaunchStatus = launched ? "launched" : "not-launched";
        record.LaunchMessage = message;
        record.LaunchedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        SaveStagedRecord(record);
        return record;
    }

    public StagedEditRecord RecordPreMergeValidation(string stagedRecordId, PreMergeValidationResult validation, bool forceApproved)
    {
        StagedEditRecord record = GetStagedRecord(stagedRecordId);
        EnsureRecordNotDecided(record);
        record.PreMergeValidationStatus = validation.Status;
        record.PreMergeValidationIsError = validation.IsError;
        record.PreMergeValidationForceApproved = validation.IsError && forceApproved;
        record.PreMergeValidationDiagnosticCount = validation.DiagnosticCount;
        record.PreMergeValidationAtUtc = DateTimeOffset.UtcNow.ToString("O");
        SaveStagedRecord(record);
        return record;
    }

    public StagedEditRecord PrepareReviewFileForLaunch(string stagedRecordId)
    {
        StagedEditRecord record = GetStagedRecord(stagedRecordId);
        EnsureRecordNotDecided(record);
        if (!record.IsNewFile)
        {
            return record;
        }

        if (File.Exists(record.WatchedFilePath))
        {
            FileInfo existing = new(record.WatchedFilePath);
            if (existing.Length > 0)
            {
                throw new InvalidOperationException("New-file review target already exists in watched source and is not blank. Refresh or choose another path before launching review.");
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record.WatchedFilePath) ?? ".");
            File.WriteAllText(record.WatchedFilePath, string.Empty);
        }

        record.ReviewBaselineFilePath = record.WatchedFilePath;
        SaveStagedRecord(record);
        return record;
    }

    public StagedEditRecord RecordDecision(string stagedRecordId, string decision, string? expectedStagedHash = null)
    {
        StagedEditRecord record = GetStagedRecord(stagedRecordId);
        EnsureRecordNotDecided(record);
        if (!File.Exists(record.WatchedFilePath) && !record.IsNewFile)
        {
            throw new FileNotFoundException("Watched file was not found.", record.WatchedFilePath);
        }

        string normalizedDecision = decision.Trim().ToLowerInvariant();

        if (normalizedDecision == "accepted")
        {
            if (string.IsNullOrWhiteSpace(expectedStagedHash))
            {
                throw new InvalidOperationException("--expected-staged-hash is required when recording an accepted decision.");
            }

            if (!record.StagedHash.Equals(expectedStagedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Staged record hash does not match --expected-staged-hash.");
            }

            if (!File.Exists(record.StagedFilePath))
            {
                throw new FileNotFoundException("Staged candidate file was not found.", record.StagedFilePath);
            }

            string currentStagedHash = FileHash.Compute(record.StagedFilePath);
            if (!currentStagedHash.Equals(record.StagedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Staged candidate content changed after staging. Edit the Working file and run edit stage again.");
            }

            if (!record.LaunchStatus.Equals("launched", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cannot accept a staged record before a successful diff review launch.");
            }

            if (string.IsNullOrWhiteSpace(record.PreMergeValidationStatus))
            {
                throw new InvalidOperationException("Cannot accept a staged record before pre-merge validation has completed.");
            }

            if (record.PreMergeValidationIsError && !record.PreMergeValidationForceApproved)
            {
                throw new InvalidOperationException("Cannot accept a staged record with failed pre-merge validation unless the failure was explicitly approved before launch.");
            }
        }

        if (normalizedDecision == "rejected"
            && record.IsNewFile
            && File.Exists(record.WatchedFilePath)
            && new FileInfo(record.WatchedFilePath).Length == 0)
        {
            File.Delete(record.WatchedFilePath);
        }

        string reviewedFilePath = GetReviewedFilePath(record);
        bool reviewedFileExists = File.Exists(reviewedFilePath);
        string reviewedHash = reviewedFileExists ? FileHash.Compute(reviewedFilePath) : string.Empty;
        ReviewDecisionResult result = new ReviewDecisionClassifier().Classify(
            new ReviewDecisionInput(
                decision,
                record.OriginalHash,
                record.StagedHash,
                reviewedHash,
                reviewedFileExists ? FileHash.ComputeNormalizedFile(reviewedFilePath) : null,
                string.IsNullOrWhiteSpace(record.StagedNormalizedHash) ? null : record.StagedNormalizedHash,
                record.IsNewFile,
                reviewedFileExists));

        if (normalizedDecision == "accepted" && result.Classification == "dirty-unexpected")
        {
            throw new InvalidOperationException("Cannot accept because the watched source does not match the staged candidate. Refresh, reapply the edit, and stage again.");
        }

        record.Decision = decision;
        record.DecisionAtUtc = DateTimeOffset.UtcNow.ToString("O");
        record.Classification = result.Classification;
        record.Message = result.Message;
        record.Status = result.Classification;
        SaveStagedRecord(record);

        using (IDisposable manifestLock = AcquireManifestLock(record.WatchedFilePath))
        {
            EditSessionManifest? manifest = LoadManifest(record.WatchedFilePath);
            if (manifest is not null)
            {
                manifest.LastDecision = decision;
                manifest.LastDecisionAtUtc = record.DecisionAtUtc;
                manifest.RequiresRefresh = result.Classification is "accepted" or "accepted-normalized";
                manifest.IndexStale = result.Classification is "accepted" or "accepted-normalized";
                SaveManifest(record.WatchedFilePath, manifest);
            }
        }

        return record;
    }

    public void MarkIndexFresh(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
        EditSessionManifest? manifest = LoadManifest(fullWatchedPath);
        if (manifest is null || !manifest.IndexStale)
        {
            return;
        }

        manifest.IndexStale = false;
        SaveManifest(fullWatchedPath, manifest);
    }

    public int MarkAllIndexesFresh()
    {
        if (!Directory.Exists(paths.MetadataRoot))
        {
            return 0;
        }

        int updated = 0;
        foreach (string manifestPath in Directory.EnumerateFiles(paths.MetadataRoot, "*.json", SearchOption.AllDirectories))
        {
            EditSessionManifest? manifest;
            try
            {
                manifest = DeserializeManifestFile(manifestPath);
            }
            catch (Exception)
            {
                continue;
            }

            if (manifest is null || !manifest.IndexStale || string.IsNullOrWhiteSpace(manifest.WatchedFilePath))
            {
                continue;
            }

            string fullWatchedPath;
            try
            {
                fullWatchedPath = Path.GetFullPath(manifest.WatchedFilePath);
                paths.GetRelativeWatchedPath(fullWatchedPath);
            }
            catch (Exception)
            {
                continue;
            }

            using IDisposable manifestLock = AcquireManifestLock(fullWatchedPath);
            EditSessionManifest? currentManifest = LoadManifest(fullWatchedPath);
            if (currentManifest is null || !currentManifest.IndexStale)
            {
                continue;
            }

            currentManifest.IndexStale = false;
            SaveManifest(fullWatchedPath, currentManifest);
            updated++;
        }

        return updated;
    }

    private static string GetReviewedFilePath(StagedEditRecord record)
    {
        return record.WatchedFilePath;
    }

    public EditSessionStatus Accept(string watchedFilePath, string expectedStagedHash)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        string stagedRecordId;
        using (IDisposable manifestLock = AcquireManifestLock(fullWatchedPath))
        {
            EditSessionManifest manifest = LoadManifest(fullWatchedPath)
                ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
            if (string.IsNullOrWhiteSpace(manifest.LastStagedRecordId))
            {
                throw new InvalidOperationException("No staged record exists for this file. Run edit stage first.");
            }

            stagedRecordId = manifest.LastStagedRecordId;
        }

        StagedEditRecord record = GetStagedRecord(stagedRecordId);
        if (!record.StagedHash.Equals(expectedStagedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staged record hash does not match --expected-hash.");
        }

        RecordDecision(record.StagedRecordId, "accepted", expectedStagedHash);
        return GetStatus(fullWatchedPath);
    }

    public EditSessionStatus Reject(string watchedFilePath)
    {
        string fullWatchedPath = Path.GetFullPath(watchedFilePath);
        string stagedRecordId;
        using (IDisposable manifestLock = AcquireManifestLock(fullWatchedPath))
        {
            EditSessionManifest manifest = LoadManifest(fullWatchedPath)
                ?? throw new InvalidOperationException("No edit session exists for this file. Run edit refresh first.");
            if (string.IsNullOrWhiteSpace(manifest.LastStagedRecordId))
            {
                throw new InvalidOperationException("No staged record exists for this file. Run edit stage first.");
            }

            stagedRecordId = manifest.LastStagedRecordId;
        }

        RecordDecision(stagedRecordId, "rejected");
        return GetStatus(fullWatchedPath);
    }

    private EditSessionManifest? LoadManifest(string watchedFilePath)
    {
        string metadataPath = paths.GetMetadataPath(watchedFilePath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        return DeserializeManifestFile(metadataPath);
    }

    private static EditSessionManifest? DeserializeManifestFile(string manifestPath)
    {
        return JsonSerializer.Deserialize<EditSessionManifest>(File.ReadAllText(manifestPath), JsonOptions);
    }

    private void SaveManifest(string watchedFilePath, EditSessionManifest manifest)
    {
        string metadataPath = paths.GetMetadataPath(watchedFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath) ?? ".");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private StagedEditRecord? LoadStagedRecord(string stagedRecordId)
    {
        string recordPath = paths.GetStagedRecordPath(stagedRecordId);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<StagedEditRecord>(File.ReadAllText(recordPath), JsonOptions);
    }

    private void SaveStagedRecord(StagedEditRecord record)
    {
        string recordPath = paths.GetStagedRecordPath(record.StagedRecordId);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath) ?? ".");
        File.WriteAllText(recordPath, JsonSerializer.Serialize(record, JsonOptions));
    }

    public IReadOnlyList<StagedEditRecord> ListStagedRecords(string? sessionId = null)
    {
        if (!Directory.Exists(paths.StagedRecordsRoot))
        {
            return [];
        }

        IEnumerable<StagedEditRecord> records = Directory
            .EnumerateFiles(paths.StagedRecordsRoot, "*.json")
            .Select(path =>
            {
                try
                {
                    return JsonSerializer.Deserialize<StagedEditRecord>(File.ReadAllText(path), JsonOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            })
            .Where(record => record is not null)
            .Select(record => record!);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            records = records.Where(record => record.SessionId.Equals(sessionId, StringComparison.Ordinal));
        }

        return records
            .OrderByDescending(record => record.CreatedAtUtc, StringComparer.Ordinal)
            .ToArray();
    }

    private void SupersedeActiveRecordsForFile(string fullWatchedPath, string supersededByStagedRecordId)
    {
        foreach (StagedEditRecord record in ListStagedRecords()
            .Where(record => record.WatchedFilePath.Equals(fullWatchedPath, StringComparison.OrdinalIgnoreCase))
            .Where(record => !IsTerminalDecision(record.Decision))
            .Where(record => !IsTerminalDecision(record.Classification))
            .Where(record => !IsSuperseded(record)))
        {
            record.Status = "superseded";
            record.Classification = "superseded";
            record.SupersededByStagedRecordId = supersededByStagedRecordId;
            record.SupersededAtUtc = DateTimeOffset.UtcNow.ToString("O");
            record.Message = "A newer staged candidate for the same watched file superseded this record.";
            SaveStagedRecord(record);
        }
    }

    private IDisposable AcquireManifestLock(string watchedFilePath)
    {
        string lockPath = paths.GetMetadataPath(watchedFilePath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new ManifestLock(File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void EnsureSessionCanEdit(EditSessionManifest manifest)
    {
        if (manifest.RequiresRefresh)
        {
            throw new InvalidOperationException("Previous decision was accepted. Run edit refresh or refresh_file before editing or staging this file again.");
        }
    }

    private static bool FilesAreIdentical(string leftPath, string rightPath)
    {
        FileInfo leftInfo = new(leftPath);
        FileInfo rightInfo = new(rightPath);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        const int BufferSize = 16 * 1024;
        byte[] leftBuffer = new byte[BufferSize];
        byte[] rightBuffer = new byte[BufferSize];
        using FileStream left = File.OpenRead(leftPath);
        using FileStream right = File.OpenRead(rightPath);
        while (true)
        {
            int leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            int rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            for (int index = 0; index < leftRead; index++)
            {
                if (leftBuffer[index] != rightBuffer[index])
                {
                    return false;
                }
            }
        }
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private string CreateBlankReviewBaseline(string relativePath, string runId)
    {
        string blankPath = Path.Combine(paths.HistoryRoot, "NewFileBaselines", Sanitize(runId), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(blankPath) ?? ".");
        File.WriteAllText(blankPath, string.Empty);
        return blankPath;
    }

    private static string DetectDominantLineEnding(string text)
    {
        int crlfCount = CountOccurrences(text, "\r\n");
        string withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        int lfOnlyCount = CountOccurrences(withoutCrLf, "\n");
        return crlfCount >= lfOnlyCount ? "\r\n" : "\n";
    }

    private static string NormalizeLineEndingsForFile(string text, string lineEnding)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int count = 0;
        int startIndex = 0;
        while (true)
        {
            int index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }

    private static string ReplaceOccurrence(string text, string oldText, string newText, int occurrenceIndex)
    {
        int index = FindOccurrence(text, oldText, occurrenceIndex);
        return text[..index] + newText + text[(index + oldText.Length)..];
    }

    private static int FindOccurrence(string text, string findText, int occurrenceIndex)
    {
        if (occurrenceIndex < 0)
        {
            throw new InvalidOperationException("Occurrence index must be zero or greater.");
        }

        int start = 0;
        for (int current = 0; current <= occurrenceIndex; current++)
        {
            int index = text.IndexOf(findText, start, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"Text occurrence {occurrenceIndex} was not found.");
            }

            if (current == occurrenceIndex)
            {
                return index;
            }

            start = index + findText.Length;
        }

        throw new InvalidOperationException($"Text occurrence {occurrenceIndex} was not found.");
    }

    private static TextPosition GetPosition(string text, int index)
    {
        if (index < 0 || index > text.Length)
        {
            throw new InvalidOperationException("Text index is out of range.");
        }

        int line = 1;
        int column = 1;
        int current = 0;
        while (current < index)
        {
            if (text[current] == '\r')
            {
                if (current + 1 < text.Length && text[current + 1] == '\n' && current + 1 < index)
                {
                    current++;
                }

                line++;
                column = 1;
            }
            else if (text[current] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }

            current++;
        }

        return new TextPosition(line, column);
    }

    private static int GetIndex(string text, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            throw new InvalidOperationException("Line and column are 1-based and must be greater than zero.");
        }

        int currentLine = 1;
        int currentColumn = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if (currentLine == line && currentColumn == column)
            {
                return index;
            }

            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                currentLine++;
                currentColumn = 1;
            }
            else if (text[index] == '\n')
            {
                currentLine++;
                currentColumn = 1;
            }
            else
            {
                currentColumn++;
            }
        }

        if (currentLine == line && currentColumn == column)
        {
            return text.Length;
        }

        throw new InvalidOperationException("Line/column span is outside the working candidate.");
    }

    private static void ValidateWorkingHash(string workingFilePath, string? expectedWorkingHash)
    {
        if (!string.IsNullOrWhiteSpace(expectedWorkingHash)
            && !FileHash.Compute(workingFilePath).Equals(expectedWorkingHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Working candidate hash does not match the expected hash. Refresh status before editing.");
        }
    }

    private static string ComputeHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class ManifestLock : IDisposable
    {
        private readonly FileStream stream;

        public ManifestLock(FileStream stream)
        {
            this.stream = stream;
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }

    private sealed record TextPosition(int Line, int Column);
}
