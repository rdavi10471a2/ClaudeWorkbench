# Plan — retire the legacy test harness

**Status:** in progress · **Date:** 2026-07-20 · **Built from:** four read-only audits of `CliIndexQueryTests`, the MCP integration suite, the `AIMonitor.Cli` deletion surface, and the three smoke runners.

## Progress

Revert point before any of this: **`b8a1966`**. Each phase is its own commit.

| Phase | State | Commit |
|---|---|---|
| 0.1 — add the two missing MCP tools (+tests) | ✅ done | `b8a1966` |
| 0.2 — ADR-0003 dated note | ✅ done | `35e10af` |
| 0.3 — preserve redaction/telemetry (Appendix A) | ✅ done | `35e10af` |
| 1 — language corpus → `dotnet test` | ✅ done | `e434748` (swept) |
| 2 — salvage 3 scenarios from `ToolSmokeTests` | in progress | |
| 3 — re-home the CLI-unique tests | ⚠️ code done, mutation-check outstanding | `e434748` (swept) |
| 5.1a — salvage the Razor code-behind sweep | ✅ done | see below |
| 4 — delete `AIMonitor.Cli` | blocked on 3 | |
| 5 — delete the smoke runners | blocked on 1, 2, 5.1a | |
| 6 — documentation sweep | blocked on 4 | |

## Why

The engine has three entry points: **MCP** (what the agent uses), the **Blazor host** (what the operator uses), and **`AIMonitor.Cli`** (what nothing uses except its own tests). The CLI is not merely unused — it is *semantically different* from production, and that difference produced two false readings in a single evening:

- `edit record-decision` assumes an external tool already merged the file (the retired WinMerge model), so an accept through the CLI left a **0-byte file** and looked like a broken write path.
- The CLI resolves paths with `Path.GetFullPath` against the process working directory; the MCP layer resolves them against the watched folder via `ResolveWatchedPath`. Behaviour attributed to the engine actually belonged to the wrapper.

A harness whose semantics differ from production does not merely fail to help. It produces **confident wrong answers** — the same defect class as a validator that always emits a false diagnostic. That is the case for removal.

The smoke runners have rotted further: **8 of `ToolSmokeTests`' 12 modes drive a project (`src/AIMonitor.McpStdioBridge`) that does not exist in this repository or anywhere in its git history.**

## What the audits established

### The objection that dissolved

The strongest argument for keeping the CLI was that it provides out-of-process coverage — a fresh `WorkflowEditService` per invocation, so staged records must genuinely rehydrate from disk rather than being served from a warm in-memory cache.

**The MCP suite already does this.** Every MCP test runs the server out-of-process over stdio, and six of them (`ReconnectAfterInAppReviewAsync`) restart the server mid-flow, forcing rehydration — each exercising it twice, once through the in-process review simulator and once through the restarted server. The property is covered, through the path production actually uses.

### What only the CLI tests cover

| Behaviour | Notes |
|---|---|
| `accepted-normalized` classification (LF vs CRLF only) | MCP covers `accepted`, `rejected`, `dirty-unexpected` — not the normalized-hash branch |
| Razor **and** CSS full round-trip to accept | MCP covers Razor *refusals* only; no non-C# accept anywhere |
| New-file: review-prep creates a blank watched file, reject deletes it | MCP covers "reject leaves it absent", not the create-then-clean cycle |
| Watched-relative path resolution for file-scoped index queries | Not asserted on the MCP side |
| The five `Edit_premerge_validation_*` tests | **Already engine-direct** — the CLI only refreshes and stages; assertions run against `PreMergeValidationService` in-process |

### The blocker

**`ListReferencesInFile` and `ListPackageReferences` have no MCP tool.** Their only non-unit-test callers are `Program.cs:113-114`. `FindIndexedReferences` is symbol-keyed — the inverse question to "what references occur *inside* this file". Deleting the CLI makes both queries unreachable from any live surface while the index continues to populate them.

### Gaps that exist today in both suites

Neither the CLI nor the MCP tests assert **staged-record supersede semantics**, and there is **no concurrency or locking coverage** despite `AcquireManifestLock` and `recordSync` being central to the design. Out of scope here; recorded so they are not mistaken for regressions later.

