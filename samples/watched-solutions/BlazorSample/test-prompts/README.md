# Test prompts — BlazorSample

Copy-paste prompts for exercising the ClaudeWorkbench governed edit loop against this
Blazor/Razor sample, mirroring `CalculatorSample/test-prompts`. They ship with the sample, so
`publish-live` carries them into the install (both `samples\` and the `samples-golden\`
backup), and the Launcher's **Reset samples** button restores them between runs.

These specifically exercise what the **new** validation workflow buys over the old in-memory
overlay: the plan-complete gate now runs the REAL `dotnet build`, so `.razor` compiles through
the actual Razor source generator and cross-file / component errors are caught accurately —
before the operator ever reviews.

## How to use

1. In the Launcher, **Reset samples**, then reindex and start a **New Thread** in the app.
2. Open a prompt file, copy the text under **## Prompt**, paste it into the composer, Submit.
3. Review each staged file in the Merge Review dialog and Accept (the session writes as a unit
   on the final Accept).

## What each one exercises

| File | Shape | Exercises |
|---|---|---|
| `01-add-code-behind-method.md` | 1 file, surgical | typed symbol edit on a `.razor.cs` code-behind, single-file session |
| `02-new-component.md` | 1 new `.razor` | `new_file` → `submit_file` for a component; plan-complete build runs the real Razor generator |
| `03-cross-file-model-and-component.md` | 3 files | model `.cs` + `.razor` + `.razor.cs` in one session; real build resolves the new member through generated code |
| `04-build-fail-and-fix.md` | 1 file | plan-complete REAL build catches a broken `.razor` reference with the actual error, then the fix→retry loop |
| `05-new-repository-di.md` | 1 new file + 1 edit | new interface implementation + rewire the consumer; real build confirms the wiring |

## Adding your own

Drop another `NN-title.md` here following the same shape (a `## Prompt` section with the
copy-paste text, phrased as an operator request — the agent chooses the tools). It ships on
the next publish.
