# Testing

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test  ClaudeWorkbench.slnx
```

**224 tests: 218 pass · 6 skipped · 0 failed.** Everything runs under `dotnet test`. There are no
console runners, no manual flags, and nothing that has to be remembered — see
[why that matters](#why-there-are-no-console-runners) below.

The table most people want is not "one row per project", because the project layout answers *where
code lives*, not *what is covered*. Grouped by capability:

| # | Capability | Tests | Where |
|---|---|---|---|
| A | [Semantic index & language coverage](#a-semantic-index--language-coverage) | 74 (5 skipped) | `Data.Tests`, `MSBuild.Tests`, `Indexing.Tests` |
| B | [Edit workflow & staging](#b-edit-workflow--staging) | 35 | `Workflow.Tests` |
| C | [Review gates & decisions](#c-review-gates--decisions) | 22 | `Workflow.Tests`, `Indexing.Tests`, `Integration.Tests` |
| D | [MCP tool surface](#d-mcp-tool-surface-out-of-process) | 50 | `Integration.Tests`, `Data.Tests` |
| E | [Sample-driven authoring](#e-sample-driven-authoring-claudesmokes) | 18 | `Data.Tests`, `Workflow.Tests` |
| F | [Host & infrastructure](#f-host--infrastructure) | 25 | `Host.Tests`, `Core.Tests`, `Logging.Tests` |
| | **Total** | **224** | |

---

## A. Semantic index & language coverage

*Can the agent see the code correctly?* Everything downstream depends on this being right, and its
failure mode is quiet: the index looks healthy and the answer is simply empty.

| Suite | Tests | Covers |
|---|---|---|
| `LanguageCorpusTests` | 42 (5 skipped) | 42 C# constructs, each bound by an independent Roslyn oracle and compared against the index on symbol identity, reference count, caller count, relationship kind, and exact line/column. Covers `operator +`, conversion operators, indexers, explicit interface implementations, method-group assignment, local functions, partials, global usings. |
| `FixtureIndexMatrixTests` | 8 | One case per symbol shape (instance/static method, property, field, event, base type, extension method); asserts `expected == roslyn == aimonitor` three ways. |
| `MSBuildWorkspaceLoaderTests` | 5 | Project/document loading through real MSBuild. |
| `SolutionIndexQueryServiceTests` | 5 | Scoped queries: solution, namespace, folder, file. |
| `SolutionIndexStoreTests` | 4 | SQLite row round-trips, including package references. |
| `SolutionIndexBuilderTests` | 3 (1 skipped) | Build-to-store pipeline. |
| `SolutionIndexDatabaseSchemaVersionTests` | 2 | Schema version gating. |
| `RazorCodeBehindIndexingTests` | 1 | Every `.razor.cs` is indexed **and** contributes symbols. |
| `RazorGeneratorEnvironmentDiagnostic` | 1 | Reports Razor generator/Roslyn version coupling. |
| `IndexingBoundaryTests` | 1 | What is in and out of the index boundary. |
| `IndexDbDumpTests` | 1 | Diagnostic dump shape. |
| `MonitorDataPathsTests` | 1 | Monitor-owned data paths. |

**The 5 skips are all corpus cases** the harness cannot express: it synthesises a single project,
and those cases need more than one. Each is skipped individually with that reason so the gap stays
countable instead of disappearing. **The 6th skip** (in `SolutionIndexBuilderTests`) is the
`razor-generated:*` reference rows, which only index when the host Roslyn matches the SDK's Razor
source generator — environment-dependent, so documented rather than pinned.

## B. Edit workflow & staging

*Can an edit be prepared without touching watched source?*

| Suite | Tests | Covers |
|---|---|---|
| `WorkflowEditServiceSafetyTests` | 23 | Path containment, working-copy isolation, staging guards, refusals. |
| `RoslynEditServiceSourceMapTests` | 6 | Source-map fidelity for symbol-level edits. |
| `WorkflowEditServiceRecordStoreTests` | 4 | Staged-record persistence and the in-memory cache's write-through to disk. |
| `RoslynEditServiceOutlineTests` | 2 | File outline extraction. |

## C. Review gates & decisions

*Does nothing reach watched source without passing the gates?*

| Suite | Tests | Covers |
|---|---|---|
| `EngineEditLifecycleTests` | 11 | Full refresh → stage → review → decide lifecycle: `accepted-normalized` (CRLF vs LF), Razor and CSS round-trips, new-file create-then-clean, watched-relative path resolution, and the five pre-merge validation gates (errors block, warnings don't, staged-hash mismatch, multi-file compile error, runtime exclusion). |
| `StagedDecisionWorkflowTests` | 5 | Decision recording and post-accept index refresh. |
| `EngineReviewSessionAtomicityTests` | 3 | **ADR-0005**: one reject invalidates the whole session — a non-terminal accept followed by a reject leaves *every* file unwritten. |
| `ReviewDecisionClassifierTests` | 3 | `accepted` / `accepted-normalized` / `rejected` / `dirty-unexpected` classification. |

`EngineEditLifecycleTests` constructs a **new `WorkflowEditService` at every seam**. That is
load-bearing, not style: the service caches staged records in memory, so reusing one instance
satisfies every guard from the warm cache and silently stops testing disk rehydration while
still passing. This was verified by mutation — see the class comment.

## D. MCP tool surface (out-of-process)

*Does the surface the agent actually speaks to behave?* These boot a real server process and speak
real JSON-RPC — 166 protocol calls across ~60 distinct tools.

| Suite | Tests | Covers |
|---|---|---|
| `McpServerSmokeTests` | 25 | Tool registration, manifest, discovery and mutation tools, session lifecycle, telemetry. |
| `McpReadIndexSurfaceTests` | 10 | Read-side index tools, including `find_references_in_file` and `list_package_references`. |
| `McpPlannedSessionSurfaceTests` | 9 | Planned sessions: staging, rejection, and the overlay state machine (`planned-overlay-pending` until every planned file exists). |
| `ClaudeSmokesPhase1McpTests` | 3 | Phase-1 tool behaviour over the samples. |
| `McpRenameDiscoverySurfaceTests` | 1 | A cross-file rename accepted through the real session path; a rebuilt index must still discover **both** external consumers. |
| `McpSurfaceIndexVerificationTests` | 1 | Index agreement across the surface. |
| `McpVsGrepTokenBenchmarkTests` | 1 | Token cost of indexed lookup vs grep. |

**There is deliberately no `AIMonitor.McpServer.Tests` unit project.** `AIMonitorTools` is a thin
attribute-decorated wrapper over engine services that already have unit tests. Calling those
methods in-process would exercise almost nothing the wrapper owns while skipping everything that
actually breaks in it: tool registration and naming, JSON-RPC serialization, `ResolveWatchedPath`
translating watched-relative paths, and the operator gate. Those are only observable across a
process boundary, so the coverage lives where the failures are.

## E. Sample-driven authoring (ClaudeSmokes)

*Do real fixture solutions in `samples/watched-solutions/` survive the loop?* 18 tests across
Blazor, WinForms, Razor and harness samples — authoring workflows, materialization, source maps,
`dirty-unexpected` handling, and validation.

## F. Host & infrastructure

| Suite | Tests | Covers |
|---|---|---|
| `GitServiceTests` | 15 | Git panel operations (argv, no shell — **ADR-0004**). |
| `MonitorSettingsLoaderTests` · `MonitorSettingsTests` · `MonitorWorkspacePathsTests` | 6 | Settings resolution and workspace-relative paths. |
| Logging (3 suites) | 3 | JSON-lines sink, log paths, in-proc log service. |
| `RepositoryShapeTests` | 1 | Repository layout invariants. |

---

## Why there are no console runners

Until the retirement documented in
[the plan](../plans/retire-legacy-test-harness.md), the repo also carried `AIMonitor.Cli` and three
console `Main` runners (~3,350 lines). **None of them could fail a build.** The language corpus
exited 0 unless `--assert` was passed, which is why 42 real fixture cases never caught anything.
`AIMonitor.SmokeTests` found zero samples on any machine but one developer's and returned 0 having
asserted nothing. `AIMonitor.ToolSmokeTests` drove a project that has never existed in this repo.

Everything they were reaching for was moved into `dotnet test` **before** they were deleted. The
corpus is category A above; the Razor sweep, fixture matrix, overlay state machine and rename
discovery are in A and D; 11 engine-lifecycle facts are in C.

The rule that came out of it: **a check that cannot fail is not coverage.** If something is worth
asserting, it belongs in `dotnet test` where a red build stops the work.

## Known gaps

Honest list, so nothing here reads as more covered than it is.

- **ADR-0006 (never-auto-approvable tools) has no automated test.** The rule is enforced in the
  Node sidecar (`sidecar/src/index.ts`, checked *before* auto-approve). The sidecar has one smoke
  test, run via `npm run smoke`, and it is not part of `dotnet test`.
- **No concurrency or lock coverage** for simultaneous sessions against one workspace.
- **Staged-record supersede semantics** (staging the same file twice in a session) are untested.
- **The language corpus fixtures are read from the source tree** (`tests/unit/AIMonitor.Data.Tests/Corpus`)
  by walking up to the repo root, so those tests would not run from a packaged output.