---

## Phases

Each phase ends at a green build and full suite, and is independently revertible. Do them in order; do not batch.

### Phase 0 — decisions and the MCP gap

**0.1 Decide the SEV-1 gap.** Either add two MCP tools or accept the loss, in writing:

- `find_references_in_file(path)` → `SolutionIndexQueryService.ListReferencesInFile`
- `list_package_references()` → `SolutionIndexQueryService.ListPackageReferences`

Recommendation: **add them.** They are thin wrappers over methods that already exist and already have unit tests, and "what references live in this file" is a question the agent has an obvious use for. Cost is roughly an hour including tool descriptions.

**0.2 Decide ADR-0003.** `0003-in-app-review-retire-winmerge.md` references "the MCP/CLI path (legacy)" at lines 25 and 47. ADRs are historical records. Recommendation: **do not edit the decision text** — add a dated note recording that the CLI was retired, as done for ADRs 0001–0003 previously.

**0.3 Preserve the orphan implementations.** Before deletion, copy into a scratch note or an ADR: the `adapterProtocol = "cli-mcp-like"` telemetry envelope, and the two redaction routines (`--old-text`/`--new-text` value redaction; recursive `snippet` → `[redacted]`). These are the **only redaction implementation in the repository**. Nothing consumes them today, but if the MCP surface ever needs to keep code out of logs, this is the prior art.

**Verify:** solution builds; new tools appear in `get_tool_manifest`; full suite green.

---

### Phase 1 — fold the language corpus into `dotnet test` *(independent; highest value)*

`AIMonitor.LanguageCorpusSmokeTests` is the one runner that pays for itself: hermetic, **zero machine-specific paths**, 42 fixture cases all present, and a genuine Roslyn oracle — it binds each marked span with a raw `CSharpCompilation` and asserts the index agrees on symbol identity, reference count, caller count, relationship kinds, **and exact line/column**. Nothing else in the repo covers `operator +`, conversion operators, indexers, explicit interface implementations, method-group assignment, or local functions.

1. Convert to an xunit class with `IAsyncLifetime`: build and index the shared corpus project **once**, in the fixture.
2. Emit one `[Theory]` case per corpus directory via `MemberData`, so a failure names the construct that broke.
3. **Drop `--assert` and make assertion the default.** Today the runner exits 0 on failure unless the flag is passed — which is why it has never caught anything.
4. Keep the 5 `informational` cases as `[Fact(Skip = …)]` with the harness limitation (single synthesised project) as the skip reason, so the gap stays visible.
5. **Do not** retarget onto MCP. It asserts index-row fidelity — stable keys, line/column, reference kinds — that the tool surface deliberately abstracts away.

**Verify:** 42 cases appear as named tests; deliberately break one corpus expectation and confirm the suite goes red.
**Rollback:** revert the commit; the console runner is untouched until Phase 5.

---

### Phase 2 — salvage from `ToolSmokeTests`, then stop

Three scenarios are worth keeping out of 2,296 lines.

**2.1 `--fixture-index-matrix` → `[Fact]`s in `AIMonitor.Data.Tests` or `AIMonitor.Indexing.Tests`.** The single highest-value salvage. Self-contained fixture, no MCP, no external solution, and a **real oracle**: it counts references independently via `SemanticModel.GetSymbolInfo` and asserts AIMonitor's count equals Roslyn's for 7 symbol shapes. Also exercises generated-file (`.g.cs`) policy. Half a day.

**2.2 The three-file rename-discovery flow → `McpSurfaceTestHarness`.** `find_indexed_symbols` → `find_indexed_references` → confirm both external consumers → rename across three files. This is the only test anywhere of *"can the index answer the question an agent must ask before a breaking change"* — the exact case that made a two-file change into a three-file one during real use. About a day.

**2.3 The overlay state machine → `McpPlannedSessionSurfaceTests`.** Two mutually-dependent new files in one planned session: the first must report `overlayValidation.status = planned-overlay-pending`, the second must have moved past pending. The only sequential assertion of the planned-overlay contract. A few hours.

