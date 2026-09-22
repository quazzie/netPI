# Launch, publish & reload (deep doc)

`AGENTS.md` lists the scripts; this is how the publish → stage → launch →
reload pipeline actually works and what each stage guarantees. Scripts:
`tools/publish-plugins.ps1`, `tools/keep-alive-host.ps1`,
`tools/launch-desktop.ps1` (P0.1).

## The one-direction data flow

```
repo sources
   │ dotnet publish (immutable artifact, sha256-pinned manifest)
   ▼
.artifacts/plugins/<id>/<buildId>/          (never modified after publish)
   │ pointer flip (atomic current.json)
   ▼
plugins/<id>/current.json  ───────────────►  host discovery
   │ resolve + snapshot (byte-verified copy)
   ▼
~/.netpi/plugin-cache/<host-instance>/<plugin>/<attempt>-<buildId>/
   │ (what the ALCs actually load)
   ▼
collectible ALCs (<id>-gen<n>)
```

The host loads from the **snapshot dir only** — never from `.artifacts/` or
`plugins/`. That is what makes a reload a pure byte-swap with no in-place
mutation of anything a running process touches.

## publish-plugins.ps1

- **Discovery**: markers `plugins/*/*.csproj` (no hardcoded list;
  `NetPI.TestPlugin` excluded unless `-IncludeTestPlugin`).
- **Publish**: `dotnet publish` → immutable `.artifacts/plugins/<id>/<buildId>/`
  + `artifact.json` manifest (every file pinned by relative posix path +
  sha256 + size). Post-process: Kestrel class-lib plugins get a hand-written
  `runtimeconfig.json` (framework `Microsoft.AspNetCore.App`);
  `NetPI.Storage.Sqlite` gets `e_sqlite3.dll` for the host's native pre-load.
- **Validate**: parse the manifest, re-hash every file in place. On failure the
  pointer is **not** flipped (a failed build never touches a live pointer).
- **Activate**: atomic `plugins/<id>/current.json` flip.
- **Prune**: keep the 3 newest artifact builds per plugin (locked ones
  skipped, never a failure).
- **buildId**: 12 lower-hex of SHA-256 over `path\nsha256\n` lines (the exact
  algorithm of `PluginPublication.ComputeBuildId`). Same bytes ⇒ same buildId
  ⇒ idempotent (`up-to-date`, no rewrite, no flip).
- **Flags**: `-Reload` asks a running host to swap the build and confirms the
  new buildId (`reloaded <id> <old> -> <new>`); `-NoBuild` re-consumes a prior
  artifact; `-Plugins` selects a subset.
- Outcomes: `built <id>` / `published <id> <buildId>` / `up-to-date <id>
  <buildId>` / `reloaded <id> <old> -> <new>`.

## keep-alive-host.ps1 (headless host)

Holds the host REPL's stdin open (background job) so the host stays up across
agent turns. It **stages** the complete host payload into a NEW immutable
`~/.netpi/app-cache/host/<staging-id>/launch-<ts>/` and runs **that copy** —
never from the repo `bin/`. A failed/partial copy aborts the launch. Prior
launch dirs are pruned to the 3 most recent; a locked dir is skipped, not a
failure. **Launcher identity (P0.3)**: the script probes `GET /identity` on
:5173 before deciding. A matching build id → reuse the running host; a
different build id or a non-netPI listener → explicit error; a free port →
stage + launch ours. An open port alone is never proof the listener is netPI.

## launch-desktop.ps1 (P0.1 — the "app")

The developer desktop launch path. Builds the desktop app (unless
`-NoBuild`), then stages the **complete** desktop payload (WinForms shell +
WebView2 + its bundled `host/`) into a NEW immutable
`~/.netpi/app-cache/desktop/<staging-id>/launch-<ts>/` and launches **that
copy** — the shell never runs from the repo `bin/`. Sets explicit launcher
identity (`NETPI_HOME`/`NETPI_PROJECT_ROOT`/`NETPI_PLUGINS`). The shell then
re-stages its bundled `host/` into `~/.netpi/app-cache/host/...` on its own and
reuses a running host on :5173 if present (it does not double-spawn).

## Reload lifecycle (after a publish)

`plugin.reload` (or `plugin.reloadAll`): re-resolve `current.json` → snapshot
the new bytes as the next per-instance attempt → drain leases (poll 10 ms,
bounded 30 s) → apply the plugin's `ReloadPolicy` (`PluginIdle` / `AgentIdle`)
→ `Stop`/`Unload` the old ALC generation → `Load`/`Start` the new one. A failed
load keeps the previous generation Active. `plugin.scan` loads ids the host has
never seen without a restart. Stale per-plugin snapshots are pruned to
`MaxCachedGenerations` (default 2) once their ALCs are finalized, via an
ownership-aware `.owner` token (one host instance never deletes another's
cache). Consumers re-resolve services after a reload (see
`docs/plugin-architecture.md` → "Reload safety for shared state").

## Never commit

`web/netpi-web/dist/`, `plugins/*/bin|obj`, staged plugin DLLs,
`.artifacts/`, and all `~/.netpi/` state — all git-ignored.
