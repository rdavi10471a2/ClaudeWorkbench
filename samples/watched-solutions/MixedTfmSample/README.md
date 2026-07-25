# MixedTfmSample

A deliberately mixed multi-project, multi-target solution used to test the candidate-edit
**overlay** against real project-type and cross-project variety:

| Project      | SDK / kind                    | TFM             | References |
|--------------|-------------------------------|-----------------|------------|
| `Shared`     | class library                 | `net8.0`        | —          |
| `ConsoleApp` | console (`Exe`)               | `net8.0`        | `Shared`   |
| `WinFormsApp`| WinForms (`WinExe`)           | `net9.0-windows`| `Shared`   |
| `BlazorApp`  | Razor components (`Sdk.Razor`)| `net10.0`       | `Shared`   |

## Why this shape

- **Project types:** Blazor / WinForms / console each resolve a different framework set
  (`AspNetCore.App`, `WindowsDesktop.App`, base) at a **different major version** (10 / 9 / 8),
  which is exactly what a hand-rolled reference list gets wrong and a project-aware overlay must
  get right.
- **Cross-project:** `Shared` is at the **lowest** TFM (`net8.0`) so all three apps can reference
  it (cross-TFM references only flow downlevel). `Shared.SharedGreeter.Greet(...)` is the single
  symbol every app calls — change its signature and update one consumer to reproduce an
  **interdependent cross-project edit** (the split-session overlay-validation case).
