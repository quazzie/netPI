# AGENTS.md — working on NetPI

NetPI is a .NET 10 agent harness: a small host kernel (`src/NetPI.Host`) plus hot-reloadable plugins (`plugins/*`)
and a Svelte 5 UI (`web/`). Read `README.md` for the overview and `docs/` for details.

## Layout
- `src/NetPI.Abstractions` — contracts shared by host and plugins. Change them **additively** only; every plugin
  depends on them. Rebuild all plugins after changing it.
- `src/NetPI.Host` — kernel: plugin manager, event bus, registries, SQLite (P/Invoke, no NuGet), settings, session
  store, model catalog, Kestrel/WebSocket server. `src/NetPI.Server` = headless exe, `src/NetPI.Desktop` =
  WinForms + WebView2 shell (Windows only).
- `plugins/<Name>` — one plugin per folder; minimal csproj, conventions in `plugins/Directory.Build.props`. Plugins
  never reference each other: they talk through services (`ctx.Services`), RPC (`ctx.Rpc`) and events (`ctx.Events`).
- `plugins/<Name>/ui` — optional Svelte tab (built to `wwwroot/ui.js` by `npm run build:plugins`, committed).
- `web/` — the app UI (built to `web/dist`, committed; copied to `artifacts/app/wwwroot` by the server build).
- `tests/` — console test runners (no test framework; NuGet packages other than WebView2 are not used),
  `tests/MockLlm` (scripted model server), `tests/NetPI.E2E` (end-to-end suite).

## Build & test
- Windows: `.\build.ps1` (add `-Test` for the unit suites, `-Run` to start); from cmd `build` (`build.cmd`, same
  options). Linux/macOS: `./build.sh [--test]`.
- While NetPI runs: `dotnet build plugins/<Name>` (one plugin, hot-reloads) or `.\build.ps1`, which builds into
  `artifacts\build\stage` and then puts it in place: plugins hot-reload; host files it replaces go into
  `artifacts\app\.old` (a running exe or DLL can be renamed, not overwritten), so a restart of NetPI runs the new host;
  after a contract change the plugins wait in `artifacts\app\.pending` for that restart. `-NextStart`: the running
  NetPI gets nothing, everything waits for the restart. It prints what needs it.
- Unit suites: `dotnet tests/NetPI.<X>.Tests/bin/<Config>/NetPI.<X>.Tests.dll [filter]` for X in Providers, Tools,
  Agent, Aux, Host. End-to-end: `dotnet tests/NetPI.E2E/bin/<Config>/NetPI.E2E.dll` (see `docs/TESTING.md`).
- UI: `npm ci` once, then `npm run build` (app + plugin tabs) or `npm run dev` / `npm run mock`.

## Inspecting the running app
- `node scripts/netpi.mjs` (the overview), then `diag.problems`, `diag.calls`, `diag.run`, `diag.journal`, `diag.logs`: see
  `docs/DEBUGGING.md`. It finds the app through `<home>/server.json` and only reads unless given `--write`.

## Conventions
- Keep the core small; new behaviour goes into a plugin. Register everything through `IPluginContext` so hot
  reload can remove it; resolve other plugins' services per use.
- Don't cache plugin-defined types in host-wide JSON options or `object` containers (blocks unloading) — see
  `docs/PLUGINS.md`.
- Tool results: model-facing text in `Content`, UI data in `Details` (document new shapes in `docs/TOOLS.md`).
- A tool that appears or disappears mid-chat is announced by the context plugin, and the notice names the cause
  (`plugins/NetPI.Context/ToolChanges.cs`, evidence in `plugins.reloaded`): anything that can make a tool go away
  (enable/disable, settings) should say so on the bus, or the cause falls back to `unknown`.
- WebView2 quirk (desktop zoom): a navigation resets the zoom to 100% and a programmatic `ZoomFactor` set does not raise `ZoomFactorChanged` — the remembered factor is therefore applied after the first navigation, and `desktop.zoom` persists itself (see `MainForm.cs`).
- New RPC methods / events: document them in `docs/PROTOCOL.md`; new settings in `docs/SETTINGS.md`.
- Add or update a test in the owning suite for every behaviour change.
