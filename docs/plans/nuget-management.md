# NuGet management on the Source tab (by project and by solution)

Status: DESIGN (branch `nuget-integration`). Not built.

## Goal

Give the operator a first-class NuGet surface on the Source tab — browse/install/update/
uninstall packages, scoped **per project** and **across the whole solution** (VS's two entry
points: "Manage NuGet Packages" and "Manage NuGet Packages for Solution"), including the
**consolidate** case where one package is pinned at different versions across projects.

## What already exists (reuse, don't reinvent)

- **Host-run `dotnet` subprocess services** — `ProjectScaffoldService` (`dotnet new`/`sln add`)
  and `SolutionRestoreService` (`dotnet restore`). Both drive the real SDK out-of-process with a
  shared `RunProcess` that redirects stdout/stderr, times out, and `Kill(entireProcessTree:true)`s
  with `-nodeReuse:false` (the file-locking lesson — never leave MSBuild nodes pinning files).
  The NuGet service is the same shape.
- **The index already stores package references** — `IndexedPackageReferenceRow(ProjectPath,
  Include, Version)` and `SolutionIndexQueryService.ListPackageReferences()`. The "Installed" view
  reads from the index (fast, already populated by the ride-the-build reindex) — no new parse.
- **`restore_solution` MCP tool** and host-side auto-restore (workspace open / post-accept).
- **Operator-action precedent** — "Add project" is a top-toolbar button that mutates the real tree
  directly (scaffold → `sln add` → restore), *outside* the staging/review pipeline. NuGet
  management follows the same door.
- **`WatchedSolutionInfo.ResolveAllProjects`** enumerates the solution's projects (used to populate
  the project scope picker and the solution-wide views).
- **Modal-from-toolbar precedent** — Conversations and Activity are full-surface modals launched
  from a toolbar. The NuGet manager is the same.

## Governance: two doors, each already consistent

Package changes are `.csproj` edits, which raises the ADR-0005 "every source change is a reviewable
edit session" question. Resolve it the way the codebase already has:

- **Operator door (this feature): direct mutation.** The Packages button/modal calls
  `dotnet add|remove package` on the real `.csproj`, then restores and reindexes — exactly like
  "Add project" already writes real files without staging. Simple, matches precedent. **Recommended
  for v1.**
- **Agent door (unchanged): governed edit.** The agent never shells; it edits the `.csproj`
  `<PackageReference>` as a normal staged edit, it goes through Merge Review, and on accept the host
  restores (`restore_solution`). Already works today.

So writes stay governed when the *agent* makes them and direct when the *operator* does — no new
policy, just the existing split.

## Service layer — `NuGetPackageService` (AIMonitor.Workflow)

Same construction as `ProjectScaffoldService`: host-run, C#-only, containment-checked, cached where
cheap. All mutations run from the **solution root** so `NuGet.config` / `Directory.Build.props` /
`global.json` are honored exactly as a normal `dotnet` invocation would (private feeds included).

```
record PackageSearchHit(string Id, string LatestVersion, long? Downloads, string? Description,
                        string? Authors, bool Verified);
record InstalledPackage(string ProjectPath, string ProjectName, string PackageId,
                        string RequestedVersion, string ResolvedVersion, string? LatestVersion,
                        bool IsTransitive, bool IsOutdated, bool IsVulnerable, bool IsDeprecated);
record PackageMutationResult(bool IsError, string Message, IReadOnlyList<string> Diagnostics);

IReadOnlyList<PackageSearchHit> Search(query, prerelease, take)      // dotnet package search --format json
IReadOnlyList<string>           GetVersions(packageId, prerelease)   // NuGet V3 flat-container index.json
IReadOnlyList<InstalledPackage> ListInstalled(settings, scope)       // index + dotnet list package enrich
PackageMutationResult           Install(settings, projectPaths, id, version)   // dotnet add package
PackageMutationResult           Update (settings, projectPaths, id, version)   // dotnet add package -v
PackageMutationResult           Uninstall(settings, projectPaths, id)          // dotnet remove package
```

Mechanism notes:
- **Search**: `dotnet package search <q> --format json [--prerelease] --take N` (SDK 9+/10). Parse
  JSON; degrade to an empty result + message if `dotnet` is missing (same launch-failure handling
  as scaffold).
- **Installed + status**: base list from the index (`ListPackageReferences`), enriched by
  `dotnet list package --format json` with `--outdated`, `--vulnerable`, `--deprecated`. The index
  gives instant paint; the `dotnet list` calls fill the badges asynchronously.