**Verify:** each salvaged test fails when its invariant is broken.

---

### Phase 3 — re-home the CLI-unique tests

Five behaviours from the table above, plus the five `Edit_premerge_validation_*` tests.

**The one rule that makes this safe:** `WorkflowEditService` holds staged records in a per-instance cache with atomic write-through. Thirteen CLI tests cross a process boundary and therefore assert **disk rehydration**. An in-process re-home that reuses a single `WorkflowEditService` would satisfy every guard from the warm cache and **silently stop testing rehydration**.

> **Rule: construct a second `WorkflowEditService` instance at each seam** where the CLI previously started a new process. Do not reuse one instance across stage → review-stamp → accept.

1. Move `CreateFixture` verbatim — it has no CLI dependency.
2. Move `ValidateStaged` and `MarkReviewedInApp` verbatim (the latter already delegates to `InAppReviewSimulator`, shared with the MCP suite).
3. Port the five `Edit_premerge_validation_*` tests first — they are already engine-direct and only used the CLI to refresh and stage.
4. Port the four remaining unique behaviours, honouring the fresh-instance rule.
5. Delete `RunCliAsync`, `GetBuildConfiguration`, `Quote`, `CliResult`.

**Explicitly not ported:** 65 exit-code assertions, JSON envelope shapes, stderr substring matching, and `Program.Accept`/`Reject`'s "resolve the last staged record from `--file`" shortcut. All CLI-contract, all disappearing with the CLI by design.

**Verify:** the ported tests fail when their invariant is broken — check this by mutation, not by them passing. Full suite green.

---

### Phase 4 — delete `AIMonitor.Cli`

Mechanically tiny: **no product project references it, no script, no CI** (`.github/workflows/` is empty).

1. `rm -r src/AIMonitor.Cli/` (3 tracked files, 709 lines)
2. `ClaudeWorkbench.slnx` — remove the project line
3. `tests/integration/AIMonitor.Integration.Tests/AIMonitor.Integration.Tests.csproj` — remove the `ProjectReference`
4. `rm tests/integration/AIMonitor.Integration.Tests/CliIndexQueryTests.cs`

`AIMonitor.Logging` stays on the test project's compile closure via `AIMonitor.Indexing` (needed by `EngineReviewSessionAtomicityTests`), so no other csproj edit is required.

**Verify:** solution builds; full suite green; `grep -ri "AIMonitor.Cli" src tests scripts` returns nothing outside docs.

---

### Phase 5 — retire the remaining smoke runners

**5.1 Delete `AIMonitor.SmokeTests` (511 lines).** On a clean machine it finds zero samples, prints "No local watched-solution smoke samples were found", and **returns 0** — a green no-op, which is worse than a failure. Its two remaining fixtures point at private solutions on one developer's `C:` drive, and the API it exercises is already covered by `AIMonitor.MSBuild.Tests`.

**Salvage first:** the **Razor code-behind sweep** — *every `.razor.cs` must be indexed, and pure code-behind must contribute symbols* — is a real product invariant. Point it at `samples/watched-solutions/BlazorSample` (which already has `CustomerList.razor` + `CustomerList.razor.cs`) and fold it into `AIMonitor.MSBuild.Tests` as a handful of `[Fact]`s.

**5.2 Delete `AIMonitor.ToolSmokeTests` (2,296 lines)** once Phase 2 salvage has landed. Also delete `AddMemberPairAsync`/`RemoveMemberPairAsync` (~160 lines, never called — orphans of a removed mode) and the cleanup verb that is that mode's only surviving trace.

**5.3 Remove the paired skip.** `McpServerSmokeTests.cs:366` is `[Fact(Skip = "…cover it with ToolSmokeTests live workflows.")]` — it defers bridge coverage to a runner that cannot run. Both sides go together. The bridge project has never existed in this repo.

**5.4** Remove the deleted projects from `ClaudeWorkbench.slnx`.

**Verify:** solution builds; full suite green; README test-count table updated.

---

### Phase 6 — documentation sweep

Roughly 20 locations. Two are **dead links** the moment `docs/components/AIMonitor.Cli.md` is deleted and must be done in the same commit:

