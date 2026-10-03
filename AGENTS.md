# AGENTS.md — working on NetPI

NetPI is a .NET 10 agent harness with hot-reloadable plugins and a Svelte 5 UI. Start with [README.md](README.md); current state and decisions are in [docs/HANDOFF.md](docs/HANDOFF.md).

## Layout and architecture

- `src/NetPI.Abstractions`: interfaces and plain data, no policy. `src/NetPI.Contracts`: plugin vocabularies (agents, workspaces, decisions, leases).
- `src/NetPI.Host`: small kernel (plugins, events, registries, settings, sessions, models, web server, storage). It references only Abstractions, never Contracts or plugin-owned types; check architectural changes with `node scripts/core-size.mjs`.
- `src/NetPI.Server`: headless executable. `src/NetPI.Desktop`: Windows WinForms/WebView2 shell.
- `plugins/<Name>`: one plugin per folder; conventions in `plugins/Directory.Build.props`. Plugins never reference each other; use services, RPC and events. `shared/<Kit>` is compiled-in source, not a shared assembly.
- `web/`: app UI → committed `web/dist`. `plugins/<Name>/ui`: optional Svelte tab → committed `wwwroot/ui.js`. `tests/`: console runners, MockLlm and E2E; no test framework. No NuGet dependencies except WebView2.
- Storage is a port (`Abstractions/StoragePort.cs`); only `Host/Storage/Sqlite` writes SQL. Plugins use `ctx.Data` JSON collections. A new provider must pass `NetPI.Storage.Tests`.
- No compatibility shims, deprecated paths, mirrors or forwarders. Contract changes update every consumer, test and doc together; rebuild all plugins. Stranded local data gets a one-off owner migration, never product migration code.

## Working and shipping

- Follow `C:\AI\Projects\AGENTS.md`: work in your own `.worktrees/<agent>` and branch, never the main checkout. Reuse that tree, one commit per piece; merge each into `master` before the next. Stage explicit paths only.
- Check other worktrees first; preserve others' edits. Merge from the main checkout, verify ancestry, then remove your merged worktree and branch. Ask before deleting work you do not own.
- **A build never touches the running app.** `.\build.ps1` (`build` from cmd), `./build.sh`, and `dotnet build` write to `artifacts/dev/app`.
- **Merge before publishing.** A worktree publish installs that branch into the same live app everyone shares; worktrees isolate files, not the running process.
- **App changes are not done until installed.** Default: `.\build.ps1 -Publish`. Plugin reloads swap versions without losing tools. Use `-Publish -NextStart` when the owner is mid-turn or asks to defer; contracts may require staging until restart. Docs-only changes need no build or publish.
- Publishing targets the running app's directory from `<home>/server.json`, or explicit `-AppDir`; a worktree's dev output is not the live app. A restart consumes `.pending`, not Git: never promise a restart will apply something you have not staged. If install is blocked, report the exact command still needed.
- `-Pending` shows staged changes; `plugins.quiet` holds reloads until switched off. Do not stop the owner's app without asking. Recipes, install locking and other switches: `Get-Help .\build.ps1 -Full`, [docs/HANDOFF.md](docs/HANDOFF.md), [docs/DEBUGGING.md](docs/DEBUGGING.md).
- UI: `npm ci` once, then `npm run build` (app + plugin tabs); `npm run dev` / `npm run mock` for development. Commit bundles with their source changes only. `.gitattributes` keeps UI sources at LF; no manual conversion.

## Verification: match the change

- Docs/comments: review the diff and links. Formatting/line endings: relevant checkout/build-output checks. **No test suites for these changes.**
- Small plugin UI edit: build its bundle and check the affected UI. Plugin behavior: add/update owning tests and run the affected cases. A merge alone never requires a full gate.
- Full unit/E2E gates are for shared contracts, cross-cutting behavior or an explicit request. Run once when ready; broaden checks only for changed scope or new evidence.
- Unit selection: `.\scripts\test.ps1 -Suite <X> -Only "<name>"`. E2E: `.\scripts\e2e.ps1 -Only <id>`, `-Changed`, or `-Failed`. Read saved failure evidence in `artifacts/e2elogs/<run>/failures/`; after a fix rerun affected/failing tests, not everything. Runner changes: `-SelfTest`.
- A flaky test is a bug: diagnose and fix it in the same work; never accept a green retry or dismiss a failure as pre-existing. Measure with `-Only <id> -Repeat 20 -Fresh`, and use mock hold controls for transient states. If blocked, report it; do not merge with an unresolved failure.
- Suite commands, mock controls, logs and debugging: [docs/TESTING.md](docs/TESTING.md).

## Plugin and tool rules

- New behavior belongs in a plugin. Register through `IPluginContext` for hot-reload cleanup; resolve other plugins' services per use. Never cache plugin-defined types in host-wide JSON options or object containers: that prevents unloading. See [docs/PLUGINS.md](docs/PLUGINS.md).
- A session's working directory is its **workspace**, not its project. Ask `IWorkspaceResolver` for root, branch or owner; never fall back to the project for a bound session. File-writing/shell tools must use `WorkspacePaths.CheckMutation` and the `workspace` guard hook.
- Tool results: model text in `Content`, UI data in `Details`. Document new shapes in [docs/TOOLS.md](docs/TOOLS.md), RPC/events in [docs/PROTOCOL.md](docs/PROTOCOL.md), settings in [docs/SETTINGS.md](docs/SETTINGS.md).
- Changes that can remove tools (plugin enable/disable, settings) must announce their cause on the bus; Context turns it into a chat notice. See `plugins/NetPI.Context/ToolChanges.cs`.
- Inspect the live app read-only: start with `diag { "action": "overview" }`; outside NetPI use `node scripts/netpi.mjs`. See [docs/DEBUGGING.md](docs/DEBUGGING.md).
- UI and desktop details: [docs/UI.md](docs/UI.md). Keep current docs accurate; move completed plans to `docs/archive/` and update their references.
