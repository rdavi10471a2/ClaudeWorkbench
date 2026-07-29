# 0007 — The index rides the build (build once, index over its output)

**Status:** Accepted (design) · Implementation pending (sync-gated) · **Date:** 2026-07 · **Applies to:** the post-accept index build (`MSBuildWorkspaceLoader`, `SolutionIndexBuilder`, the accept flow)

## Context

At a terminal accept the loop compiles the same source **three** times (verified from the
compile-index provenance trace, one real run, all UTC):

1. **Terminal gate-build** — a real out-of-proc `dotnet build` on the **persistent mirror**
   (`PreMergeValidationService`, `validation-workspace`). Validates the candidate set before any
   byte reaches watched source. `05:54:34 → 05:54:44`.
2. **Index-compile** — the index's **own** pass over the **real tree** (`MSBuildWorkspaceLoader`):
   `MSBuildWorkspace` evaluation (out-of-proc BuildHost) + Roslyn semantic model + **its own Razor
   generation**. `05:54:44 → 05:54:57`.
3. **Build-after-accept** — a real out-of-proc `dotnet build` on the **real tree**, producing the
   runnable exe. `→ 05:55:06`.

Two of those (2 and 3) compile the **real tree**. And #2 is the problem child on two counts:

- **Its Razor generation is compile-less and therefore approximate.** It builds a *default* Razor
  engine — `RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem, SetRootNamespace)`
  ([MSBuildWorkspaceLoader.cs:1730](../../src/AIMonitor.MSBuild/MSBuildWorkspaceLoader.cs#L1730)) —
  with **no compilation, no references, no tag-helpers** fed in. It manufactures generated Razor C#
  *without a compile*, so it cannot resolve component/tag-helper types the way the real build's
  Razor source generator (which runs **inside csc** with the full reference set) does. Leaf markup
  is fine; component composition is a degraded shadow.
- **It is the BuildHost handle-holder.** `MSBuildWorkspace`'s out-of-proc BuildHost is the process
  that pins the real tree's `obj/bin` (the file-locking family; see the file-locking diagnosis).

The index step is still necessary — neither `dotnet build` produces a queryable **symbol index**
(SQLite). But the index does not need to *compile* or *generate Razor* itself. It needs a semantic
model over source + generated text + references — all of which a real build already produces.

## Decision

**The real build-after-accept becomes the single real compile on the real tree, and the index is a
Roslyn semantic pass over its output — not a compile.**

- The build-after-accept runs with `-p:EmitCompilerGeneratedFiles=true`, so the SDK's Razor source
  generator writes the generated C# (`*.g.cs`) to `obj/…/generated` — **generated inside the
  compile, with full references/tag-helpers, i.e. accurate**.
- The resolved reference set is harvested from that same build (`@(ReferencePathWithRefAssemblies)`
  via a dump target, or a binlog).
- The index builds a `CSharpCompilation` over **{ real source `.cs` + the build's generated `.g.cs` }**
  with those references, and extracts symbols. **No `RazorProjectEngine`, no `MSBuildWorkspace`
  evaluation, no BuildHost.**

**Ordering invariant (non-negotiable):**

```
mirror gate-build (validate candidates)  →  write real source  →  REAL build-after-accept (emits .g.cs + exe)  →  index over { real source + generated .g.cs } + refs
```

The index runs **dead last**, because the generated files do not exist until the build emits them:
**no build → no generated files → no index.** And it reads the **real** build's output, so every
path is a real source path — **no mirror→real remap** (that is the reason to ride the real build,
not the mirror gate-build).

## Why

- **Correctness:** the build's in-compile Razor generation resolves components/tag-helpers; the
  index's compile-less generation cannot. Riding the build makes the index *more* accurate, not just
  cheaper.
- **One real-tree pass, not two:** the index stops compiling; #2 collapses into #3.
- **Kills the handle-holder:** no `MSBuildWorkspace`/BuildHost from the index → the real tree is no
  longer pinned by indexing (dissolves that slice of the file-locking family).
- **No remap:** real build → real paths → the index stores real source paths, as everything
  downstream expects.
- **SDK-coupling (dotnet/roslyn#84137) is moot:** the build is the SDK's own compile; the index only
  reads its outputs.

## Proof (already green)

- `IndexFromBuildOutputsTests` — builds BlazorSample with `EmitCompilerGeneratedFiles`, harvests the
  reference set, builds a Roslyn compilation over { source + generated `.g.cs` } + refs, and asserts
  it compiles clean and resolves a plain type (`Customer`), a Razor-generated type (`CustomerList`),
  **and a fully-disciplined component** (`CustomerCard` — markup + code-behind merged into one type,
  carrying the code-behind `[Parameter]`), plus the scoped-CSS isolation artifact.
- `IndexFindReferencesTests` — builds the real index over BlazorSample and asserts
  `SolutionIndexQueryService` returns queryable references at real source locations.

Both pass. The mechanism is not hypothetical; only the production wiring remains.

## Consequences / open decisions

- **The index now depends on a build.** When "Build after accept" is unchecked, and for the
  **startup warm-up** and **manual Rebuild Index** (which have no build), there is no fresh `.g.cs`.
  Decision needed: either those paths trigger a build first, or the current self-contained loader is
  **retained as the no-build fallback** (approximate Razor, but better than nothing). Leaning:
  retain it as fallback; ride-the-build is the accept-path default.
- **Reorder the accept flow:** index moves to *after* build-after-accept (today it is before).
- **Ref harvest:** the `@(ReferencePathWithRefAssemblies)` dump target is proven in the test;
  a binlog is the alternative. Pick one.
- **Rollout:** add the build-output index path **behind a flag, off by default**, verify parity
  against the current index on the samples (symbol/reference counts, disciplined-component
  resolution), then flip. Never rip out the old loader before the new one is proven at parity.
- **Sync-gate:** this lands on `MSBuildWorkspaceLoader` / `SolutionIndexBuilder` / the accept flow —
  the highest-collision files. Sync the other shift and rebase before implementing.

## Before the flag is removed — convergence checklist (review 2026-07-29)

The mechanism is proven and, behind `CWB_INDEX_RIDES_BUILD` (off), the default path is untouched
and safe. The items below are the gap between "proven side-by-side" and "flag removed, whole-solution
ride is the only index path." They are gates on flipping the default — not defects in the flagged
feature. Grouped structural → convergence → correctness. Pair this with the side-by-side parity run
before planning the flip.

**Scope note (decided):** multi-*targeting* — one project emitting several frameworks via plural
`<TargetFrameworks>` — is **out of scope**. Only single-TFM projects are supported; a project is
built and indexed under the one framework it targets. Multi-*project* solutions of single-TFM
projects (e.g. `MixedTfmSample`: net8 console + net9 WinForms + net10 Blazor + net8 lib) remain fully
in scope and are the default target. The `FindGeneratedRoot` / `ReadPerProjectReferences`
`FirstOrDefault` picks are therefore correct (one TFM folder per project) — but make the single-TFM
assumption **explicit**: if a project ever declares plural `TargetFrameworks`, pick its first
declared TFM and log it, rather than silently indexing an arbitrary framework's output.

### Structural — build these (the ride is not yet the whole story)

- [ ] **A. Accept-time ride is single-project only.** `EngineReviewWorkflow` sets `ridesBuild` only
  when `WatchedSolutionInfo.ResolveSingleProject(...)` is non-null, and `SolutionBuildService.Build`
  harvests `GeneratedRoot`/`HarvestedReferences` for that one project only. On a multi-project
  solution the *accept* silently falls back to the in-proc reindex — the exact case meant to be the
  default. Needs: whole-solution harvest (per-project `generated/` + per-project refs) on the
  build-after-accept, and a multi-project `RebuildFromBuildOutputAsync`. Today "multi-project ride"
  exists only for warm-up / manual `RebuildAsync`, not for accepts.
- [ ] **B. The per-file refresh still spins a BuildHost — and is not flag-gated.**
  `SolutionIndexBuilder.RefreshProjectFilesAsync` → `MSBuildWorkspaceLoader.OpenProjectFilesAsync`
  uses in-proc `MSBuildWorkspace` (out-of-proc BuildHost) on every project-scoped refresh, producing
  the *approximate* compile-less Razor the ADR set out to kill. With the flag gone this leaves two
  fidelity regimes coexisting and keeps a handle-holder alive. Decide: convert file-refresh to read
  build output too, or retire it in favour of the whole-project ride.

### Convergence — one path, not two ("double-identity" cleanup)

- [ ] **C. Fold the single-project loader into the whole-solution loader.** `ResolveAllProjects`
  already yields `[the-one-csproj]` for a single project and `OpenSolutionFromBuildAsync` handles
  N ≥ 1, so `OpenProjectFromBuildAsync` exists only to special-case N = 1 — and it is the buggier
  variant: its ref-dump target uses `BeforeTargets="CoreCompile"`, which MSBuild **skips on an
  up-to-date build** (the common warm-up / manual-rebuild case) → empty reference set → the Roslyn
  compilation resolves no external types → degraded/empty index. The whole-solution path already
  fixed exactly this with `AfterTargets="ResolveReferences"` (runs even when up-to-date). Routing
  everything through the whole-solution machinery deletes the divergent path **and** the up-to-date
  empty-refs bug in one move. Same applies to `SolutionBuildService.Build`'s single-project
  `CoreCompile` dump on the accept path.

### Correctness — mechanical must-fixes (today hidden behind the flag)

- [ ] **`BuildOutputSnapshotLoader.RunDotnet` can deadlock and leaks on timeout.** Sequential
  `StandardOutput.ReadToEnd()` then `StandardError.ReadToEnd()` → if the child fills its stderr pipe
  buffer while we drain stdout, it blocks and never exits; and on the 5-min timeout the process is
  never `Kill()`ed, so a wedged `dotnet build` keeps pinning `obj/bin`. Reuse the safe
  concurrent-drain + kill-tree logic already in `SolutionBuildService.RunProcess`.
- [ ] **A failed build still overwrites the index.** `SolutionIndexBuilder.RebuildAsync` saves
  `buildResult.Snapshot` without checking `buildResult.BuildSucceeded` (it only logs it), so a
  transient build failure during warm-up / manual rebuild replaces a good index with one built from
  missing generated files + empty refs. Fall back to the in-proc loader (or keep the prior index)
  when the build fails — mirror the accept path, which already guards on `IsError`.
- [ ] **`CollectSourceFiles` indexes files the real compile excludes.** It globs every `*.cs` under
  the project directory instead of the project's evaluated `Compile` item set, so `<Compile Remove>`
  files, and any `.cs` in a nested/sibling project's folder, get indexed as if they were in the
  assembly (phantom symbols; wrong project attribution when project dirs nest). Take the file list
  from the MSBuild evaluation already performed, or at least exclude nested project cones.
- [ ] **Regex-scraped project metadata.** `ReadRootNamespace`, `ParseProjectReferences`, and
  `WatchedSolutionInfo.EnumerateProjects` parse raw csproj/sln text — missing computed values
  (`Directory.Build.props`, globbed `ProjectReference`s) and matching commented-out entries.
  `MSBuildEvaluatedProject.Load` is already called for metadata; read these from the evaluated
  project instead.

### Verified good (no action)

- Ride ordering in `EngineReviewWorkflow` (defer index → build-after-accept → read-from-output, with
  a clean `IsError` / empty-`GeneratedRoot` fallback).
- `RazorDocumentIndex.BuildFromGeneratedFiles`: `#pragma checksum` → `.razor` recovery and
  `GetMappedLineSpan` mapping via `#line` directives.
- `CompileIndexTrace` is best-effort-safe (never breaks a build/index); the `Echo` sink wiring.
- `find_project_dependencies` reads the persisted `project_references` graph (both directions).
- Test coverage: build-output parity, find-references-after-rebuild, accept ordering, multi-project.

## Checklist progress (2026-07-29, continued)

Worked the review findings against a **side-by-side parity run on the real 17-project workbench solution**
(`ClaudeWorkbench - Copy`, incl. the Blazor Host and the WPF Launcher). Parity verdict, both before and after
the convergence below: **GREENLIGHT — 0 symbol regressions, 0 dropped projects; every customer/src project at
EXACT parity (symbols, references, documents), WPF Launcher included.** The only divergence is
`ClaudeWorkbench.Host` (new over-collects) — traced entirely to a test artifact: the copy has the workbench's
own `runtime/` per-workspace state nested under the Host project (CalculatorSample copies, validation
snapshots). That won't exist in a customer solution; the exact parity on all other projects confirms the glob
isn't biting real code. Test projects were excluded from the gate (not customer-facing).

Done:
- [x] **#1** `RunDotnet` deadlock/leak — concurrent drain + kill-tree (`2cd1399`).
- [x] **#2** failed build no longer overwrites the index — `RebuildAsync` uses the build-output snapshot only
  when `BuildSucceeded`, else falls back to the in-proc loader (`d0dc1b2`).
- [x] **C** one build-output path for 1..N — `RebuildAsync` routes through `OpenSolutionFromBuildAsync`
  (`ResolveAllProjects` yields `[the-one]` for a single project); deleted the single-project
  `OpenProjectFromBuildAsync`/`BuildProjectSnapshotAsync` and their `BeforeTargets=CoreCompile` dump. The
  per-project refs dump is `AfterTargets=ResolveReferences` (survives up-to-date builds), shared from
  `IndexRidesBuild.WritePerProjectRefsTargetsFile` (`d0dc1b2`, `c4ab7c7`).
- [x] **A** accept-time ride is now 1..N, not single-project — `EngineReviewWorkflow.ridesBuild` gates on
  `ResolveAllProjects().Count>=1`; the build-after-accept emits every project's output; the reindex reads the
  whole solution via `ReadSolutionSnapshotAsync`. No single-project and no single-file special case in the ride
  path (`c4ab7c7`).

More done:
- [x] **#3** index the evaluated `@(Compile)` set (`MSBuildEvaluatedProject.CompileFiles`) + build-time obj
  helpers, not a directory glob. Parity re-run: eliminated the Host's `runtime/` over-collection entirely
  (docs 226→84 exact; symbols 1817→1120, new +1 real win), and **halved build+read time (51s→27s)** by not
  reading stray files. Makes the index robust to stray `.cs` regardless of the artifact (`8879418`).
- [x] **#4** `RootNamespace` + `<ProjectReference>` graph read from the evaluated project, not regex; deleted
  `ReadRootNamespace`/`ParseProjectReferences` (`8879418`).

Even more done:
- [x] **index configuration is Debug by design.** The index (and the accept-flow build-after-accept that
  feeds it) always builds `IndexRidesBuild.IndexBuildConfiguration` (`"Debug"`) — one consistent view of the
  code. This closes a latent build/read asymmetry from the A fix (the build took a caller config while the
  read defaulted to Debug — a `Release` accept would have read `obj/Debug`). `IReviewWorkflow.Accept` now takes
  `bool buildAfterAccept` instead of `string? buildConfiguration`; the merge dialog passes a yes/no and no
  longer knows configurations exist. Debug/Release is a **Source-tab-only** concern (build/run the app to
  test), which never feeds the index. Caveat (accepted): `#if RELEASE`-only code is not indexed.

Remaining (hardening, not blockers):
- [ ] **B** the per-file `RefreshProjectFilesAsync` → `OpenProjectFilesAsync` BuildHost path is now
  fallback-only (flag-off / failed-build); delete it when the flag flips / the old loader is removed.
- [ ] **single-TFM assumption** — make explicit: plural `<TargetFrameworks>` → pick first + log.
