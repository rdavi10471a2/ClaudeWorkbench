using AIMonitor.Core;
using AIMonitor.Workflow;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ClaudeWorkbench.Host.Components.Dialogs;

// NuGet package manager for the Source tab — the operator (human) surface. Browse/install/update/
// uninstall packages, scoped to the whole Solution or a single project (VS's two entry points in one
// modal). All work is host-side and out-of-process via NuGetPackageService; the agent is not involved.
//
// The Installed view is read straight from the project files (<PackageReference> items); Updates come
// from `dotnet list package --outdated` and search from `dotnet package search`. Everything is
// deliberately INDEPENDENT of the code index: installing a package changes no user symbols, so a package
// change restores inline (via `dotnet add`) but never triggers a reindex. The index's package graph
// refreshes on its own on the next real build/accept.
public partial class NuGetDialog
{
    private enum Tab { Browse, Installed, Updates, Consolidate }

    // Scope: the whole solution, or one project path.
    private const string SolutionScope = "";

    private sealed record ProjectEntry(string Path, string Name);
    private sealed record InstalledGroup(string PackageId, IReadOnlyList<(string ProjectPath, string ProjectName, string Version)> Uses)
    {
        public IReadOnlyList<string> DistinctVersions => Uses.Select(u => u.Version).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
        public bool IsMixed => DistinctVersions.Count > 1;
    }

    private Tab activeTab = Tab.Installed;
    private string scope = SolutionScope;
    private bool includePrerelease;

    private IReadOnlyList<ProjectEntry> projects = [];
    private IReadOnlyList<NuGetPackageService.InstalledPackage> installed = [];
    private IReadOnlyList<NuGetPackageService.OutdatedPackage> outdated = [];
    private bool loadingInstalled;
    private bool loadingOutdated;

    // Browse state.
    private string searchText = string.Empty;
    private bool searching;
    private IReadOnlyList<NuGetPackageService.PackageSearchHit> searchHits = [];
    private NuGetPackageService.PackageSearchHit? selectedHit;
    private string installVersion = string.Empty;
    private HashSet<string> installTargets = new(StringComparer.OrdinalIgnoreCase);

    // Shared op state.
    private bool busy;
    private string? statusMessage;
    private bool statusIsError;
    private IReadOnlyList<string> statusDiagnostics = [];

    [Parameter]
    public string? InitialProjectPath { get; set; }

    protected override void OnInitialized()
    {
        if (!string.IsNullOrWhiteSpace(InitialProjectPath))
        {
            scope = System.IO.Path.GetFullPath(InitialProjectPath);
        }

        // Every project in the solution — including package-less ones, so they're valid install targets.
        projects = WatchedSolutionInfo.ResolveAllProjects(Workspace.Settings.WatchedSolutionPath)
            .Select(path => System.IO.Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ProjectEntry(path, System.IO.Path.GetFileNameWithoutExtension(path)))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scope != SolutionScope && projects.All(p => !PathEquals(p.Path, scope)))
        {
            scope = SolutionScope;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        // Installed + outdated are both live SDK reads — run them together so the dialog fills quickly.
        await Task.WhenAll(RefreshInstalledAsync(), RefreshOutdatedAsync());
    }

    // ---- data loading (all live from the SDK; never touches the code index) ----

    private async Task RefreshInstalledAsync()
    {
        loadingInstalled = true;
        StateHasChanged();
        installed = await Task.Run(() => Packages.ListInstalled(Workspace.Settings));
        loadingInstalled = false;
        StateHasChanged();
    }

    private async Task RefreshOutdatedAsync()
    {
        loadingOutdated = true;
        StateHasChanged();
        outdated = await Task.Run(() => Packages.ListOutdated(Workspace.Settings, includePrerelease));
        loadingOutdated = false;
        StateHasChanged();
    }

    // ---- scope helpers ----

    private IReadOnlyList<string> ScopeProjectPaths =>
        scope == SolutionScope ? projects.Select(p => p.Path).ToArray() : [scope];

