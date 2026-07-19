# Troubleshooting

## "Claude unavailable" / turns don't run

The sidecar isn't up or the host can't reach it.

- The host **launches the sidecar** itself — but the sidecar must be **built** first:
  `cd sidecar && npm install && npm run build` (produces `sidecar/dist/index.js`).
- Check the host log for a `[sidecar]` line, or hit `http://localhost:6110/health`.
- Make sure **Node** is on PATH (the host runs `node dist/index.js`).
- Auth: the sidecar inherits the local `claude` CLI login. Run `claude` once to confirm
  you're logged in (a subscription login is enough — no API key needed).

## Port already in use (6100 / 6110)

Something is already listening. Stop the old process (or change `Sidecar:BaseUrl` /
Kestrel endpoint in config). The sidecar **won't** double-launch if its port is already
taken — the host detects it and uses the existing one.

## The Git panel says "Git is not available"

`git` isn't on PATH. Install git and ensure `git --version` works in a fresh shell, then
**Refresh** the Git tab. (This is distinct from "not a repository," which offers an
**Initialize** button instead.)

## A push fails

- The watched repo needs a remote — the panel tells you if there's **no remote**. Add one
  (`git remote add origin <url>`) or let the first push set the upstream.
- Auth to the remote uses your machine's git credential helper — make sure you can push
  from a normal terminal.

## The Host build fails with CS0101 / CS0111 (duplicate types)

This happens if the watched solution's runtime mirror (`runtime/…/working/*.cs`) gets
compiled into the Host. It shouldn't — the csproj excludes `runtime/**`
(`DefaultItemExcludes`). If you see it, confirm that exclude is present and that a stale
build isn't holding a lock (stop the running host first).

## Nothing to review after an edit turn

The Merge Review opens only when the turn **finishes** with staged edits. If the agent
only read code, or a gate was denied, there's nothing staged. Check the transcript for
denied gates or errors.

## Self-check warnings

`get_self_check` reports guardrails for the current workspace. All green is normal.
(WinMerge is retired — the review/merge surface is in-app — so there is no external
diff-tool guardrail.)

## Where the logs are

Engine narration goes to a JSON-lines file under the workspace `RuntimeRoot` (`logs/`).
The live **Activity** tab shows the same events in-app.
