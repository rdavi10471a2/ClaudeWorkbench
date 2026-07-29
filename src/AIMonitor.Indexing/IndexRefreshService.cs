using System.Diagnostics;
using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Logging;
using AIMonitor.MSBuild;
using AIMonitor.Workflow;

namespace AIMonitor.Indexing;

// Refreshes the solution index after an accepted edit — scoped to changed files when safe, else a full
// rebuild. A capability, not a workflow phase: the manual "Rebuild Index" button and the accept flow
// both call it, so its name says what it does, not when it runs.
public sealed class IndexRefreshService
{
    public PostAcceptIndexRefreshResult RebuildAfterAcceptedDecision(
        MonitorSettings settings,
        IMonitorLogger logger,
        StagedEditRecord record,
        string source,
        PostAcceptIndexRefreshPlan? refreshPlan = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        string[] projectPaths = GetProjectRefreshPaths(record, refreshPlan);
        string[] filePaths = GetFileRefreshPaths(record, refreshPlan);
        bool useFileRefresh = projectPaths.Length == 1 && filePaths.Length > 0;

        // Schema-versioned rebuild gate: if the index was opened against a db whose persisted PRAGMA user_version did
        // not match SolutionIndexDatabase.SchemaVersion, EnsureCreated dropped ALL index tables, recreated the full
        // schema empty, and set a persistent needs_full_rebuild marker. The tables are now empty, so a scoped refresh
        // would leave the index half-populated. Force a full RebuildAsync (which repopulates everything and clears the
        // marker) and refuse/upgrade the scoped path until then.
        bool fullRebuildRequired = new SolutionIndexDatabase(databasePath).IsFullRebuildRequired();
        if (fullRebuildRequired)
        {
            useFileRefresh = false;
        }

        string[] inboundDependents = [];
        if (useFileRefresh)
        {
            // HIGH #1: a project-scoped refresh of A re-inserts only A's own rows. Other projects' inbound
            // reference/call-site/relationship rows that target A's symbols are then left pointing at target keys
            // that may now be STALE (A's symbols can change stable_key on re-index) — silently orphaning inbound
            // cross-project references. (Schema v2 removed the cross-symbol ON DELETE CASCADE, so this is a
            // stale-key hazard, not a cascade-delete one.) If any other project holds inbound references into A,
            // fall back to a full solution rebuild (MVP). (Optimization for later: refresh only the closure = A
            // union its inbound dependents.)
            SolutionIndexProbe probe = new(new SolutionIndexDatabase(databasePath));
            inboundDependents = probe.GetInboundDependentProjectPaths(projectPaths[0]).ToArray();
            if (inboundDependents.Length > 0)
            {
                useFileRefresh = false;
            }
        }

        string refreshMode = useFileRefresh ? "project" : "solution";
        CompileIndexTrace.Record(
            settings,
            "index-refresh.start",
            settings.WatchedSolutionPath,
            $"index refresh (trigger={source}, mode={refreshMode}) — in the accept flow this runs AFTER the terminal gate-build above; the index-compile.* events that follow are its OWN in-proc compile");
        logger.Write(
            MonitorLogLevel.Information,
            source,
            "index.refresh-after-accept.started",
            useFileRefresh
                ? "Post-accept planned project index refresh started."
                : "Post-accept solution index rebuild started.",
            new Dictionary<string, string>
            {
                ["stagedRecordId"] = record.StagedRecordId,
                ["watchedFilePath"] = record.WatchedFilePath,
                ["watchedSolutionPath"] = settings.WatchedSolutionPath,
                ["databasePath"] = databasePath,
                ["refreshMode"] = refreshMode,
                ["projectPaths"] = string.Join(";", projectPaths),
                ["filePaths"] = string.Join(";", filePaths),
                ["inboundReferencingProjects"] = string.Join(";", inboundDependents)
            });

        if (refreshPlan is not null)
        {
            refreshPlan.InboundReferencingProjects = inboundDependents;
        }

        try
        {
            Action<string, long, IReadOnlyDictionary<string, string>> timingSink = CreateTimingSink(
                logger,
                source,
                record,
                refreshMode,
                projectPaths,
                filePaths);
            SolutionIndexSummary summary = useFileRefresh
                ? new SolutionIndexRebuildService().RefreshProjectFilesAsync(settings, projectPaths[0], filePaths, timingSink: timingSink).GetAwaiter().GetResult()
                : new SolutionIndexRebuildService().RebuildAsync(settings, timingSink: timingSink).GetAwaiter().GetResult();
            stopwatch.Stop();
            PostAcceptIndexRefreshResult result = new()
            {
                Status = "rebuilt",
                RefreshMode = refreshMode,
                IsError = false,
                DatabasePath = databasePath,
                ProjectCount = summary.ProjectCount,
                DocumentCount = summary.DocumentCount,
                DiagnosticCount = summary.DiagnosticCount,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = useFileRefresh
                    ? "Post-accept planned project index refresh completed."
                    : "Post-accept solution index rebuild completed."
            };
            MarkRefreshFilesFresh(settings, record, filePaths, rebuiltWholeProjectOrSolution: useFileRefresh, refreshPlan);
            logger.Write(
                MonitorLogLevel.Information,
                source,
                "index.refresh-after-accept.completed",
                result.Message,
                new Dictionary<string, string>
                {
                    ["stagedRecordId"] = record.StagedRecordId,
                    ["watchedFilePath"] = record.WatchedFilePath,
                    ["databasePath"] = databasePath,
                    ["projectCount"] = result.ProjectCount.ToString(),
                    ["documentCount"] = result.DocumentCount.ToString(),
                    ["diagnosticCount"] = result.DiagnosticCount.ToString(),
                    ["durationMs"] = result.DurationMs.ToString(),
                    ["isError"] = "false",
                    ["refreshMode"] = result.RefreshMode,
                    ["projectPaths"] = string.Join(";", projectPaths),
                    ["filePaths"] = string.Join(";", filePaths)
                });
            return result;
        }
        catch (Exception ex)
        {
            if (useFileRefresh)
            {
                try
                {
                    Action<string, long, IReadOnlyDictionary<string, string>> fallbackTimingSink = CreateTimingSink(
                        logger,
                        source,
                        record,
                        "solution-fallback",
                        projectPaths,
                        filePaths);
                    SolutionIndexSummary fallbackSummary = new SolutionIndexRebuildService().RebuildAsync(settings, timingSink: fallbackTimingSink).GetAwaiter().GetResult();
                    stopwatch.Stop();
                    PostAcceptIndexRefreshResult fallbackResult = new()
                    {
                        Status = "rebuilt",
                        RefreshMode = "solution-fallback",
                        IsError = false,
                        DatabasePath = databasePath,
                        ProjectCount = fallbackSummary.ProjectCount,
                        DocumentCount = fallbackSummary.DocumentCount,
                        DiagnosticCount = fallbackSummary.DiagnosticCount,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        Message = "Post-accept project index refresh failed; full solution index rebuild completed."
                    };
                    // Solution fallback rebuilt the entire solution, so every accepted session file is fresh.
                    MarkRefreshFilesFresh(settings, record, filePaths, rebuiltWholeProjectOrSolution: true, refreshPlan);
                    logger.Write(
                        MonitorLogLevel.Information,
                        source,
                        "index.refresh-after-accept.completed",
                        fallbackResult.Message,
                        new Dictionary<string, string>
                        {
                            ["stagedRecordId"] = record.StagedRecordId,
                            ["watchedFilePath"] = record.WatchedFilePath,
                            ["databasePath"] = databasePath,
                            ["projectCount"] = fallbackResult.ProjectCount.ToString(),
                            ["documentCount"] = fallbackResult.DocumentCount.ToString(),
                            ["diagnosticCount"] = fallbackResult.DiagnosticCount.ToString(),
                            ["durationMs"] = fallbackResult.DurationMs.ToString(),
                            ["isError"] = "false",
                            ["refreshMode"] = fallbackResult.RefreshMode,
                            ["projectPaths"] = string.Join(";", projectPaths),
                            ["filePaths"] = string.Join(";", filePaths),
                            ["refreshError"] = ex.Message
                        });
                    return fallbackResult;
                }
                catch (Exception fallbackEx)
                {
                    ex = new InvalidOperationException(
                        "Project index refresh failed, and the full solution fallback also failed: " + fallbackEx.Message,
                        fallbackEx);
                }
            }

            stopwatch.Stop();
            PostAcceptIndexRefreshResult result = new()
            {
                Status = "failed",
                RefreshMode = refreshMode,
                IsError = true,
                DatabasePath = databasePath,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = ex.Message
            };
            logger.Write(
                MonitorLogLevel.Error,
                source,
                "index.refresh-after-accept.failed",
                "Post-accept solution index rebuild failed.",
                new Dictionary<string, string>
                {
                    ["stagedRecordId"] = record.StagedRecordId,
                    ["watchedFilePath"] = record.WatchedFilePath,
                    ["databasePath"] = databasePath,
                    ["durationMs"] = result.DurationMs.ToString(),
                    ["isError"] = "true",
                    ["refreshMode"] = result.RefreshMode,
                    ["projectPaths"] = string.Join(";", projectPaths),
                    ["filePaths"] = string.Join(";", filePaths),
                    ["error"] = ex.Message
                });
            return result;
        }
    }

