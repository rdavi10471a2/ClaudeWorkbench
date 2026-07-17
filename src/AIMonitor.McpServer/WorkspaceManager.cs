using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Workflow;

namespace AIMonitor.McpServer;

// Owns the CURRENT watched workspace and the engine services bound to it, and can
// rebuild them when the operator switches workspaces at runtime. Monitor-general
// facts (install/repo root, runtime base, WinMerge candidates) are stable; the
// per-workspace MonitorSettings + services are swapped by SwitchTo(). Consumers
// (the MCP tools, the source browser, the UI) read the current services through
// this manager rather than capturing a startup singleton.
public sealed class WorkspaceManager
{
    private readonly object sync = new();
    private WorkspaceServices? current;

    public WorkspaceManager(
        string repositoryRoot,
        string runtimeRoot,
        IReadOnlyList<string> winMergeCandidatePaths,
        MonitorSettings? initial)
    {
        RepositoryRoot = repositoryRoot;
        RuntimeRoot = runtimeRoot;
        WinMergeCandidatePaths = winMergeCandidatePaths;
        if (initial is not null && WatchedWorkspaceExists(initial))
        {
            current = WorkspaceServices.Build(initial);
        }
    }

    public string RepositoryRoot { get; }

    public string RuntimeRoot { get; }

    public IReadOnlyList<string> WinMergeCandidatePaths { get; }

    public event Action? Changed;

    public bool HasWorkspace
    {
        get
        {
            lock (sync)
            {
                return current is not null;
            }
        }
    }

    public string? WatchedSolutionPath
    {
        get
        {
            lock (sync)
            {
                return current?.Settings.WatchedSolutionPath;
            }
        }
    }

    public MonitorSettings Settings => Require().Settings;

    public SolutionIndexQueryService Query => Require().Query;

    public WorkflowEditService EditService => Require().EditService;

    public RoslynEditService RoslynEditService => Require().RoslynEditService;

    public WorkflowEditPaths EditPaths => Require().EditPaths;

    // Point the monitor at a different watched solution, rebuilding its engine
    // services against the new workspace. Persistence of the choice is the host's job.
    public void SwitchTo(string watchedSolutionPath)
    {
        MonitorSettings settings = MonitorSettings.Create(
            RepositoryRoot,
            Path.GetFullPath(watchedSolutionPath),
            RuntimeRoot,
            WinMergeCandidatePaths);
        WorkspaceServices services = WorkspaceServices.Build(settings);
        lock (sync)
        {
            current = services;
        }

        Changed?.Invoke();
    }

    // Initialize the current workspace's runtime state (build the solution index).
    public async Task ProvisionAsync()
    {
        WorkspaceServices services = Require();
        await new SolutionIndexRebuildService().RebuildAsync(services.Settings);
        Changed?.Invoke();
    }

    private WorkspaceServices Require()
    {
        lock (sync)
        {
            return current ?? throw new InvalidOperationException("No watched workspace is configured.");
        }
    }

    private static bool WatchedWorkspaceExists(MonitorSettings settings)
    {
        // A valid workspace requires the actual solution file to exist (its folder
        // existing is not enough — a seeded placeholder path must open the picker).
        return File.Exists(settings.WatchedSolutionPath);
    }

    private sealed class WorkspaceServices
    {
        public required MonitorSettings Settings { get; init; }

        public required SolutionIndexQueryService Query { get; init; }

        public required WorkflowEditService EditService { get; init; }

        public required RoslynEditService RoslynEditService { get; init; }

        public required WorkflowEditPaths EditPaths { get; init; }

        public static WorkspaceServices Build(MonitorSettings settings)
        {
            return new WorkspaceServices
            {
                Settings = settings,
                Query = SolutionIndexQueryService.Create(settings),
                EditService = new WorkflowEditService(settings),
                RoslynEditService = new RoslynEditService(settings),
                EditPaths = new WorkflowEditPaths(settings),
            };
        }
    }
}
