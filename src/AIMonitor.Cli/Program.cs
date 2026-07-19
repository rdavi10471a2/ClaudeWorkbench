using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Workflow;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIMonitor.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("AIMonitor CLI scaffold");
            Console.WriteLine("Commands:");
            Console.WriteLine("  hub start (not supported — use: dotnet run --project src/ClaudeWorkbench.Host)");
            Console.WriteLine("  status [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index rebuild [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index summary [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index projects [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index documents [--project <path>] [--file <path>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index symbols [--file <path>] [--name <name>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index references [--symbol <stable-key>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index references-in-file --file <path> [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  index packages [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit refresh --file <path> [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit new --file <future-path> [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit replace-text --file <path> --old-text <text>|--old-text-file <path> --new-text <text>|--new-text-file <path> [--expected-matches <n>] [--expected-working-hash <hash>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit status --file <path> [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit stage --file <path> [--ledger-summary <text>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit review (--file <path> | --staged-record-id <id>) [--force-validation] [--repo-root <path>] [--config <path>]");
        Console.WriteLine("  edit record-decision --staged-record-id <id> --decision accepted|rejected [--expected-staged-hash <hash>] [--repo-root <path>] [--config <path>]");
            Console.WriteLine("  edit accept --file <path> --expected-staged-hash <hash> [--repo-root <path>] [--config <path>] (shortcut)");
            Console.WriteLine("  edit reject --file <path> [--repo-root <path>] [--config <path>] (shortcut)");
            return 0;
        }

        if (IsHubStart(args))
        {
            return StartHub();
        }

        if (string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            return Query(args, service => service.GetMonitorStatus());
        }

        if (IsIndexRebuild(args))
        {
            return await RebuildIndexAsync(args);
        }

        if (IsIndexQuery(args))
        {
            return QueryIndex(args);
        }

        if (IsEditCommand(args))
        {
            return Edit(args);
        }

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        return 2;
    }

    private static bool IsIndexRebuild(string[] args)
    {
        return args.Length >= 2
            && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "rebuild", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHubStart(string[] args)
    {
        return args.Length >= 2
            && string.Equals(args[0], "hub", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIndexQuery(string[] args)
    {
        return args.Length >= 2
            && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEditCommand(string[] args)
    {
        return args.Length >= 2
            && string.Equals(args[0], "edit", StringComparison.OrdinalIgnoreCase);
    }

    private static int QueryIndex(string[] args)
    {
        return args[1].ToLowerInvariant() switch
        {
            "summary" => Query(args, service => service.GetSummary()),
            "projects" => Query(args, service => service.ListProjects()),
            "documents" => Query(args, service => service.ListDocuments(
                GetOption(args, "--project"),
                GetOption(args, "--file"))),
            "symbols" => Query(args, service => service.ListSymbols(
                GetOption(args, "--file"),
                GetOption(args, "--name"))),
            "references" => Query(args, service => service.ListReferences(GetOption(args, "--symbol"))),
            "references-in-file" => Query(args, service => service.ListReferencesInFile(RequireOption(args, "--file"))),
            "packages" => Query(args, service => service.ListPackageReferences()),
            _ => UnknownIndexCommand(args[1])
        };
    }

    private static int Query<T>(string[] args, Func<SolutionIndexQueryService, T> query)
    {
        try
        {
            MonitorSettings settings = LoadSettings(args);
            IMonitorLogger logger = CreateLogger(settings);
            string commandName = GetCommandName(args);
            string commandLine = CreateCommandLinePreview(args);
            string requestId = Guid.NewGuid().ToString("N");
            Stopwatch stopwatch = Stopwatch.StartNew();
            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "adapter.query.started",
                "CLI adapter query started.",
                new Dictionary<string, string>
                {
                    ["requestId"] = requestId,
                    ["adapterProtocol"] = "cli-mcp-like",
                    ["command"] = commandName,
                    ["toolName"] = commandName,
                    ["commandLine"] = commandLine,
                    ["paramsPreview"] = CreateParamsPreview(args)
                });

            SolutionIndexQueryService service = CreateQueryService(settings);
            T result = query(service);
            string responseJson = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(responseJson);
            stopwatch.Stop();

            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "adapter.query.completed",
                "CLI adapter query completed.",
                new Dictionary<string, string>
                {
                    ["requestId"] = requestId,
                    ["adapterProtocol"] = "cli-mcp-like",
                    ["command"] = commandName,
                    ["toolName"] = commandName,
                    ["commandLine"] = commandLine,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["isError"] = "false",
                    ["contentType"] = "application/json",
                    ["contentShape"] = GetResultKind(responseJson),
                    ["contentCount"] = GetResultCount(responseJson),
                    ["contentTextPreview"] = CreateResponsePreview(responseJson)
                });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Edit(string[] args)
    {
        return ExecuteJsonCommand(args, () =>
        {
            MonitorSettings settings = LoadSettings(args);
            IMonitorLogger logger = CreateLogger(settings);
            WorkflowEditService service = new(settings);
            string subcommand = args[1].ToLowerInvariant();
            return subcommand switch
            {
                "refresh" => service.Refresh(RequireOption(args, "--file")),
                "new" => service.NewFile(RequireOption(args, "--file")),
                "replace-text" => service.ReplaceText(
                    RequireOption(args, "--file"),
                    RequireTextOption(args, "--old-text", "--old-text-file"),
                    RequireTextOption(args, "--new-text", "--new-text-file"),
                    GetIntOption(args, "--expected-matches"),
                    GetOption(args, "--expected-working-hash"),
                    GetIntOption(args, "--occurrence-index")),
                "status" => service.GetStatus(RequireOption(args, "--file")),
                "stage" => CreateStageResponse(
                    service,
                    service.Stage(RequireOption(args, "--file"), GetOption(args, "--ledger-summary")),
                    HasOption(args, "--verbose")),
                "staged-record" => service.GetStagedRecord(RequireOption(args, "--staged-record-id")),
                "review" => Review(args, service),
                "record-decision" => RecordDecision(args, settings, logger, service),
                "accept" => Accept(args, settings, logger, service),
                "reject" => Reject(args, settings, logger, service),
                _ => throw new InvalidOperationException($"Unknown edit command: {args[1]}")
            };
        });
    }

    private static object CreateStageResponse(WorkflowEditService service, StagedEditRecord record, bool verbose)
    {
        StagedEditSummary summary = service.CreateSummary(record);
        return new
        {
            stagedRecordId = summary.StagedRecordId,
            watchedFilePath = summary.WatchedFilePath,
            relativePath = summary.RelativePath,
            status = summary.Status,
            classification = summary.Classification,
            stagedHash = summary.StagedHash,
            launchStatus = summary.LaunchStatus,
            stagedRecordPath = summary.RecordPath,
            stagedRecordSummary = summary,
            stagedRecord = verbose ? record : null,
            nextStep = "Candidate staged. Use edit staged-record for full details; the operator reviews and decides in the ClaudeWorkbench Merge Review surface."
        };
    }

    private static object RecordDecision(string[] args, MonitorSettings settings, IMonitorLogger logger, WorkflowEditService service)
    {
        return new StagedDecisionWorkflow().Record(
            settings,
            logger,
            service,
            RequireOption(args, "--staged-record-id"),
            RequireOption(args, "--decision"),
            GetOption(args, "--expected-staged-hash"),
            "AIMonitor.Cli",
            HasOption(args, "--verbose"));
    }

    // The engine refuses to record an accept until a review has been recorded for the staged
    // record. In the app that stamp is applied by EngineReviewWorkflow.EnsureValidatedAndLaunched
    // when the operator opens Merge Review. Out-of-process callers need an explicit equivalent:
    // it used to be a side effect of the retired `edit launch-diff` verb, and without it
    // `edit accept` is unreachable. This records the same GATE-1 verdict and review stamp the
    // host does — it launches nothing.
    private static object Review(string[] args, WorkflowEditService service)
    {
        string? stagedRecordId = GetOption(args, "--staged-record-id");
        if (string.IsNullOrWhiteSpace(stagedRecordId))
        {
            EditSessionStatus status = service.GetStatus(RequireOption(args, "--file"));
            if (string.IsNullOrWhiteSpace(status.LastStagedRecordId))
            {
                throw new InvalidOperationException("No staged record exists for this file. Run edit stage first.");
            }

            stagedRecordId = status.LastStagedRecordId;
        }

        StagedEditRecord record = service.GetStagedRecord(stagedRecordId);
        service.PrepareReviewFileForLaunch(stagedRecordId);
        PreMergeValidationResult validation = new PreMergeValidationService().ValidateStagedOverlay(record, [record]);
        service.RecordPreMergeValidation(stagedRecordId, validation, HasOption(args, "--force-validation"));
        StagedEditRecord reviewed = service.RecordDiffLaunch(stagedRecordId, launched: true, "cli review");
        return new
        {
            stagedRecordId,
            preMergeValidationStatus = validation.Status,
            preMergeValidationIsError = validation.IsError,
            preMergeValidationDiagnosticCount = validation.DiagnosticCount,
            launchStatus = reviewed.LaunchStatus
        };
    }

    private static object Accept(string[] args, MonitorSettings settings, IMonitorLogger logger, WorkflowEditService service)
    {
        string file = RequireOption(args, "--file");
        EditSessionStatus status = service.GetStatus(file);
        if (string.IsNullOrWhiteSpace(status.LastStagedRecordId))
        {
            throw new InvalidOperationException("No staged record exists for this file. Run edit stage first.");
        }

        return new StagedDecisionWorkflow().Record(
            settings,
            logger,
            service,
            status.LastStagedRecordId,
            "accepted",
            RequireExpectedStagedHash(args),
            "AIMonitor.Cli",
            HasOption(args, "--verbose"));
    }

    private static object Reject(string[] args, MonitorSettings settings, IMonitorLogger logger, WorkflowEditService service)
    {
        string file = RequireOption(args, "--file");
        EditSessionStatus status = service.GetStatus(file);
        if (string.IsNullOrWhiteSpace(status.LastStagedRecordId))
        {
            throw new InvalidOperationException("No staged record exists for this file. Run edit stage first.");
        }

        return new StagedDecisionWorkflow().Record(
            settings,
            logger,
            service,
            status.LastStagedRecordId,
            "rejected",
            null,
            "AIMonitor.Cli",
            HasOption(args, "--verbose"));
    }

    private static string RequireExpectedStagedHash(string[] args)
    {
        return GetOption(args, "--expected-staged-hash")
            ?? GetOption(args, "--expected-hash")
            ?? throw new InvalidOperationException("--expected-staged-hash is required when recording an accepted decision.");
    }

    private static int ExecuteJsonCommand<T>(string[] args, Func<T> command)
    {
        try
        {
            MonitorSettings settings = LoadSettings(args);
            IMonitorLogger logger = CreateLogger(settings);
            string commandName = GetCommandName(args);
            string commandLine = CreateCommandLinePreview(args);
            string requestId = Guid.NewGuid().ToString("N");
            Stopwatch stopwatch = Stopwatch.StartNew();
            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "adapter.query.started",
                "CLI adapter command started.",
                new Dictionary<string, string>
                {
                    ["requestId"] = requestId,
                    ["adapterProtocol"] = "cli-mcp-like",
                    ["command"] = commandName,
                    ["toolName"] = commandName,
                    ["commandLine"] = commandLine,
                    ["paramsPreview"] = CreateParamsPreview(args)
                });

            T result = command();
            string responseJson = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(responseJson);
            stopwatch.Stop();
            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "adapter.query.completed",
                "CLI adapter command completed.",
                new Dictionary<string, string>
                {
                    ["requestId"] = requestId,
                    ["adapterProtocol"] = "cli-mcp-like",
                    ["command"] = commandName,
                    ["toolName"] = commandName,
                    ["commandLine"] = commandLine,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["isError"] = "false",
                    ["contentType"] = "application/json",
                    ["contentShape"] = GetResultKind(responseJson),
                    ["contentCount"] = GetResultCount(responseJson),
                    ["contentTextPreview"] = CreateResponsePreview(responseJson)
                });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RebuildIndexAsync(string[] args)
    {
        try
        {
            MonitorSettings settings = LoadSettings(args);
            string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
            string logPath = MonitorLogPaths.GetDefaultLogPath(settings);
            IMonitorLogger logger = CreateLogger(settings);

            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "index.rebuild.started",
                "Solution index rebuild started.",
                new Dictionary<string, string>
                {
                    ["watchedSolutionPath"] = settings.WatchedSolutionPath,
                    ["databasePath"] = databasePath
                });

            SolutionIndexSummary summary = await new SolutionIndexRebuildService().RebuildAsync(settings);

            logger.Write(
                MonitorLogLevel.Information,
                "AIMonitor.Cli",
                "index.rebuild.completed",
                "Solution index rebuild completed.",
                new Dictionary<string, string>
                {
                    ["projectCount"] = summary.ProjectCount.ToString(),
                    ["documentCount"] = summary.DocumentCount.ToString(),
                    ["diagnosticCount"] = summary.DiagnosticCount.ToString()
                });

            Console.WriteLine($"Indexed solution: {settings.WatchedSolutionPath}");
            Console.WriteLine($"Database: {databasePath}");
            Console.WriteLine($"Log: {logPath}");
            Console.WriteLine($"Projects: {summary.ProjectCount}");
            Console.WriteLine($"Documents: {summary.DocumentCount}");
            Console.WriteLine($"Diagnostics: {summary.DiagnosticCount}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // 'hub start' used to shell out to src/AIMonitor.App (the WinForms hub of the
    // AIMonitor lineage). That project does not exist in ClaudeWorkbench, so the verb
    // could only ever fail with a confusing "project was not found". It now fails fast
    // and points the operator at the real console instead.
    private static int StartHub()
    {
        Console.Error.WriteLine("'hub start' is not supported in ClaudeWorkbench: the AIMonitor.App hub no longer exists.");
        Console.Error.WriteLine("The operator console is ClaudeWorkbench.Host — run 'dotnet run --project src/ClaudeWorkbench.Host',");
        Console.Error.WriteLine("or start it from the Launcher in a published install.");
        return 1;
    }

    private static SolutionIndexQueryService CreateQueryService(string[] args)
    {
        return SolutionIndexQueryService.Create(LoadSettings(args));
    }

    private static SolutionIndexQueryService CreateQueryService(MonitorSettings settings)
    {
        return SolutionIndexQueryService.Create(settings);
    }

    private static MonitorSettings LoadSettings(string[] args)
    {
        string repositoryRoot = GetOption(args, "--repo-root") ?? Directory.GetCurrentDirectory();
        string? settingsPath = GetOption(args, "--config");
        return MonitorSettingsLoader.Load(repositoryRoot, settingsPath);
    }

    private static IMonitorLogger CreateLogger(MonitorSettings settings)
    {
        return new JsonLinesMonitorLogger(MonitorLogPaths.GetDefaultLogPath(settings));
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool HasOption(string[] args, string optionName)
    {
        return args.Any(argument => string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequireOption(string[] args, string optionName)
    {
        return GetOption(args, optionName)
            ?? throw new InvalidOperationException($"{optionName} is required.");
    }

    private static int? GetIntOption(string[] args, string optionName)
    {
        string? value = GetOption(args, optionName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out int parsed))
        {
            throw new InvalidOperationException($"{optionName} must be an integer.");
        }

        return parsed;
    }

    private static string RequireTextOption(string[] args, string inlineOptionName, string fileOptionName)
    {
        string? inlineValue = GetOption(args, inlineOptionName);
        string? filePath = GetOption(args, fileOptionName);
        if (HasOption(args, inlineOptionName) && !string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException($"Use either {inlineOptionName} or {fileOptionName}, not both.");
        }

        if (HasOption(args, inlineOptionName))
        {
            return inlineValue ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return File.ReadAllText(filePath);
        }

        throw new InvalidOperationException($"{inlineOptionName} or {fileOptionName} is required.");
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ', StringComparison.Ordinal)
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;
    }

    private static string CreateCommandLinePreview(string[] args)
    {
        List<string> sanitized = [];
        for (int index = 0; index < args.Length; index++)
        {
            string value = args[index];
            sanitized.Add(value);
            if (IsSensitiveTextOption(value) && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                sanitized.Add("[redacted]");
                index++;
            }
        }

        return string.Join(" ", sanitized.Select(QuoteArgument));
    }

    private static string GetCommandName(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase))
        {
            return $"index {args[1]}";
        }

        if (args.Length >= 2 && string.Equals(args[0], "edit", StringComparison.OrdinalIgnoreCase))
        {
            return $"edit {args[1]}";
        }

        return args[0];
    }

    private static string GetResultKind(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        return document.RootElement.ValueKind.ToString();
    }

    private static string GetResultCount(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.GetArrayLength().ToString()
            : "1";
    }

    private static string CreateResponsePreview(string responseJson)
    {
        string redactedResponseJson = RedactResponseForLogPreview(responseJson);
        string singleLine = string.Join(" ", redactedResponseJson.Split(
            [' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 600
            ? singleLine
            : singleLine[..600] + "...";
    }

    private static string RedactResponseForLogPreview(string responseJson)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(responseJson);
            RedactSnippetProperties(node);
            return node?.ToJsonString(JsonOptions) ?? string.Empty;
        }
        catch (JsonException)
        {
            return "[unavailable]";
        }
    }

    private static void RedactSnippetProperties(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (KeyValuePair<string, JsonNode?> property in jsonObject.ToList())
            {
                if (property.Key.Equals("snippet", StringComparison.OrdinalIgnoreCase))
                {
                    jsonObject[property.Key] = "[redacted]";
                    continue;
                }

                RedactSnippetProperties(property.Value);
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? item in jsonArray)
            {
                RedactSnippetProperties(item);
            }
        }
    }

    private static string CreateParamsPreview(string[] args)
    {
        Dictionary<string, string> values = [];
        for (int index = 0; index < args.Length; index++)
        {
            string value = args[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            values[value.TrimStart('-')] = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? IsSensitiveTextOption(value) ? "[redacted]" : args[index + 1]
                : "true";
        }

        return JsonSerializer.Serialize(values, JsonOptions);
    }

    private static bool IsSensitiveTextOption(string option)
    {
        return option.Equals("--old-text", StringComparison.OrdinalIgnoreCase)
            || option.Equals("--new-text", StringComparison.OrdinalIgnoreCase);
    }

    private static int UnknownIndexCommand(string command)
    {
        Console.Error.WriteLine($"Unknown index command: {command}");
        return 2;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

}
