# Getting Started

How to build, run, and drive ClaudeWorkbench for the first time. For *how it works
inside*, see the [architecture doc](../architecture/Architecture.md).

## What you need

| Requirement | Why |
|---|---|
| **.NET 10 SDK** | The Blazor host + extracted engine + in-proc MCP server |
| **Node.js** (LTS) | The sidecar runs the Claude Agent SDK (Node-only) |
| **`claude` CLI** | The Agent SDK spawns it; **bundled with the SDK** — no separate install |
| **A Claude login** | A **subscription** login (`~/.claude`) runs it for yourself — no API key needed |
| **git** | The Git panel + gated git tools shell out to `git` (must be on PATH) |
| Ports **6100** + **6110** | Host + sidecar talk over localhost |

## One-time setup

Build the sidecar once (this is what the host launches):

```powershell
cd sidecar
npm install        # restores @anthropic-ai/claude-agent-sdk + express
npm run build      # builds dist/  (host runs node dist/index.js)
```

## Run

```powershell
dotnet run --project src/ClaudeWorkbench.Host
```

The host **launches and supervises the sidecar itself** (single-start), then serves the
console at **http://localhost:6100**. You do not start the sidecar separately.

> First run: the mutable `config/appsettings.json` is seeded from a template pointing at a
> placeholder, so the app opens the **workspace picker**.

## Your first turn

1. **Pick a watched solution** — the workspace picker (or **Select Solution**). Point it
   at a `.sln`. See [workspaces](workspaces.md).
2. Open the **Workbench** tab and type a prompt, e.g. *"add a `Ping()` method to
   `Foo`."*
3. Claude reasons and, to change anything, calls governed tools that **pause at your
   gate** — Approve them.
4. When the turn finishes with staged edits, the **Merge Review** opens. Review the
   side-by-side diff and **Accept** (or Reject). Accept is the only thing that writes real
   source. See [merge review](merge-review.md).
5. Optionally use the **Git** tab to commit + push the accepted change. See
   [git panel](git-panel.md).

## The mental model

**The agent reasons and stages; you accept; only the accept writes real source.** The
agent is read-only to your solution and can only touch it through governed tools you
approve. Full picture: [the governed loop](the-governed-loop.md).