    // ADR-0007 accept path: refresh the index by READING the build-after-accept's WHOLE-solution output (every
    // project's generated .g.cs + per-project refs) rather than running a compile. This is the no-3-builds path
    // — the terminal gate build validated, the build-after-accept produced the real output for all N projects,
    // and this reads it. One path for 1..N projects — no single-project and no single-file special case.
    public PostAcceptIndexRefreshResult RebuildAfterAcceptedDecisionFromBuildOutput(
        MonitorSettings settings,
        IMonitorLogger logger,
        StagedEditRecord record,
        string source,
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        PostAcceptIndexRefreshPlan? refreshPlan = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        string[] filePaths = GetFileRefreshPaths(record, refreshPlan);

        CompileIndexTrace.Record(
            settings,
            "index-refresh.start",
            settings.WatchedSolutionPath,
            $"index refresh (trigger={source}, mode=build-output-read) — reads the build-after-accept's whole-solution generated + refs, NO compile of its own");
        logger.Write(
            MonitorLogLevel.Information,
            source,
            "index.refresh-after-accept.started",
            "Post-accept build-output index refresh started (reads the build's generated files + refs, no compile).",
            new Dictionary<string, string>
            {
                ["stagedRecordId"] = record.StagedRecordId,
                ["watchedFilePath"] = record.WatchedFilePath,
                ["watchedSolutionPath"] = settings.WatchedSolutionPath,
                ["databasePath"] = databasePath,
                ["refreshMode"] = "build-output-read",
                ["solutionPath"] = solutionPath,
                ["projectCount"] = projectPaths.Count.ToString()
            });

        try
        {
            Action<string, long, IReadOnlyDictionary<string, string>> timingSink = CreateTimingSink(
                logger, source, record, "build-output-read", projectPaths, filePaths);
            SolutionIndexSummary summary = new SolutionIndexRebuildService()
                .RebuildFromBuildOutputAsync(settings, solutionPath, projectPaths, timingSink: timingSink)
                .GetAwaiter()
                .GetResult();
            stopwatch.Stop();
            PostAcceptIndexRefreshResult result = new()
            {
                Status = "rebuilt",
                RefreshMode = "build-output-read",
                IsError = false,
                DatabasePath = databasePath,
                ProjectCount = summary.ProjectCount,
                DocumentCount = summary.DocumentCount,
                DiagnosticCount = summary.DiagnosticCount,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = "Post-accept build-output index refresh completed."
            };
            // The build-output read reindexed the whole (single) project, so every accepted session file is fresh.
            MarkRefreshFilesFresh(settings, record, filePaths, rebuiltWholeProjectOrSolution: true, refreshPlan);
            logger.Write(
                MonitorLogLevel.Information,
                source,
                "index.refresh-after-accept.completed",
                result.Message,
                new Dictionary<string, string>
                {
                    ["stagedRecordId"] = record.StagedRecordId,
                    ["watchedFilePath"] = record.WatchedFilePath,
                    ["databasePath"] = databasePath,
                    ["projectCount"] = result.ProjectCount.ToString(),
                    ["documentCount"] = result.DocumentCount.ToString(),
                    ["diagnosticCount"] = result.DiagnosticCount.ToString(),
                    ["durationMs"] = result.DurationMs.ToString(),
                    ["isError"] = "false",
                    ["refreshMode"] = result.RefreshMode
                });
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            PostAcceptIndexRefreshResult result = new()
            {
                Status = "failed",
                RefreshMode = "build-output-read",
                IsError = true,
                DatabasePath = databasePath,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Message = ex.Message
            };
            logger.Write(
                MonitorLogLevel.Error,
                source,
                "index.refresh-after-accept.failed",
                "Post-accept build-output index refresh failed.",
                new Dictionary<string, string>
                {
                    ["stagedRecordId"] = record.StagedRecordId,
                    ["watchedFilePath"] = record.WatchedFilePath,
                    ["databasePath"] = databasePath,
                    ["durationMs"] = result.DurationMs.ToString(),
                    ["isError"] = "true",
                    ["refreshMode"] = result.RefreshMode,
                    ["error"] = ex.Message
                });
            return result;
        }
    }

