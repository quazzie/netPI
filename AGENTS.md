# Working on NetPI

.NET 10 host, hot-reloadable plugins, Svelte 5 UI. Overview: [README](README.md). Current state: [HANDOFF](docs/HANDOFF.md).

## Layout

- `src/NetPI.Abstractions`: shared interfaces/data. `src/NetPI.Contracts`: plugin vocabularies.
- `src/NetPI.Host`: kernel; references only Abstractions. `Server`: headless; `Desktop`: WinForms/WebView2.
- `plugins/<Name>`: independent plugins; `shared/`: compiled-in source, not shared assemblies.
- `web/src`, `plugins/*/ui`: Svelte sources. Commit their generated bundles with source changes only.
  Svelte 5 trap: `$state(fn)` stores the function as the value (no lazy init) — call the initializer
  and pass the result; a bound `f.name` on a function reads the function's own name.
- `tests/`: console runners, MockLlm, E2E. No NuGet packages except WebView2 and Jint (the CodeMode plugin's script engine, pinned).

## Work and delivery

- Follow `C:\AI\Projects\AGENTS.md`. Use your own worktree/branch; preserve others' edits; stage explicit paths.
- One commit per piece. Merge into `master` from the main checkout, verify ancestry, remove your merged tree/branch.
- Plain builds use `artifacts/dev/app`; they must never touch the installed app.
- After merging, publish yourself with `.\build.ps1 -Publish` — never leave it to the user. The script decides: plugins
  hot-reload and the web UI swaps in place (no restart; a window refresh shows it); host files, or plugins built against
  changed contracts, are staged for the next start automatically. There is nothing else to choose. `-NextStart` (reload
  nothing, stage everything) only when the user asks for it; the "tools" notice chats get from a reload is no reason to
  avoid it. Report what the script printed: went live, or waits in `.pending`.
- App changes are done when `-Publish` has run. A restart does not install Git changes.
- Docs-only changes need no build/publish. Ask before stopping the owner's app.
- Commands and install details: `Get-Help .\build.ps1 -Full`, [HANDOFF](docs/HANDOFF.md). UI: `npm ci`, `npm run build`; Git handles LF.

## Verification

- Docs/formatting: check diffs, links or affected build output. No test suites.
- Small UI edits: bundle build and focused UI check. Behavior changes: owning regression tests and affected E2E.
- Full gates only for shared contracts, cross-cutting behavior or explicit requests—never merely for merging.
- Fix flaky tests; a passing retry is not a fix. Read saved evidence and rerun only affected tests. Do not merge unresolved failures.
- Commands, selection, mocks and failure evidence: [TESTING](docs/TESTING.md).
- Deterministic builds are load-bearing (hot-reload + the contracts check in build.ps1): unchanged sources must rebuild
  byte-identical, same commit in two checkouts included. If a dll's bytes start changing with no source change, diff the
  PDBs (SourceLink, line endings and checkout paths are the known leaks) before touching anything else.
  Web bundles too: `web/scripts/lf-sources.mjs` compiles UI sources as LF (a CRLF file once baked `\r` into the
  committed bundle), and `build.ps1` warns when unchanged sources rebuild the committed bundle differently: commit it.

## Architecture and conventions

- Keep policy in plugins; check core changes with `node scripts/core-size.mjs`.
- Plugins communicate through services/RPC/events, never project references. Register via `IPluginContext`; resolve services per use.
- Never cache plugin types in host-wide JSON options/object containers; it prevents unloading. See [PLUGINS](docs/PLUGINS.md).
- No compatibility shims. Contract changes update all consumers/tests/docs and rebuild all plugins. Local data migrations are one-off scripts.
- Only `Host/Storage/Sqlite` writes SQL; plugins use `ctx.Data`. New storage providers must pass Storage tests.
- Session root means workspace, not project: use `IWorkspaceResolver`, without fallback for bound sessions. Mutations use `WorkspacePaths.CheckMutation` and the workspace guard.
- Tool text goes in `Content`, UI data in `Details`. Announce tool-availability changes and their cause on the bus.
- Document [tools](docs/TOOLS.md), [RPC/events](docs/PROTOCOL.md), [settings](docs/SETTINGS.md) and [UI](docs/UI.md) changes. Archive completed plans.
- Inspect read-only with `diag { "action": "overview" }` or `node scripts/netpi.mjs`; see [DEBUGGING](docs/DEBUGGING.md).
