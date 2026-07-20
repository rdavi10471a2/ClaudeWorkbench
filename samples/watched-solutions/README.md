# Watched Solution Samples

Samples are human-readable projects used to demonstrate watched-solution behavior.

Watched solution copies are local-only by default. Put large or real-world samples
under this folder when you want a local smoke target, but do not commit those
project copies.

The committed exceptions are these tiny watched solutions, kept because a repeatable
smoke or demo points at them:

| Sample | Used for |
|---|---|
| `CalculatorSample` | The **flow smoke** fixture (`sidecar/src/smoke/`) — accept / reject / multi-file, driven end to end against a real Claude turn. Also the workspace the publish script ships and the Launcher seeds on first run. |
| `WorkflowHarnessSample` | A minimal watched solution for staging/decision workflow smokes. |
| `CodexWindows`, `CodexBlazor` | Windows and Blazor shapes for CLI-driven workflow samples. |
| `BlazorSample`, `WinFormsSample` | Indexing/source-map shapes (Razor + `.razor.cs`, WinForms designer files). |

Use a sample instead of a developer's real watched application whenever a smoke is
shared, scripted, or Claude-driven.

## Pointing the app at one

The only committed sample config is
`src/ClaudeWorkbench.Host/config/appsettings.calculator-sample.json`. Its paths are
**relative** — `WatchedSolutionPath` resolves against the config file's own folder and
`RuntimeRoot` against the repo root — so it works in any checkout:

```bash
dotnet run --project src/ClaudeWorkbench.Host \
  -- --config src/ClaudeWorkbench.Host/config/appsettings.calculator-sample.json
```

For the other samples, pick the solution in the app's workspace picker, or add a
workspace in the Launcher and point it at the `.slnx`. Copy the calculator config if
you want a committed config of your own — keep the paths relative, never an absolute
path into your checkout.

## Notes

Do not use samples as the only regression proof. If a behavior must stay fixed, add a test fixture or smoke.

For Razor samples, keep smoke expectations representative and grep-verified. The current monitor indexes normal C#, clean `.razor.cs`, and source-mapped Razor references; it does not attempt to prove every Blazor markup binding in a production page.

## Framework / project-format coverage

Two fixtures exist specifically to prove the watched solution's target framework and project
format are independent of ClaudeWorkbench's own `net10.0`:

| Fixture | Shape | Result |
|---|---|---|
| `Net8Sample` | SDK-style, `net8.0` | indexes; `NamedType`/`Method`/`Field`/`Property` symbols extracted |
| `LegacyFrameworkSample` | **non-SDK-style** `net472` — `ToolsVersion`, explicit `<Compile Include>`, `packages.config`, `Microsoft.CSharp.targets` import | indexes; `NamedType`/`Method` symbols extracted |

The legacy one exists because the expected failure — `MSBuildLocator.RegisterDefaults()` picking
the SDK's MSBuild, which "cannot evaluate a Visual Studio-era project" — **did not happen**. It
evaluates fine and Roslyn builds a real semantic model from it.

Both are deliberately minimal: framework references only, no restore, no VS-only imports. They
prove the *format* is not the barrier. They do **not** prove an arbitrary legacy solution will
load — that is far likelier to fail on a dependency (unrestored `packages.config`, a custom
`.targets`) than on the `.csproj` shape.