    private IReadOnlyList<InstalledGroup> InstalledInScope()
    {
        HashSet<string> inScope = new(ScopeProjectPaths, StringComparer.OrdinalIgnoreCase);
        return installed
            .Where(row => inScope.Contains(System.IO.Path.GetFullPath(row.ProjectPath)))
            .GroupBy(row => row.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InstalledGroup(
                group.Key,
                group.Select(row => (
                    System.IO.Path.GetFullPath(row.ProjectPath),
                    System.IO.Path.GetFileNameWithoutExtension(row.ProjectPath),
                    row.Version ?? "(central)"))
                    .ToArray()))
            .OrderBy(g => g.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<InstalledGroup> ConsolidationCandidates() =>
        InstalledInScope().Where(group => group.IsMixed).ToArray();

    // Outdated entries within the current scope, one row per (project, package).
    private IReadOnlyList<NuGetPackageService.OutdatedPackage> OutdatedInScope()
    {
        HashSet<string> inScope = new(ScopeProjectPaths, StringComparer.OrdinalIgnoreCase);
        return outdated
            .Where(entry => inScope.Contains(System.IO.Path.GetFullPath(entry.ProjectPath)))
            .OrderBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string? LatestFor(string packageId)
    {
        HashSet<string> inScope = new(ScopeProjectPaths, StringComparer.OrdinalIgnoreCase);
        return outdated
            .Where(entry => inScope.Contains(System.IO.Path.GetFullPath(entry.ProjectPath))
                && string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.LatestVersion)
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    // ---- browse / search ----

    private async Task SearchAsync()
    {
        if (searching || string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        searching = true;
        selectedHit = null;
        StateHasChanged();
        searchHits = await Task.Run(() => Packages.Search(searchText, includePrerelease, 40));
        searching = false;
        StateHasChanged();
    }

    private void SelectHit(NuGetPackageService.PackageSearchHit hit)
    {
        selectedHit = hit;
        installVersion = string.Empty; // default: latest
        // Default install targets: the scoped project, or all projects at solution scope.
        installTargets = new HashSet<string>(ScopeProjectPaths, StringComparer.OrdinalIgnoreCase);
    }

    private void ToggleTarget(string projectPath, bool on)
    {
        if (on)
        {
            installTargets.Add(projectPath);
        }
        else
        {
            installTargets.Remove(projectPath);
        }
    }

    private async Task InstallSelectedAsync()
    {
        if (selectedHit is null)
        {
            return;
        }

        string[] targets = installTargets.Count > 0 ? installTargets.ToArray() : ScopeProjectPaths.ToArray();
        await RunMutationAsync(() => Packages.Install(
            Workspace.Settings, targets, selectedHit.Id,
            string.IsNullOrWhiteSpace(installVersion) ? null : installVersion.Trim()));
    }

    // ---- installed / updates / consolidate actions ----

    private Task UninstallAsync(InstalledGroup group)
    {
        string[] targets = group.Uses.Select(u => u.ProjectPath).ToArray();
        return RunMutationAsync(() => Packages.Uninstall(Workspace.Settings, targets, group.PackageId));
    }

    private Task UpdateToLatestAsync(InstalledGroup group)
    {
        string? latest = LatestFor(group.PackageId);
        string[] targets = group.Uses.Select(u => u.ProjectPath).ToArray();
        return RunMutationAsync(() => Packages.Install(Workspace.Settings, targets, group.PackageId, latest));
    }

    private Task UpdateOutdatedAsync(NuGetPackageService.OutdatedPackage entry) =>
        RunMutationAsync(() => Packages.Install(Workspace.Settings, [entry.ProjectPath], entry.PackageId, entry.LatestVersion));

    private async Task UpdateAllInScopeAsync()
    {
        // Group the scope's outdated packages by id and push each to its latest across the projects using it.
        var byPackage = OutdatedInScope()
            .GroupBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                PackageId: group.Key,
                Latest: group.Select(e => e.LatestVersion).OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First(),
                Projects: group.Select(e => e.ProjectPath).ToArray()))
            .ToArray();

        await RunMutationAsync(() =>
        {
            foreach (var package in byPackage)
            {
                NuGetPackageService.PackageMutationResult result =
                    Packages.Install(Workspace.Settings, package.Projects, package.PackageId, package.Latest);
                if (result.IsError)
                {
                    return result;
                }
            }

            return new NuGetPackageService.PackageMutationResult(
                false, $"Updated {byPackage.Length} package(s) to the latest version.", []);
        });
    }

    // Per-package chosen consolidation target (persists across re-renders while the dialog is open).
    private readonly Dictionary<string, string> consolidateChoice = new(StringComparer.OrdinalIgnoreCase);

    // Version options to consolidate onto: the versions already in use plus the latest, if newer.
    private IReadOnlyList<string> ConsolidateOptions(InstalledGroup group)
    {
        List<string> options = group.DistinctVersions.ToList();
        string? latest = LatestFor(group.PackageId);
        if (latest is not null && !options.Contains(latest, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(latest);
        }

        return options
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // The currently selected target for a package — defaults to the highest option (latest / newest in use).
    private string ConsolidateTarget(InstalledGroup group)
    {
        if (consolidateChoice.TryGetValue(group.PackageId, out string? chosen) && chosen.Length > 0)
        {
            return chosen;
        }

        return ConsolidateOptions(group).FirstOrDefault() ?? string.Empty;
    }

    private Task ConsolidateAsync(InstalledGroup group, string targetVersion)
    {
        string[] targets = group.Uses.Select(u => u.ProjectPath).ToArray();
        return RunMutationAsync(() => Packages.Install(Workspace.Settings, targets, group.PackageId, targetVersion));
    }

    // ---- shared mutation pipeline ----

    private async Task RunMutationAsync(Func<NuGetPackageService.PackageMutationResult> operation)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        statusMessage = null;
        statusDiagnostics = [];
        StateHasChanged();

        NuGetPackageService.PackageMutationResult result = await Task.Run(operation);

        statusIsError = result.IsError;
        statusMessage = result.Message;
        statusDiagnostics = result.Diagnostics;

        if (!result.IsError)
        {
            // No reindex: the package change restored assets for the next build but altered no user
            // symbols. Just re-read the live package views (independent of the code index).
            await Task.WhenAll(RefreshInstalledAsync(), RefreshOutdatedAsync());
        }

        busy = false;
        StateHasChanged();
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string FormatDownloads(long? downloads) => downloads switch
    {
        null => "—",
        >= 1_000_000_000 => $"{downloads.Value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{downloads.Value / 1_000_000d:0.#}M",
        >= 1_000 => $"{downloads.Value / 1_000d:0.#}K",
        _ => downloads.Value.ToString(),
    };

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && !busy)
        {
            DialogService.Close(null);
        }
    }
}