- **Versions dropdown**: NuGet V3 flat-container `.../v3-flatcontainer/{id-lower}/index.json`
  (honor the configured source; fall back to "latest stable / latest prerelease / type a version"
  if the feed can't be reached).
- **Mutate**: `dotnet add <csproj> package <id> [-v <ver>]` / `dotnet remove <csproj> package <id>`,
  looping over `projectPaths` (one project, or all in scope). Then **one** `dotnet restore` +
  reindex for the batch, not per project.
- **Containment**: every target `.csproj` must resolve inside the solution root (identical guard to
  `ProjectScaffoldService.Create`). Package id validated against the NuGet id grammar.

### Central Package Management (must handle)

If the solution has a `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`), `dotnet add
package` puts the version in that file and the per-project `<PackageReference>` carries no version.
Detect CPM up front:
- **Installed/Consolidate** read the effective version from CPM when present (consolidation may be a
  no-op or a single edit to `Directory.Packages.props`).
- **Install/Update** let the SDK do the right thing (it already targets the props file under CPM);
  the UI just labels the scope as "managed centrally" so the operator isn't surprised the version
  didn't land in the `.csproj`.

## UI

### Entry points
1. **"Packages" button** in the Source top toolbar, next to "Add project" (top-right, secondary
   style). Opens the manager modal at **Solution** scope.
2. **Right-click a project node** in the Solution tree → "Manage NuGet packages…" → same modal,
   pre-scoped to that project. (Mirrors VS's per-project entry.)

### The manager modal (full surface, like Conversations)
- **Scope switch** at the top: `Solution` | `<project dropdown>`. One surface covers both VS
  dialogs.
- **Tabs** (VS-familiar):
  - **Browse** — search box + prerelease toggle → results list (id, latest version, downloads,
    verified badge, one-line description). Select a hit → detail pane: version dropdown +
    **target-project checkboxes** (solution scope) or the single scoped project + **Install**.
  - **Installed** — table for the scope: package, requested vs resolved version, latest, and
    per-row **Update** / **Uninstall**. Vulnerable/deprecated/outdated shown as severity chips (the
    same visual language as the index-health chip already in the toolbar).
  - **Updates** — only packages with a newer version; **Update all** for the scope.
  - **Consolidate** *(solution scope only)* — packages installed at differing versions across
    projects; pick a target version, apply to all. This is the headline "by solution" feature.
- **Diagnostics strip** at the bottom — restore/add output on failure, reusing the
  `ScaffoldResult.Diagnostics` list style (NU-code lines surfaced, not a silent non-zero exit).
- **Busy + health**: mutations show a busy state; on completion the host restores + reindexes, and
  if the resulting build fails the existing `IndexHealthMarker` "index blocked — build failing"
  chip lights up (already wired in the toolbar). No new failure surface.

## Post-mutation pipeline

`add/remove` → **one** `dotnet restore` (solution) → reindex (ride-the-build path) → refresh the
Installed/Updates lists from the index. Batch a multi-project apply into a single restore+reindex.
Restore/reindex already exist; this feature only sequences them after the mutation.

## MCP parity (optional, small)

- **`list_packages`** (read-only) — expose `ListInstalled` so the agent can *see* the package graph
  it's editing. Trivial; data's already indexed.
- **Writes stay on the governed `.csproj`-edit + `restore_solution` path** — no `add_package` write
  tool, to keep the agent's single reviewable-edit door.

## Build order / phasing

1. `NuGetPackageService` (search + list-installed read paths) + unit/integration coverage against a
   sample solution (MixedTfmSample already has multiple projects for the consolidate case).
2. Modal shell + Installed tab (index-backed) — usable read-only surface first.
3. Browse + Install/Uninstall (single project), then multi-project (solution) + Consolidate.
4. Updates tab + Update all.
5. CPM handling pass.
6. Optional `list_packages` MCP tool.

## Open questions

- **Version dropdown source** when private feeds are configured without anonymous flat-container
  access — may need `dotnet package search --exact-match` per source instead of the flat-container
  shortcut.
- **Interactive/authenticated feeds** — run non-interactive; surface `NU1301`/auth failures in the
  diagnostics strip rather than hanging (the `Kill(entireProcessTree)` timeout already prevents a
  hang).
- Whether **Consolidate under CPM** should just deep-link to `Directory.Packages.props` in the
  viewer instead of offering a picker (one file, one edit).
