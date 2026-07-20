# Deploying — the Launcher and a live install

Two ways to run ClaudeWorkbench:

- **From the checkout** — `dotnet run --project src/ClaudeWorkbench.Host`, one instance, port `6100`.
- **From a published install** — a self-contained folder with the Blazor host, the sidecar and
  the **Launcher**, which runs several watched solutions side by side. That is what this page covers.

---

## The Launcher

`ClaudeWorkbench.Launcher` is a small WinForms control panel: one row per workspace, with
**Start** / **Stop** / **Add workspace** / **Remove** / **Settings** / **Help**.

Starting a workspace allocates a free host+sidecar port pair, writes that instance its own
config, launches the host (which spawns its sidecar) and a browser window — all inside **one
Windows Job Object**. So the whole set dies together:

- **Stop**, or closing the Launcher, terminates that instance's host + sidecar + browser.
- Closing the **browser window** stops that instance's backend on its own (the host's
  `CWB_EXIT_WITH_BROWSER`), within a few seconds.
- A Launcher *crash* can't orphan a backend — the Job Object dies with it.

Each instance is fully isolated: its own ports, its own runtime, its own index.

### Where an instance puts its state

```
<workbench root>\runtime\<workspace>\
    config\appsettings.json   this instance's watched-solution config (generated)
    watched-solutions\        the index and working/staging state
    browser-profile\          the app window's Chromium profile
    host.log                  host stdout/stderr, for diagnosing a failed start
```

The folder is named after the **workspace**, not a GUID. It is claimed on first Start and then
**sticky** — recorded in the Launcher's state — so renaming a workspace afterwards does not
strand its index. Two workspaces with the same name get `-2`, `-3` suffixes.

---

## Publishing a live install

```powershell
.\scripts\publish-live.ps1
```

Defaults to `C:\ClaudeWorkBenchLive`, Release. Options:

| Switch | Effect |
|---|---|
| `-Destination <path>` | Where to publish (default `C:\ClaudeWorkBenchLive`) |
| `-Configuration <cfg>` | Build configuration (default `Release`) |
| `-Clean` | Delete `host\`, `sidecar\`, `launcher\` first. **`runtime\` is preserved.** |
| `-NoShortcut` | Don't create the Desktop shortcut (one is still written into the install folder) |

It produces:

```
C:\ClaudeWorkBenchLive\
    host\       ClaudeWorkbench.Host.exe (the Blazor app) + config\
    sidecar\    dist\index.js + production node_modules
    launcher\   ClaudeWorkbench.Launcher.exe
    samples\    CalculatorSample — a small watched solution to try
    runtime\    created on first Start: one folder per workspace
    ClaudeWorkbench Launcher.lnk
