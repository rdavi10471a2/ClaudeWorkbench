# Settings & Usage

## Agent settings (the Settings dialog)

Open **Settings** (top-right). Per-thread controls passed to the sidecar's query options:

| Setting | Effect |
|---|---|
| **Model** | Which Claude model drives the turn |
| **Reasoning level** | The effort/thinking level for the turn |
| **Auto-approve** (also on the composer) | Per-**thread**: `claude-workbench` candidate mutations proceed without a per-call gate prompt. The **merge-review Accept still gates every write to watched source.** New Thread turns it back off. |
| **Native read tools** | When off, even the agent's reads (`Read`/`Grep`/`Glob`) are disallowed, forcing *all* access — reads included — through the MCP surface. On by default (native reads are ergonomic and safe). |

## Usage meters

In the **Workbench** tab, the **Usage** dropdown reads straight off the SDK `Query`
handle:

- **Context** — how full the context window is + headroom until auto-compact.
- **Weekly / 5-hour** — subscription utilization, with reset times.
- **Plan / monthly overage** — when available.

Usage appears once a turn has run in the current thread. Hit **Refresh** to update it.

## Threads & continuity

- **New Thread** starts a fresh conversation (and resets Auto-approve). The prior thread's
  session ends.
- Within a thread, the session **resumes** — the agent remembers the conversation, and a
  turn survives a host rebuild mid-turn.

## Auth

The sidecar sets **no** `ANTHROPIC_API_KEY` and injects no auth — it inherits whatever the
local `claude` CLI is logged into. A **subscription** login (cached in `~/.claude`) runs
it for yourself with no API key. An API key is only needed to ship to *other* people.
