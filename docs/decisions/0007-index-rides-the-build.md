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
