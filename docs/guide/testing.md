# Testing

```powershell
dotnet build ClaudeWorkbench.slnx
dotnet test  ClaudeWorkbench.slnx
```

**227 tests: 227 pass · 0 skipped · 0 failed.** Everything runs under `dotnet test`. There are no
console runners, no manual flags, and nothing that has to be remembered — see
[why that matters](#why-there-are-no-console-runners) below.

The table most people want is not "one row per project", because the project layout answers *where
code lives*, not *what is covered*. Grouped by capability (per-project totals: Data 72 · MSBuild 7 ·
Indexing 6 · Core 6 · Logging 3 · Host 15 · Workflow 46 · Integration 72):

| # | Capability | Tests | Where |
|---|---|---|---|
| A | [Semantic index & language coverage](#a-semantic-index--language-coverage) | 68 | `Data.Tests`, `MSBuild.Tests`, `Indexing.Tests` |
| B | [Edit workflow & staging](#b-edit-workflow--staging) | 34 | `Workflow.Tests` |
| C | [Review gates & decisions](#c-review-gates--decisions) | 23 | `Workflow.Tests`, `Indexing.Tests`, `Integration.Tests` |
| D | [MCP tool surface](#d-mcp-tool-surface-out-of-process) | 50 | `Integration.Tests`, `Data.Tests` |
| E | [Sample-driven authoring](#e-sample-driven-authoring-claudesmokes) | 19 | `Data.Tests`, `Workflow.Tests` |
| F | [Host & infrastructure](#f-host--infrastructure) | 25 | `Host.Tests`, `Core.Tests`, `Logging.Tests`, `Integration.Tests` |
| G | [Agent-loop end-to-end (real builds)](#g-agent-loop-end-to-end-real-builds) | 8 | `Integration.Tests` |
| | **Total** | **227** | |

---

## A. Semantic index & language coverage

*Can the agent see the code correctly?* Everything downstream depends on this being right, and its
failure mode is quiet: the index looks healthy and the answer is simply empty.

| Suite | Tests | Covers |
|---|---|---|
| `LanguageCorpusTests` | 37 | C# constructs, each bound by an independent Roslyn oracle and compared against the index on symbol identity, reference count, caller count, relationship kind, and exact line/column. Covers `operator +`, conversion operators, indexers, explicit interface implementations, method-group assignment, local functions, partials, global usings. |
| `FixtureIndexMatrixTests` | 8 | One case per symbol shape (instance/static method, property, field, event, base type, extension method); asserts `expected == roslyn == aimonitor` three ways. |
| `MSBuildWorkspaceLoaderTests` | 5 | Project/document loading through real MSBuild. |
| `SolutionIndexQueryServiceTests` | 5 | Scoped queries: solution, namespace, folder, file. |
| `SolutionIndexStoreTests` | 4 | SQLite row round-trips, including package references. |
| `SolutionIndexBuilderTests` | 2 | Build-to-store pipeline. |
| `SolutionIndexDatabaseSchemaVersionTests` | 2 | Schema version gating. |
| `RazorCodeBehindIndexingTests` | 1 | Every `.razor.cs` is indexed **and** contributes symbols. |
| `RazorGeneratorEnvironmentDiagnostic` | 1 | Reports Razor generator/Roslyn version coupling. |
| `IndexingBoundaryTests` | 1 | What is in and out of the index boundary. |
| `IndexDbDumpTests` | 1 | Diagnostic dump shape. |
| `MonitorDataPathsTests` | 1 | Monitor-owned data paths. |

## B. Edit workflow & staging

*Can an edit be prepared without touching watched source?*

| Suite | Tests | Covers |
|---|---|---|
| `WorkflowEditServiceSafetyTests` | 22 | Path containment, working-copy isolation, staging guards, refusals. |
| `RoslynEditServiceSourceMapTests` | 6 | Source-map fidelity for symbol-level edits. |
| `WorkflowEditServiceRecordStoreTests` | 4 | Staged-record persistence and the in-memory cache's write-through to disk. |
| `RoslynEditServiceOutlineTests` | 2 | File outline extraction. |

## C. Review gates & decisions

*Does nothing reach watched source without passing the gates?*

| Suite | Tests | Covers |
|---|---|---|
| `EngineEditLifecycleTests` | 11 | Full refresh → stage → review → decide lifecycle: `accepted-normalized` (CRLF vs LF), Razor and CSS round-trips, new-file create-then-clean, watched-relative path resolution, and the five pre-merge validation gates (errors block, warnings don't, staged-hash mismatch, multi-file compile error, runtime exclusion). |
| `StagedDecisionWorkflowTests` | 5 | Decision recording and post-accept index refresh. |
| `EngineReviewSessionAtomicityTests` | 4 | **ADR-0005**: one reject invalidates the whole session — a non-terminal accept followed by a reject leaves *every* file unwritten. |
| `ReviewDecisionClassifierTests` | 3 | `accepted` / `accepted-normalized` / `rejected` / `dirty-unexpected` classification. |

`EngineEditLifecycleTests` constructs a **new `WorkflowEditService` at every seam**. That is
load-bearing, not style: the service caches staged records in memory, so reusing one instance
satisfies every guard from the warm cache and silently stops testing disk rehydration while
still passing. This was verified by mutation — see the class comment.

## D. MCP tool surface (out-of-process)

*Does the surface the agent actually speaks to behave?* These boot a real server process and speak
real JSON-RPC across the MCP tool surface (71 tools: 64 `AIMonitorTools` + 3 `TaskMcpTools` + 4
`GitMcpTools`).

| Suite | Tests | Covers |
|---|---|---|
| `McpServerSmokeTests` | 25 | Tool registration, manifest, discovery and mutation tools, session lifecycle, telemetry. |
| `McpReadIndexSurfaceTests` | 10 | Read-side index tools, including `find_references_in_file` and `list_package_references`. |
| `McpPlannedSessionSurfaceTests` | 9 | Planned sessions: staging, rejection, and plan-complete gating — submits are **syntax-only and carry no overlay result** (asserted: no `overlayValidation` on submit); the real build runs once, at `complete_edit_plan`, after every planned file exists. |
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

*Do real fixture solutions in `samples/watched-solutions/` survive the loop?* 19 tests across
Blazor, WinForms, Razor and harness samples — authoring workflows, materialization, source maps,
`dirty-unexpected` handling, and validation.

## G. Agent-loop end-to-end (real builds)

*Does a full author→submit→validate→fix loop actually converge on a real build?*
`AgentLoopSampleWorkflowTests` (8, all `[Trait("Suite","AgentLoop")]`, in `Integration.Tests`) drive
a scripted agent through the governed edit loop against real sample solutions and run the **real
`dotnet build`** at plan-complete (`WorkflowEditService.ValidatePlannedOverlayBuild`), asserting on
both the emitted source and the workflow artifacts. The scenarios use the `test-prompts/` fixtures
under `samples/watched-solutions/CalculatorSample` and `.../BlazorSample` (including a
deliberately-broken edit that must fail the build, then a follow-up fix that must make it pass).
These are the tests that push `Integration.Tests` to 72.

`samples/watched-solutions/MixedTfmSample` (net8 console + net9 WinForms + net10 Blazor + a shared
net8 library, wired via `appsettings.mixed-tfm.json`) exists as a **multi-TFM** overlay-build
fixture for cross-target validation and live dogfooding; it is not one of the automated AgentLoop
scenarios.

## F. Host & infrastructure

| Suite | Tests | Covers |
|---|---|---|
| `GitServiceTests` | 15 | Git panel operations (argv, no shell — **ADR-0004**). |
| `MonitorSettingsLoaderTests` · `MonitorSettingsTests` · `MonitorWorkspacePathsTests` | 6 | Settings resolution and workspace-relative paths. |
| Logging (3 suites) | 3 | JSON-lines sink, log paths, in-proc log service. |
| `RepositoryShapeTests` | 1 | Repository layout invariants. (Physically in `Integration.Tests`, grouped here by capability.) |

---

## Build-time dead-code analysis

Beyond `dotnet test`, the build itself carries a **dead-code / code-style gate**. The repo-root
`Directory.Build.props` sets `<EnableNETAnalyzers>` + `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`,
and the root `.editorconfig` raises the "this is never used" rules — IDE0051 (unused private members),
IDE0052 (write-only fields), IDE0059 (dead assignments), IDE0060 (unused parameters, `= all`) — to
**warnings**. They surface at `dotnet build` without a `TreatWarningsAsErrors`, so dead code is
reported but does not break the loop. The watched-solution fixtures under `samples/` deliberately opt
out (`samples/watched-solutions/Directory.Build.props` sets `EnforceCodeStyleInBuild=false`), since
several carry intentionally-unused members for edit scenarios. This gate exists because AI-authored
code accretes dead code in a way hand-written code does not; the build should surface it continuously.

## Why there are no console runners

Until the retirement documented in
[the plan](../plans/retire-legacy-test-harness.md), the repo also carried `AIMonitor.Cli` and three
console `Main` runners (~3,350 lines). **None of them could fail a build.** The language corpus
exited 0 unless `--assert` was passed, which is why 42 real fixture cases never caught anything.
`AIMonitor.SmokeTests` found zero samples on any machine but one developer's and returned 0 having
asserted nothing. `AIMonitor.ToolSmokeTests` drove a project that has never existed in this repo.

Everything they were reaching for was moved into `dotnet test` **before** they were deleted. The
corpus is category A above; the Razor sweep, fixture matrix, planned-session surface and rename
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