```

plus a **Desktop shortcut**. Double-click either one; there is nothing else to configure.

On a **first run with no state**, the Launcher seeds the bundled sample as a workspace, so there
is a row to Start immediately instead of having to go find a solution. Remove it and it stays
removed (`SampleSeeded` is persisted). The sample lives in `samples\`, deliberately **not** in
`runtime\` — that folder is disposable per-workspace state and is meant to be safe to delete.

### Updating an install

Re-run the script. `runtime\` is never touched, so **workspaces and indexes survive** a
republish. **Close the Launcher first** — a running install holds its exes open, and the script
stops with a clear message rather than failing halfway through.

---

## How paths resolve (the assumptions)

The whole design goal: **the install works wherever it is, and whatever folder the Launcher exe
is in.** That rests on a few rules.

**1. The Launcher finds the "workbench root" — the folder that owns `runtime\`.** In order:

1. From the **host exe** it is configured to use — walking up for a checkout marker
   (`ClaudeWorkbench.slnx` or `src\ClaudeWorkbench.Host\`), else that exe's own folder for a
   published host. The host exe wins because it is what actually runs.
2. From the **Launcher's own location**, for the in-checkout dev case.
3. From `WorkbenchRootHint` in its saved state — the last place the workbench was seen. This is
   what lets a Launcher exe *copied out* of the workbench still resolve.

A published install is recognised by its `host\` folder, so `C:\ClaudeWorkBenchLive` is the root
even though it is not a checkout.

**2. The state file lives in the install, and its paths are relative to the root.** The Launcher
writes `launcher.json` at the **workbench root** (`C:\ClaudeWorkBenchLive\launcher.json`), so
copying the folder takes the workspace list with it. Paths inside the workbench are stored
relative — absolute ones would rot the moment the folder moved — while a watched solution
elsewhere on disk stays absolute:

```json
{
  "HostExePath": "host\\ClaudeWorkbench.Host.exe",
  "SidecarDirectory": "sidecar",
  "InstancesRoot": "",
  "WorkbenchRootHint": "C:\\ClaudeWorkBenchLive",
  "Workspaces": [
    { "Name": "SchemaCommander",
      "SolutionPath": "C:\\SchemaCommander\\SchemaCommander.sln",
      "InstanceFolder": "SchemaCommander" }
  ]
}
```

`"InstancesRoot": ""` means *follow the workbench root* (`<root>\runtime`). Setting an explicit
folder in Settings overrides it; clearing it back to the default restores tracking.

**3. A stored path that no longer exists is re-guessed, not kept.** State written by an older
build, or pointing at a checkout that has since moved, heals itself on load. The host-exe guess
prefers one shipped next to the Launcher, then `<root>\host\`, then the newest build in a
checkout — **Release ahead of Debug**, so it doesn't go stale against one configuration.

**4. The host anchors to its own binary, not the current directory.** The Launcher starts the
host from its install folder, so cwd is not a reliable anchor; the host falls back to its binary's
location when cwd has no `config\`, and finds its sidecar by walking up from the binary.

**5. Relative paths in a monitor config are anchored predictably** — `WatchedSolutionPath`
against the config file's own folder, `RuntimeRoot` against the repo root. That is why the
committed sample config uses relative paths and works in any checkout.

---

## Requirements

**On the build machine:** .NET 10 SDK · Node.js + npm on PATH · network access for
`npm ci --omit=dev` (falls back to copying the checkout's `node_modules` if offline).

**On the target machine:**

> ⚠️ **`runtime\` is not "the runtime."** It is the per-workspace *instance state* (indexes,
> staging, logs) — safe to delete, it just re-indexes. It contains no .NET and no Node. Copying
> the install folder to a fresh machine does **not** bring a runtime with it; see below.

| Needs | Why |
|---|---|
| **.NET 10 SDK** | Two separate reasons. The publish is framework-dependent, so it needs the *runtime* to start — but indexing also calls `MSBuildLocator.RegisterDefaults()`, which needs **MSBuild and the Roslyn toolset that ship with the SDK**. Publishing `--self-contained` removes the runtime requirement but **not** this one: without the SDK the app starts and then fails to index a solution. This is the requirement for running ClaudeWorkbench itself — **not** for the solution you watch; see below. |
| **Node.js** on PATH | The sidecar runs the Claude Agent SDK, which is Node-only. |
| **A Claude login** | A subscription login cached in `~\.claude` runs it with no API key; `ANTHROPIC_API_KEY` is only needed to ship to other people. See [Settings & Usage](settings-and-usage.md#auth). |
| **Free ports from 6100 / 6110** | Each instance takes a host+sidecar pair; the Launcher picks free ones per instance. |
| **Chrome or Edge** (recommended) | Opened as a clean `--app` window that closes as a unit. "Default browser" works but can't be force-closed from the Launcher. |

The `claude` CLI does **not** need a separate install — the Agent SDK package ships it, which is
most of the sidecar's `node_modules` size.

### What the WATCHED solution has to be

ClaudeWorkbench targets `net10.0`; **the solution it watches does not have to.** The index is
built through `MSBuildWorkspace`, so the real requirement is simply that the installed SDK can
evaluate the project. A .NET 10 SDK builds `net9.0`, `net8.0`, `netstandard2.0` and
multi-targeted projects perfectly well.

| Watched project | Verified by |
|---|---|
| `net10.0` — Razor, Web, WinForms, console | `samples/watched-solutions/` fixtures |
| `net9.0` (ASP.NET Core) · `net9.0-windows` (desktop) | daily use |
| `net8.0`, SDK-style | `samples/watched-solutions/Net8Sample` — indexed, symbols extracted |
| **`net472`, legacy NON-SDK-style `.csproj`** (`ToolsVersion`, explicit `<Compile Include>`, `packages.config`) | `samples/watched-solutions/LegacyFrameworkSample` — indexed, symbols extracted |

The legacy case is the surprising one. The expectation was that
`MSBuildLocator.RegisterDefaults()` resolves the **SDK's** MSBuild, which would refuse a project
expecting Visual Studio's — but the SDK's MSBuild evaluates an old-format project fine, and
Roslyn produced a real semantic model from it (`NamedType`/`Method` symbols, not just parsed
text). Both fixtures were indexed through the engine's index-rebuild path (`SolutionIndexRebuildService`,
the same code the `refresh_solution_index` tool calls).

> **What that does and does not prove.** Both fixtures are deliberately simple: framework
> references only, no NuGet restore, no `.targets` imports from a VS-only SDK, no WCF/WebForms/
> designer generation. A real legacy solution is far more likely to fail on a *dependency* —
> an unrestored `packages.config`, a custom `.targets`, a VS-only import — than on its project
> format. Treat "old formats load" as established and "your particular legacy solution loads"
> as untested until you point the workbench at it.

### Copying an install to another machine

The folder is **xcopy-deployable for everything the workbench itself owns** — the Launcher
discovers `host\` and `sidecar\` from its own location with no configuration, so a copied install
starts clean with no state at all. Delete `runtime\` to drop the previous machine's workspaces
and indexes; they rebuild on first Start.

What does **not** travel in the folder:

- **The .NET 10 SDK** and **Node.js** — install both on the target. Publishing self-contained
  removes the *runtime* requirement but not the SDK one (indexing needs MSBuild).
- **The Claude login** (`~\.claude`) — sign in on the target.

The **workspace list travels** — `launcher.json` sits at the install root. Watched solutions
outside the workbench are recorded as absolute paths, so if they don't exist at the same location
on the target, re-point them with **Add workspace**.

> Only if the install folder is **read-only** (Program Files, a network share) does the Launcher
> fall back to `%LOCALAPPDATA%\ClaudeWorkbench\Launcher\`. State written there by an older build
> is adopted automatically and migrated into the install on the next save.

## Notes and gotchas

- The build machine's `config\appsettings.json` is **deliberately not shipped** — it would point
  every fresh install at the build machine's watched solution. Each instance gets its own,
  written by the Launcher.
- `scripts\publish-live.ps1` is **ASCII-only on purpose**: Windows PowerShell 5.1 reads the file
  as ANSI, and non-ASCII punctuation becomes a parse error.
- The Launcher has a headless lifecycle check used during development:
  `ClaudeWorkbench.Launcher.exe --selftest <solution> <logPath>` starts an instance, confirms the
  host **and** its sidecar are up, stops it, and confirms both are gone. It writes a report and
  returns 0 on success. It binds the normal ports, so don't run it against a live install you are
  using.
- A first Start on a large solution can sit for a while building its index — the browser window
  opens once the host is responsive.
