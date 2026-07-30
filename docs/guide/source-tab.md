# The Source Tab

A read-only window into the watched solution: browse it, view and render its files, build and
run it, manage its packages, and scaffold new projects — all without an external editor. Nothing
here writes to your source *code* except **Add project** (which scaffolds new files), **Packages**
(which edits project files' `<PackageReference>` items), and **Build/Run** (which produce `bin/`
output); the code the agent authors is never edited from this tab. Authoring still happens only
through the [governed loop](the-governed-loop.md) and its [Merge Review](merge-review.md) gate.

## Two trees: Solution and Files

The sidebar has two sub-tabs. They are two *views* of the same solution, not a toggle — each keeps
its own filter and expand/collapse state, and both open files into the same viewer.

### Solution

The code model, built from the in-process semantic index: **projects → files → symbols**. Types and
their members (methods, properties, fields, grouped by accessibility) are navigable — click a symbol
to jump straight to its line in the viewer. This is the "what the compiler sees" view, so it shows
only files that are part of a project.

### Files

A plain file browser — the "what's on disk" view — fed by:

```
git ls-files --cached --others --exclude-standard
```

run in the watched folder. That means it lists:

- **tracked** files (`--cached`), **plus**
- **new, uncommitted** files that aren't ignored (`--others --exclude-standard`) — so a file the
  agent just created shows up here *before* it's ever committed, and
- **minus** anything `.gitignore` excludes (`bin/obj`, generated output, `.git`, vendored trees) —
  no hand-maintained ignore list; git's own rules keep it clean.

Because it's git-fed, not index-fed, the Files tree is **index-independent**: it works before the
first index build, and it surfaces the non-code files the Solution tree never had — README, `docs/`,
`scripts/`, `.gitignore`, `.slnx`, and (the motivating case) committed decision documents.

The list is cached and refreshed on **Refresh** / **Rebuild Index** and when the workspace changes,
so clicking a file never re-shells git.

**Filesystem fallback (no git).** If git isn't available — not on `PATH`, or the watched folder
isn't a repository — the Files tree still works: it falls back to a plain recursive filesystem walk
(VS Explorer style) instead of going dark. The walk is *pruned* — it skips build output and
tool/VCS metadata (`bin`, `obj`, `.git`, `node_modules`, `.vs`, `.idea`, `.vscode`, `packages`,
`TestResults`, and any dotted directory), plus any per-solution extras listed under
`FilesTree.ExcludeDirectories` in `.claudeworkbench.json` — and is **capped** (10,000 files) so a
pathological tree can't stall the UI. It produces the same file entries as the git path, just without
git's ignore rules; the tree's empty-state hint suggests `git init` (no remote needed) to get the
richer git-fed listing back.

## The viewer

Both trees open into one persistent, in-app **Monaco** editor (vendored locally — not an iframe, not
a CDN), read-only, with the model swapped per file. Selecting a file reveals and highlights the
target line.

### Markdown rendering

`.md` files render as **formatted HTML** by default — headings, lists, code blocks, tables, links —
using the same sanitizing `MarkdownRenderer` the chat transcript uses. A **Rendered / Raw** toggle at
the top of the viewer switches to Monaco source when you want the raw text. (This is the standard
editor-vs-preview split — Monaco itself has no native markdown preview; Rendered is a separate HTML
pane, Raw is Monaco. Monaco stays mounted under the rendered pane, so toggling is instant.)

Non-markdown files always open straight in Monaco with syntax highlighting.

## Toolbar: Build, Run, and workspace actions

The top toolbar carries operator actions over the whole solution:

