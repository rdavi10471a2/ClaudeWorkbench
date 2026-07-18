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

[McpServerToolType]
public sealed partial class AIMonitorTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WorkspaceManager workspace;
    private readonly AIMonitorMcpRuntimeState runtimeState;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly IMonitorLogger logger;

    public AIMonitorTools(
        WorkspaceManager workspace,
        AIMonitorMcpRuntimeState runtimeState,
        IHostApplicationLifetime applicationLifetime,
        IMonitorLogger logger)
    {
        this.workspace = workspace;
        this.runtimeState = runtimeState;
        this.applicationLifetime = applicationLifetime;
        this.logger = logger;
    }

    // Workspace-scoped services are read through the manager so the whole tool
    // surface retargets when the operator switches watched workspaces at runtime.
    private MonitorSettings settings => workspace.Settings;
    private SolutionIndexQueryService queryService => workspace.Query;
    private WorkflowEditService workflowService => workspace.EditService;
    private RoslynEditService roslynEditService => workspace.RoslynEditService;
    private WorkflowEditPaths workflowPaths => workspace.EditPaths;

    private string ComposeToolManifest()
    {
        StringBuilder builder = new();
        builder.AppendLine("# AIMonitor MCP Tool Manifest");
        builder.AppendLine();
        builder.AppendLine("This manifest is generated from the currently loaded AIMonitor MCP tool methods.");
        builder.AppendLine();
        foreach (MethodInfo method in typeof(AIMonitorTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => ToToolName(method.Name), StringComparer.Ordinal))
        {
            string toolName = ToToolName(method.Name);
            string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "No description.";
            builder.AppendLine($"## `{toolName}`");
            builder.AppendLine();
            builder.AppendLine(description);
            builder.AppendLine();
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length > 0)
            {
                builder.AppendLine("Parameters:");
                foreach (ParameterInfo parameter in parameters)
                {
                    string parameterDescription = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
                    string nullable = IsNullableParameter(parameter) ? "optional" : "required";
                    builder.AppendLine($"- `{parameter.Name}` ({parameter.ParameterType.Name}, {nullable}): {parameterDescription}");
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine("## Safety Notes");
        builder.AppendLine();
        builder.AppendLine("- Watched source is not edited directly by agents.");
        builder.AppendLine("- Existing files enter through `refresh_file`; future files enter through `new_file`.");
        builder.AppendLine("- MCP edit sessions start with `start_monitor_session` and a non-empty `filesPlanned` list.");
        builder.AppendLine("- Candidate edits happen in monitor-owned Working files.");
        builder.AppendLine("- Review uses `stage_candidate_for_review`, `launch_staged_diff`, WinMerge review/save, and `record_diff_decision`.");
        builder.AppendLine("- Planned sessions require all planned files to be staged before review launch.");
        builder.AppendLine("- Planned sessions defer the expensive build/index pass until all planned files are accepted/rejected.");
        builder.AppendLine("- Accepted decisions trigger index refresh metadata after the planned session reaches terminal decisions; refresh before editing the same watched file again.");
        return builder.ToString();
    }

    private string ComposeStagingGuide()
    {
        StringBuilder builder = new();
        builder.AppendLine("# Staging Guide (ClaudeWorkbench)");
        builder.AppendLine();
        builder.AppendLine("Use this sequence for watched-project edits. You never write watched source directly; you build and stage a candidate, and the operator reviews and accepts it in the in-app Merge Review dialog.");
        builder.AppendLine();
        builder.AppendLine("1. Check `get_self_check`, `get_workflow_status`, and `get_monitor_status` when starting a session.");
        builder.AppendLine("2. Call `start_monitor_session(filesPlanned: [...])` before editing. Include every watched file the session intends to mutate, even for one-file edits, and include `owningProjectPath` when the index cannot prove a single owner.");
        builder.AppendLine("3. Pass that same `sessionId` to `refresh_file`, `new_file`, every mutation tool, and `stage_candidate_for_review`.");
        builder.AppendLine("4. For existing files, call `refresh_file`. For future watched files, call `new_file`.");
        builder.AppendLine("5. Edit only the monitor-owned Working candidate with `submit_file`, text/span tools, or Roslyn typed edit tools.");
        builder.AppendLine("6. Stage every planned file with `stage_candidate_for_review(path, sessionId)`, then STOP.");
        builder.AppendLine("7. The operator reviews the staged diff in the ClaudeWorkbench Merge Review dialog and accepts or rejects each file. Do NOT call `launch_staged_diff` or `record_diff_decision`, and do not expect WinMerge — review, the accept-time write to watched source, and the decision record are host/operator actions in this environment.");
        builder.AppendLine("8. After the operator accepts, that file has been written to the watched solution. Call `refresh_file` before editing the same file again.");
        builder.AppendLine();
        builder.AppendLine("Notes:");
        builder.AppendLine();
        builder.AppendLine("- `blocked`, `dirty-unexpected`, `superseded`, missing Working files, and stale hashes require recovery before follow-up edits.");
        builder.AppendLine("- Pre-merge (GATE 1) validation and the full build/index (GATE 2) validation are run by the host around the operator's accept, not by you.");
        builder.AppendLine("- The operator's Accept in the Merge Review dialog is the only path candidates reach watched source. Never try to copy a candidate into watched source yourself.");
        return builder.ToString();
    }

    private AIMonitorGuardrailCheck[] BuildSelfCheckGuardrails()
    {
        string repositoryRoot = Path.GetFullPath(settings.RepositoryRoot);
        string runtimeRoot = Path.GetFullPath(settings.RuntimeRoot);
        string watchedProjectFolder = Path.GetFullPath(settings.WatchedProjectFolder);
        string workingRoot = Path.GetFullPath(workflowPaths.WorkingRoot);
        string historyRoot = Path.GetFullPath(workflowPaths.HistoryRoot);
        string stagedRoot = Path.GetFullPath(workflowPaths.StagedRoot);
        List<AIMonitorGuardrailCheck> checks =
        [
            CheckPathExists("repository-root-exists", repositoryRoot, Directory.Exists(repositoryRoot), "Repository root exists.", "Repository root is missing."),
            CheckPathExists("watched-solution-exists", settings.WatchedSolutionPath, File.Exists(settings.WatchedSolutionPath), "Watched solution/project exists.", "Watched solution/project is missing."),
            CheckPathExists("watched-project-folder-exists", watchedProjectFolder, Directory.Exists(watchedProjectFolder), "Watched project folder exists.", "Watched project folder is missing."),
            CheckPathExists("runtime-root-exists", runtimeRoot, Directory.Exists(runtimeRoot), "Runtime root exists.", "Runtime root is missing."),
            CheckPathUnderRoot("working-under-runtime", workingRoot, runtimeRoot, "Working root is under runtime root.", "Working root is outside runtime root."),
            CheckPathUnderRoot("history-under-runtime", historyRoot, runtimeRoot, "History root is under runtime root.", "History root is outside runtime root."),
            CheckPathUnderRoot("staged-under-runtime", stagedRoot, runtimeRoot, "Staged root is under runtime root.", "Staged root is outside runtime root."),
            CheckPathOutsideRoot("runtime-outside-watched-source", runtimeRoot, watchedProjectFolder, "Runtime state is outside watched source.", "Runtime state is inside watched source."),
        ];

        string? diffTool = settings.WinMergeCandidatePaths.FirstOrDefault(File.Exists);
        checks.Add(diffTool is null
            ? new AIMonitorGuardrailCheck("diff-tool-available", "warning", "No configured WinMerge candidate exists on disk.", string.Join(";", settings.WinMergeCandidatePaths))
            : new AIMonitorGuardrailCheck("diff-tool-available", "passed", "Configured WinMerge candidate exists.", diffTool));

        return checks.ToArray();
    }

    private static AIMonitorGuardrailCheck CheckPathExists(string name, string path, bool passed, string passedMessage, string failedMessage)
    {
        return new AIMonitorGuardrailCheck(name, passed ? "passed" : "failed", passed ? passedMessage : failedMessage, path);
    }

    private static AIMonitorGuardrailCheck CheckPathUnderRoot(string name, string path, string root, string passedMessage, string failedMessage)
    {
        bool passed = IsPathUnderRoot(path, root);
        return new AIMonitorGuardrailCheck(name, passed ? "passed" : "failed", passed ? passedMessage : failedMessage, path);
    }

    private static AIMonitorGuardrailCheck CheckPathOutsideRoot(string name, string path, string root, string passedMessage, string failedMessage)
    {
        bool passed = !IsPathUnderRoot(path, root);
        return new AIMonitorGuardrailCheck(name, passed ? "passed" : "failed", passed ? passedMessage : failedMessage, path);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNullableParameter(ParameterInfo parameter)
    {
        return parameter.HasDefaultValue
            || Nullable.GetUnderlyingType(parameter.ParameterType) is not null
            || !parameter.ParameterType.IsValueType;
    }

    private static string ToToolName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        StringBuilder builder = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private string SessionRoot => Path.Combine(MonitorWorkspacePaths.GetWatchedSolutionWorkspaceRoot(settings), "workflow", "sessions");

    private string ResolveWatchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(settings.WatchedProjectFolder, path));
        workflowPaths.GetRelativeWatchedPath(fullPath);
        return fullPath;
    }

    private EditSessionStatus EnsureSession(string watchedFilePath)
    {
        return workflowService.EnsureEditableSession(watchedFilePath);
    }

    private void SaveSession(AIMonitorSessionState session)
    {
        Directory.CreateDirectory(SessionRoot);
        File.WriteAllText(GetSessionPath(session.SessionId), JsonSerializer.Serialize(session, JsonOptions));
    }

    private AIMonitorSessionFileAccess RecordSessionFileAccess(
        string sessionId,
        string sourceFilePath,
        string accessKind,
        AIMonitorFileHashInfo hash)
    {
        AIMonitorSessionState session = LoadSessionById(sessionId)
            ?? throw new InvalidOperationException($"Monitor session was not found: {sessionId}");
        List<AIMonitorSessionFileAccess> files = session.Files.ToList();
        AIMonitorSessionFileAccess? previous = files
            .FirstOrDefault(item => item.SourceFilePath.Equals(sourceFilePath, StringComparison.OrdinalIgnoreCase));
        AIMonitorSessionFileAccess updated = new(
            sessionId,
            sourceFilePath,
            workflowPaths.GetRelativeWatchedPath(sourceFilePath),
            accessKind,
            hash,
            (previous?.FetchCount ?? 0) + 1,
            previous?.FirstAccessedAtUtc ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        if (previous is not null)
        {
            files.Remove(previous);
        }

        files.Add(updated);
        SaveSession(session with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = files
        });
        return updated;
    }

    private AIMonitorSessionState? LoadSessionById(string sessionId)
    {
        string path = GetSessionPath(sessionId);
        return File.Exists(path) ? LoadSession(path) : null;
    }

    private AIMonitorSessionState? LoadSession(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AIMonitorSessionState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GetSessionPath(string sessionId)
    {
        return Path.Combine(SessionRoot, $"{Sanitize(sessionId)}.json");
    }

    private void RecordRoslynSessionEvent(string? sessionId, string eventType, RoslynEditResult result)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            RecordMonitorSessionEvent(sessionId, eventType, result.WatchedFilePath, JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    private PlannedSessionDecisionOptions BuildPlannedSessionDecisionOptions(string stagedRecordId, string requestedDecision)
    {
        StagedEditRecord currentRecord = workflowService.GetStagedRecord(stagedRecordId);
        if (string.IsNullOrWhiteSpace(currentRecord.SessionId))
        {
            return new PlannedSessionDecisionOptions(false, null, []);
        }

        AIMonitorSessionEditPlan? editPlan = LoadSessionById(currentRecord.SessionId)?.EditPlan;
        if (editPlan is null || editPlan.FilesPlanned.Count == 0)
        {
            return new PlannedSessionDecisionOptions(false, null, []);
        }

        IReadOnlyList<StagedEditRecord> sessionRecords = workflowService.ListStagedRecords(currentRecord.SessionId);
        HashSet<string> terminalPlannedPaths = sessionRecords
            .Where(record => !record.StagedRecordId.Equals(stagedRecordId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(record.Decision))
            .Select(record => Path.GetFullPath(record.WatchedFilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        terminalPlannedPaths.Add(Path.GetFullPath(currentRecord.WatchedFilePath));
        bool allPlannedFilesDecided = editPlan.FilesPlanned.All(file =>
            terminalPlannedPaths.Contains(Path.GetFullPath(file.SourceFilePath)));

        string normalizedDecision = requestedDecision.Trim().ToLowerInvariant();
        HashSet<string> acceptedPaths = sessionRecords
            .Where(record => !record.StagedRecordId.Equals(stagedRecordId, StringComparison.Ordinal)
                && record.Classification is "accepted" or "accepted-normalized")
            .Select(record => Path.GetFullPath(record.WatchedFilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedDecision.Equals("accepted", StringComparison.OrdinalIgnoreCase))
        {
            acceptedPaths.Add(Path.GetFullPath(currentRecord.WatchedFilePath));
        }

        AIMonitorSessionPlannedFile[] acceptedPlannedFiles = editPlan.FilesPlanned
            .Where(file => acceptedPaths.Contains(Path.GetFullPath(file.SourceFilePath)))
            .ToArray();
        if (acceptedPlannedFiles.Length == 0)
        {
            return new PlannedSessionDecisionOptions(false, null, []);
        }

        PostAcceptIndexRefreshPlan refreshPlan = new()
        {
            ChangedFilePaths = acceptedPlannedFiles.Select(file => file.SourceFilePath).ToArray(),
            OwningProjectPaths = acceptedPlannedFiles.Select(file => file.OwningProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        StagedEditRecord[] terminalValidationRecords = !allPlannedFilesDecided
            ? []
            : sessionRecords
                .Append(currentRecord)
                .Where(record => acceptedPaths.Contains(Path.GetFullPath(record.WatchedFilePath)))
                .GroupBy(record => Path.GetFullPath(record.WatchedFilePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(record => record.CreatedAtUtc, StringComparer.Ordinal).First())
                .OrderBy(record => record.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return new PlannedSessionDecisionOptions(!allPlannedFilesDecided, refreshPlan, terminalValidationRecords);
    }

    private bool ShouldDeferPlannedOverlayValidation(string? sessionId, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        AIMonitorSessionEditPlan? editPlan = RequireSessionEditPlan(sessionId);
        EnsurePlannedFile(editPlan, sourceFilePath);
        string currentPath = Path.GetFullPath(sourceFilePath);
        bool allPlannedWorkingFilesExist = editPlan.FilesPlanned.All(file =>
        {
            string plannedPath = Path.GetFullPath(file.SourceFilePath);
            if (plannedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return File.Exists(workflowPaths.GetWorkingFilePath(plannedPath));
        });
        return !allPlannedWorkingFilesExist;
    }

    private void EnsurePlannedMutationAllowed(string? sessionId, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("Session edit scope is required before MCP workflow mutations. Call start_monitor_session with filesPlanned before editing or staging.");
        }

        AIMonitorSessionEditPlan editPlan = RequireSessionEditPlan(sessionId);
        EnsurePlannedFile(editPlan, sourceFilePath);
    }

    private bool ShouldDeferBuildValidationUntilAccept(StagedEditRecord stagedRecord)
    {
        if (string.IsNullOrWhiteSpace(stagedRecord.SessionId))
        {
            return false;
        }

        AIMonitorSessionEditPlan editPlan = RequireSessionEditPlan(stagedRecord.SessionId);
        EnsurePlannedFile(editPlan, stagedRecord.WatchedFilePath);
        IReadOnlyList<StagedEditRecord> sessionRecords = workflowService.ListStagedRecords(stagedRecord.SessionId);
        foreach (AIMonitorSessionPlannedFile plannedFile in editPlan.FilesPlanned)
        {
            string plannedPath = Path.GetFullPath(plannedFile.SourceFilePath);
            IEnumerable<StagedEditRecord> plannedFileRecords = sessionRecords.Where(record =>
                Path.GetFullPath(record.WatchedFilePath).Equals(plannedPath, StringComparison.OrdinalIgnoreCase));
            // Launch-deadlock fix: a planned file already carrying a final decision is
            // satisfied. Interleaving launch -> decide -> launch on the remaining files
            // must not throw just because an earlier file was decided and no longer has an
            // active (undecided) staged record. Only files NOT yet decided must still have
            // an active staged record.
            bool alreadyDecided = plannedFileRecords.Any(record => !string.IsNullOrWhiteSpace(record.Decision));
            if (alreadyDecided)
            {
                continue;
            }

            bool hasActiveStagedRecord = plannedFileRecords.Any(record =>
                string.IsNullOrWhiteSpace(record.Decision)
                && string.IsNullOrWhiteSpace(record.SupersededByStagedRecordId)
                && !record.Status.Equals("superseded", StringComparison.OrdinalIgnoreCase)
                && !record.Classification.Equals("superseded", StringComparison.OrdinalIgnoreCase));
            if (!hasActiveStagedRecord)
            {
                throw new InvalidOperationException("Cannot launch review until every planned session edit file has a staged record. Stage missing planned file: " + plannedFile.RelativePath);
            }
        }

        return true;
    }

    private AIMonitorSessionEditPlan RequireSessionEditPlan(string sessionId)
    {
        AIMonitorSessionState session = GetMonitorSession(sessionId);
        if (session.EditPlan is null || session.EditPlan.FilesPlanned.Count == 0)
        {
            throw new InvalidOperationException("Session edit plan is required before MCP workflow edits. Call start_monitor_session with filesPlanned before editing, staging, or launching review.");
        }

        return session.EditPlan;
    }

    private IReadOnlyList<AIMonitorSessionPlannedFile> BuildPlannedFiles(IReadOnlyList<AIMonitorSessionPlannedFileInput> filesPlanned)
    {
        if (filesPlanned.Count == 0)
        {
            throw new InvalidOperationException("At least one planned edit file is required.");
        }

        List<AIMonitorSessionPlannedFile> plannedFiles = [];
        foreach (AIMonitorSessionPlannedFileInput input in filesPlanned)
        {
            string sourceFilePath = ResolveWatchedPath(input.SourceFilePath);
            string owningProjectPath = string.IsNullOrWhiteSpace(input.OwningProjectPath)
                ? ResolveOwningProjectPath(sourceFilePath)
                : Path.GetFullPath(input.OwningProjectPath);
            if (plannedFiles.Any(file =>
                file.SourceFilePath.Equals(sourceFilePath, StringComparison.OrdinalIgnoreCase)
                && file.OwningProjectPath.Equals(owningProjectPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            plannedFiles.Add(new AIMonitorSessionPlannedFile(
                sourceFilePath,
                workflowPaths.GetRelativeWatchedPath(sourceFilePath),
                owningProjectPath,
                Path.GetFileName(sourceFilePath),
                Path.GetFileName(owningProjectPath),
                string.IsNullOrWhiteSpace(input.Role) ? "edit" : input.Role,
                input.Reason ?? string.Empty));
        }

        return plannedFiles;
    }

    private static void EnsurePlannedFile(AIMonitorSessionEditPlan editPlan, string sourceFilePath)
    {
        string fullPath = Path.GetFullPath(sourceFilePath);
        bool isPlanned = editPlan.FilesPlanned.Any(file =>
            Path.GetFullPath(file.SourceFilePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (!isPlanned)
        {
            throw new InvalidOperationException("Source file is not in the session edit plan: " + fullPath);
        }
    }

    private string ResolveOwningProjectPath(string sourceFilePath)
    {
        string normalizedSourceFilePath = Path.GetFullPath(sourceFilePath);
        IndexedDocumentRow[] matches = queryService.ListDocuments(filePath: normalizedSourceFilePath)
            .Where(document => Path.GetFullPath(document.FilePath).Equals(normalizedSourceFilePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("Planned file must include owningProjectPath when the existing index does not have exactly one owning project for: " + sourceFilePath);
        }

        return Path.GetFullPath(matches[0].ProjectPath);
    }

    private static AIMonitorFileHashInfo GetFileHashInfo(string path)
    {
        FileInfo info = new(path);
        return new AIMonitorFileHashInfo(
            ComputeFileHash(path),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static AIMonitorToolErrorResult? TryCreateIndexedStableSymbolKeyError(string stableSymbolKey)
    {
        if (string.IsNullOrWhiteSpace(stableSymbolKey))
        {
            return new AIMonitorToolErrorResult(
                true,
                "A stable indexed symbol key is required. Use query_solution_index, find_indexed_symbols, or get_indexed_symbol to obtain a symbol:<hash> key.",
                "symbol:<hash>",
                stableSymbolKey);
        }

        if (stableSymbolKey.StartsWith("symbol:", StringComparison.Ordinal))
        {
            return null;
        }

        if (stableSymbolKey.Contains("::", StringComparison.Ordinal))
        {
            return new AIMonitorToolErrorResult(
                true,
                "This looks like a Roslyn source-map selector key, not an indexed symbol key. find_indexed_references and find_indexed_callers require the symbol:<hash> key returned by query_solution_index, find_indexed_symbols, or get_indexed_symbol.",
                "symbol:<hash>",
                stableSymbolKey);
        }

        return new AIMonitorToolErrorResult(
            true,
            "Indexed reference tools require a stable indexed symbol key in symbol:<hash> form. Use query_solution_index, find_indexed_symbols, or get_indexed_symbol first.",
            "symbol:<hash>",
            stableSymbolKey);
    }

    private static AIMonitorIndexedReferenceResult ToMcpReferenceRow(IndexedReferenceRow reference)
    {
        return new AIMonitorIndexedReferenceResult(
            reference.TargetStableKey,
            reference.FilePath,
            reference.Line,
            reference.Column,
            reference.ReferenceKind,
            reference.Snippet,
            reference.TargetName,
            reference.TargetKind,
            reference.CallerStableKey,
            reference.CallerName,
            reference.CallerKind);
    }

    private static bool IsRecoverableRoslynGuidanceError(InvalidOperationException ex)
    {
        return ex.Message.Contains("Razor markup", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("supports C# source files only", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateExpectedHash(string path, string? expectedFileHash)
    {
        if (!string.IsNullOrWhiteSpace(expectedFileHash)
            && !ComputeFileHash(path).Equals(expectedFileHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Working candidate hash did not match expectedFileHash.");
        }
    }

    private static bool IsUnderBuildOrHiddenDirectory(string path)
    {
        string[] parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part.StartsWith(".", StringComparison.Ordinal)
            || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "item" : clean;
    }
}
