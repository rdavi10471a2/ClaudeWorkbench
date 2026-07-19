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
