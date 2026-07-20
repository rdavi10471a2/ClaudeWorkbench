# Settings & Usage

## Agent settings (the Settings dialog)

Open **Settings** (top-right) — *Agent · Tool Settings*. Controls passed to the sidecar's
query options; **Save** applies them, **Cancel** discards:

| Setting | Effect |
|---|---|
| **Native read tools (Read / Grep / Glob)** | When off, even the agent's reads are disallowed, forcing *all* access — reads included — through the MCP surface. On by default (native reads are ergonomic and safe). |
| **Isolate MCP surface** | Expose only `claude-workbench` and ignore the machine's account/user connectors (e.g. claude.ai connectors). |
| **Model** | Which Claude model drives the turn |
| **Reasoning level** | The effort/thinking level — default, `low`, `medium`, `high`, `xhigh`, `max` |
| **Optional tools** | Off by default. Each checkbox widens what the agent can do **outside** the governed gate; the ⚠ ones (`Bash`, `PowerShell`, `Write`, `Edit`) let it write files directly, bypassing the staged-review gate. `WebFetch`, `WebSearch`, `Agent` and `Workflow` are the non-risky ones. |

> **Model** and **Reasoning level** apply to **new threads** — start a **New Thread** for a
> change to take effect.

**Auto-approve** is *not* in this dialog — it's a toggle on the **composer** in the
Workbench tab. Per-**thread**: `claude-workbench` candidate mutations proceed without a
per-call gate prompt. The **merge-review Accept still gates every write to watched
source.** New Thread turns it back off.

## Usage meters

In the **Workbench** tab, the **Usage** dropdown reads straight off the SDK `Query`
handle:

- **Context** — how full the context window is + headroom until auto-compact.
- **Weekly / 5-hour** — subscription utilization, with reset times.
- **Plan / monthly overage** — when available.

Usage appears once a turn has run in the current thread. Hit **Refresh** to update it.

## Threads & continuity

- **New Thread** starts a fresh conversation (and resets Auto-approve, and picks up any
  changed Model / Reasoning level). The prior thread's session ends.
- Within a thread, the session **resumes** — the agent remembers the conversation, and a
  turn survives a host rebuild mid-turn.

## Auth

The sidecar sets **no** `ANTHROPIC_API_KEY` and injects no auth — it inherits whatever the
local `claude` CLI is logged into. A **subscription** login (cached in `~/.claude`) runs
it for yourself with no API key. An API key is only needed to ship to *other* people.

### Signing in from the Launcher

The Launcher toolbar has **Claude sign-in** and **GitHub sign-in** buttons. Each drops a small
menu — *Sign in…* or *Check status* — and opens a **terminal** on the CLI's own flow:

| Button | Runs | Used by |
|---|---|---|
| Claude sign-in | `claude auth login` / `claude auth status` | the sidecar (drives Claude) |
| GitHub sign-in | `gh auth login` / `gh auth status` | the [Git panel](git-panel.md) |

**Why a terminal and not an in-app dialog.** Both `claude auth login` and `gh auth login` are
interactive OAuth flows: they print a URL and a one-time code, open a browser, and then block on
the console waiting for the round-trip. That needs a real terminal — there is nothing the launcher
can usefully render in-process, so it hands you a console and gets out of the way. The window is
kept open after the command finishes (`cmd /k`) so you can read the result. Complete the flow in
the browser, confirm success in the terminal, then close it.

**It's machine-wide, not per-workspace.** Both CLIs cache their login under your user profile
(`~/.claude` for Claude), so you sign in **once per machine**, not once per watched solution. The
buttons live on the launcher rather than inside any instance for exactly that reason.

**Forcing a fresh login.** The menu's third item is *Sign out* (`claude auth logout` /
`gh auth logout`). Use it when you actually want to re-authenticate: `login` on an
already-signed-in CLI can short-circuit, so signing out first is the only reliable way to force the
full flow. Note that signing out of the Claude **Desktop** app, or closing an editor, does **not**
sign the CLI out — the CLI credential is separate and lives in `~/.claude`.

If a CLI isn't on `PATH`, the button says so — with the install command — instead of flashing a
terminal that reports "command not found". Install hints:
`npm install -g @anthropic-ai/claude-code` (Claude) and <https://cli.github.com/> (GitHub).