    public static PostAcceptIndexRefreshResult DeferredUntilPlannedFilesComplete()
    {
        return new PostAcceptIndexRefreshResult
        {
            Status = "deferred",
            RefreshMode = "session-deferred",
            Message = "Index refresh deferred until all planned session edit files have terminal decisions."
        };
    }

    private static string[] GetProjectRefreshPaths(
        StagedEditRecord record,
        PostAcceptIndexRefreshPlan? refreshPlan)
    {
        if (!IsSafeFileScopedRefresh(record))
        {
            return [];
        }

        return refreshPlan?.OwningProjectPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static string[] GetFileRefreshPaths(
        StagedEditRecord record,
        PostAcceptIndexRefreshPlan? refreshPlan)
    {
        if (!IsSafeFileScopedRefresh(record))
        {
            return [];
        }

        return refreshPlan?.ChangedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !RazorLikePath(path))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static bool IsSafeFileScopedRefresh(StagedEditRecord record)
    {
        string extension = Path.GetExtension(record.WatchedFilePath);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            && !RazorLikePath(record.WatchedFilePath)
            && !Path.GetFileName(record.WatchedFilePath).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(record.WatchedFilePath).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(record.WatchedFilePath).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RazorLikePath(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void MarkRefreshFilesFresh(
        MonitorSettings settings,
        StagedEditRecord record,
        IReadOnlyList<string> filePaths,
        bool rebuiltWholeProjectOrSolution,
        PostAcceptIndexRefreshPlan? refreshPlan)
    {
        WorkflowEditService workflowService = new(settings);
        foreach (string path in GetFilesToMarkFresh(record, filePaths, rebuiltWholeProjectOrSolution, refreshPlan))
        {
            workflowService.MarkIndexFresh(path);
        }
    }

    private static IReadOnlyList<string> GetFilesToMarkFresh(
        StagedEditRecord record,
        IReadOnlyList<string> filePaths,
        bool rebuiltWholeProjectOrSolution,
        PostAcceptIndexRefreshPlan? refreshPlan)
    {
        // A project-scoped refresh rebuilds the ENTIRE owning project (and the solution fallback rebuilds everything),
        // so every accepted session file in that scope was reindexed — including the .razor.cs / .cshtml.cs siblings
        // that GetFileRefreshPaths filters out of the cheap-path file list. Mark all of those fresh too, not just the
        // Razor-filtered .cs subset, otherwise an accepted .razor.cs is left flagged stale despite being reindexed.
        if (rebuiltWholeProjectOrSolution && refreshPlan is not null)
        {
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            foreach (string changed in refreshPlan.ChangedFilePaths)
            {
                if (!string.IsNullOrWhiteSpace(changed))
                {
                    paths.Add(changed);
                }
            }

            foreach (string filtered in filePaths)
            {
                paths.Add(filtered);
            }

            paths.Add(record.WatchedFilePath);
            return paths.ToArray();
        }

        return filePaths.Count == 0
            ? [record.WatchedFilePath]
            : filePaths.ToArray();
    }

    private static Action<string, long, IReadOnlyDictionary<string, string>> CreateTimingSink(
        IMonitorLogger logger,
        string source,
        StagedEditRecord record,
        string refreshMode,
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> filePaths)
    {
        return (phase, durationMs, properties) =>
        {
            Dictionary<string, string> logProperties = new(StringComparer.Ordinal)
            {
                ["stagedRecordId"] = record.StagedRecordId,
                ["watchedFilePath"] = record.WatchedFilePath,
                ["refreshMode"] = refreshMode,
                ["phase"] = phase,
                ["durationMs"] = durationMs.ToString(),
                ["projectPaths"] = string.Join(";", projectPaths),
                ["filePaths"] = string.Join(";", filePaths)
            };
            foreach (KeyValuePair<string, string> property in properties)
            {
                logProperties[property.Key] = property.Value;
            }

            logger.Write(
                MonitorLogLevel.Information,
                source,
                "index.refresh-after-accept.phase",
                "Post-accept index refresh phase completed.",
                logProperties);
        };
    }
}