| Control | What it does |
|---|---|
| **Refresh** | Re-read current source into the trees/viewer without re-indexing. |
| **Rebuild Index** | Re-index the whole solution (the same rebuild the startup warm-up runs). |
| **Build** + Debug/Release | Run a real `dotnet build` into the watched solution's own `bin/<config>` — actual output, not the throwaway validation build. |
| **Run** + startup-project picker | Build, then launch the selected executable project. The dropdown lists the solution's executable projects (`OutputType` Exe/WinExe, from the index) — VS's "startup project" selector — so Run is never ambiguous when there's more than one. Disabled ("No executable project") for a library-only solution. |
| **Admin shell** | Open an **elevated** `cmd.exe` rooted at the solution folder — a place to run git/dotnet/system commands by hand with the rights some need. Windows shows a **UAC prompt**; dismiss it and you get a "cancelled" toast, nothing opens (see below). |
| **Packages** | Open the **NuGet manager** — browse/install/update/uninstall packages by project or across the whole solution (see below). |
| **Add project** | Scaffold a new C# project into the solution (see [Creating projects](../README.md) / below). |

**Build and Run are operator actions**, run host-side out-of-process — they are deliberately *not*
part of the agent's MCP tool surface. The agent never builds or launches anything; you do.

Why build here at all? The governed loop's validation build is a throwaway mirror used only to gate an
accept — it doesn't leave binaries in your tree. When you actually want to run the thing, **Build**
(and **Run**) produce real artifacts in place. Build-after-accept and run-after-accept are also
available from the Merge Review dialog.

## Add project

Scaffold greenfield without leaving the app. **Add project** enumerates the installed SDK's templates
(`dotnet new list`) and target frameworks (`dotnet --list-sdks`) at open time — so the dropdowns show
exactly what this machine can create — then runs `dotnet new` → `dotnet sln add` → `dotnet restore`
and reindexes. **C# only**, and new projects must live inside the solution folder. Like Build/Run,
this is an **operator** action; the agent never runs the SDK. Requires the `dotnet` CLI on `PATH`
(the same .NET SDK the app already needs). See the [README](../README.md#creating-projects) for detail.

## Packages (NuGet)

**Packages** opens a NuGet manager for the watched solution — the same browse/install/update/uninstall
surface Visual Studio's package manager gives you, in one modal. A **Scope** switch at the top picks
**Whole solution** or a single project; four tabs work within that scope:

- **Installed** — every referenced package, read straight from each project's `<PackageReference>`
  items (a file parse, not an SDK call, so it's instant and always fresh). Shows the version(s) in use
  (flagged **mixed** when they differ across projects), the latest available, and **Update** /
  **Uninstall** actions. A package with no version is under Central Package Management (its version
  lives in `Directory.Packages.props`).
- **Browse** — search nuget.org (`dotnet package search`), pick a hit, optionally pin a version (blank
  = latest), choose which projects to install into, and **Install**.
- **Updates** — packages with a newer version, from `dotnet list package --outdated`. Update one, or
  **Update all** in scope.
- **Consolidate** (solution scope only) — packages pinned at different versions across projects; pick
  one version to apply everywhere it's used.

Everything runs **host-side and out-of-process** via the real .NET SDK (`dotnet add|remove package`),
invoked from the solution root so `NuGet.config`, `Directory.Build.props`, and private feeds are
honored exactly as a normal `dotnet` run would. Installs restore inline. Like Build/Run/Add project,
this is an **operator-only** action — the agent is not involved and gets no new tool; when the agent
needs a package it stages a `<PackageReference>` edit through the governed loop instead. A package
change alters no user symbols, so the manager deliberately **does not reindex** — the index's package
graph refreshes on its own on the next real build/accept. Requires the `dotnet` CLI on `PATH`.

## Admin shell

**Admin shell** opens an **elevated** `cmd.exe` rooted at the solution folder — an operator
convenience for running git/dotnet/system commands by hand against the watched tree with the rights
some of them need. It uses Windows' `runas` verb, so you get a **UAC prompt**; the window is opened
with `cmd /K cd /d` into the solution root (an elevated process otherwise starts in `system32`).
It's **human-only** — not part of the agent surface — and best-effort: dismiss the UAC prompt and you
get a "cancelled" toast, never a crash.

## What this tab is not

- **Not an editor.** Source code is read-only. To change code, prompt the agent and accept the result
  in [Merge Review](merge-review.md). The exceptions are *creating* new projects/files via Add project
  and editing `<PackageReference>` items via Packages.
- **Not an agent surface.** Build, Run, Add project, Packages, and Admin shell are operator-only,
  host-side actions — they add nothing to the agent's tool count.