- `docs/README.md:79`, `docs/components/README.md:18` — table rows
- `docs/architecture/Architecture.md` — the `Cli[...]` node **and** the `Mcp --> Cli` edge, together, or Mermaid renders a dangling node
- `docs/components/AIMonitor.Logging.md:29` — diagram label
- Six component pages — drop `AIMonitor.Cli` from "Depended on by"
- `README.md` — repo-layout lines, "integration/ end-to-end over the CLI + engine", the test-count table, and the now-confusing note about CLI tests being converted
- `docs/guide/deploying.md:180` — *"Both fixtures were indexed through `AIMonitor.Cli index rebuild`"*. This is a **provenance claim about how the shipped fixtures were produced**. Do not swap in another tool name unless the fixtures are actually re-indexed that way; rewrite in past tense or re-index and restate.

**Verify:** every relative link resolves; every Mermaid fence balanced; `grep -ri "AIMonitor.Cli" docs README.md` returns only intentional historical mentions.

---

## Expected outcome

| | Before | After |
|---|---|---|
| Engine entry points | 3 (MCP, host, CLI) | 2 (MCP, host) |
| Legacy console lines retired | — | ~2,800 of ~3,350 smoke + 709 CLI + 1,392 CLI tests |
| Integration tests | 66 pass · 1 skip | ~45 + re-homed + salvaged |
| Language-corpus cases | 42, manual, exits 0 on failure | 42 in `dotnet test`, assert by default |
| Coverage pointed at production paths | partial | complete |

## Risks

1. **Silent coverage loss in Phase 3** if the fresh-instance rule is not followed. Mitigation: verify each ported test by mutation.
2. **SEV-1 capability loss** if Phase 0.1 is skipped. Mitigation: it gates Phase 4.
3. **Dead documentation links** if Phase 6 is partial. Mitigation: link check in the same commit.
4. **Salvage never happens** — the classic failure of "delete but keep the good bits". Mitigation: Phases 1, 2 and the 5.1 salvage land *before* any deletion.

## Sequencing note

Phases 1 and 2 are pure additions and can be done any time. Phase 0.1 gates Phase 4. Phase 3 gates Phase 4. Phase 5 is independent of the CLI work. Phase 6 must follow Phase 4 in the same session or the docs contradict the code — the failure mode this project has hit repeatedly.

---

## Appendix A — preserved implementations (Phase 0.3)

`AIMonitor.Cli/Program.cs` is the **only place in the repository** that redacts source code
before it reaches a log. Nothing consumes these routines today, and they die with the project.
They are recorded here because if the MCP surface is ever asked to keep code out of
`adapter.mcp.tool.called` telemetry, this is the prior art.

### Why it exists

The CLI logged every command as `adapter.query.started` / `adapter.query.completed` NDJSON,
including the command line and a response preview. Two things in there are real source code:
the values of `--old-text` / `--new-text`, and any `snippet` property in an index response. Both
were replaced with `[redacted]` before logging. The MCP path has **no equivalent** — worth
knowing rather than assuming parity.

### Recursive `snippet` redaction

```csharp
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
        return "[unavailable]";   // never let a log preview throw
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
```

### Sensitive-option-value redaction

```csharp
private static bool IsSensitiveTextOption(string option)
{
    return option.Equals("--old-text", StringComparison.OrdinalIgnoreCase)
        || option.Equals("--new-text", StringComparison.OrdinalIgnoreCase);
}
```

…applied in `CreateParamsPreview` / `CreateCommandLinePreview`, which walk the argument array and
substitute `[redacted]` for the value following a sensitive flag.

### The telemetry envelope

`adapterProtocol = "cli-mcp-like"`, source `"AIMonitor.Cli"`, emitted as a started/completed pair
with: `requestId`, `command`, `toolName`, `commandLine`, `paramsPreview`, `durationMs`, `isError`,
`contentType`, `contentShape`, `contentCount`, `contentTextPreview`. The name was always aspirational —
the CLI never spoke MCP; it shaped its logs to *look* like the MCP adapter's so both could be read in
one stream. Recorded so nobody re-derives the shape from scratch.
