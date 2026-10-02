# AGENTS.md — working on NetPI

NetPI is a .NET 10 agent harness: a small host kernel (`src/NetPI.Host`) plus hot-reloadable plugins (`plugins/*`)
and a Svelte 5 UI (`web/`). Read `README.md` for the overview and `docs/` for details.

## Layout
- `src/NetPI.Abstractions` — contracts shared by host and plugins: interfaces and plain data, no policy. Change them
  whenever the design calls for it: NetPI runs on one machine and nothing outside this repository depends on them, so
  there are **no compatibility shims, deprecated paths, mirrors or forwarders**. A contract change updates every plugin,
  test and doc in the same change; rebuild all plugins. Existing local data that a change strands is migrated for the
  owner's setup by a one-off script (see `docs/plans/2026-10-02-replaceable-parts.md`), never by product code.
- `src/NetPI.Contracts` — the vocabularies built on those: agent slots/scheduler, workspaces, decisions, resource
  leases, deferred tools, the plugins' event names. The **Host project must not reference it**, so the compiler enforces
  the small core; a plugin that speaks none of them needs only Abstractions.
- `src/NetPI.Host` — kernel: plugin manager, event bus, registries, settings, session service, model catalog, Kestrel/WebSocket
  server, and **storage as a port**: `src/NetPI.Abstractions/StoragePort.cs` (`IStorageProvider` chosen by the
  `storage.provider` setting, default `sqlite`; `--ephemeral` = the `memory` provider) with the two built-in providers in
  `src/NetPI.Host/Storage/{Sqlite,Memory}` — the only code that writes SQL (`Sqlite3` is a P/Invoke over the OS library,
  no NuGet). A plugin keeps its data in `ctx.Data` (`IPluginData`: named collections of JSON documents with declared
  index fields), never in SQL, and a new provider must pass `tests/NetPI.Storage.Tests`. `src/NetPI.Server` = headless
  exe, `src/NetPI.Desktop` = WinForms + WebView2 shell (Windows only).
- `plugins/<Name>` — one plugin per folder; minimal csproj, conventions in `plugins/Directory.Build.props`. Plugins
  never reference each other: they talk through services (`ctx.Services`), RPC (`ctx.Rpc`) and events (`ctx.Events`).
- `plugins/<Name>/ui` — optional Svelte tab (built to `wwwroot/ui.js` by `npm run build:plugins`, committed).
- `web/` — the app UI (built to `web/dist`, committed; copied into the build output's `wwwroot` by the server build).
- `tests/` — console test runners (no test framework; NuGet packages other than WebView2 are not used),
  `tests/MockLlm` (scripted model server), `tests/NetPI.E2E` (end-to-end suite).

## Build & test
- **Work in a worktree, not here** (see `C:\AI\Projects\AGENTS.md`): one worktree and branch per agent. It is how you
  avoid another agent's *edits* reaching you and yours reaching them. Several fixes can share that one tree — take them
  one at a time, one commit each, merging each into `master` before the next.
- **A build never touches the running app.** `AppOutDir` (`Directory.Build.props`) is `artifacts/dev/app`, so a plain
  `dotnet build` of a plugin — or of a test project that references one — lands there, and a running NetPI (which loads
  its plugins from `artifacts/app`) sees nothing: no chat loses a tool because someone ran the tests. Installing into
  `artifacts\app` is a separate, deliberate step (`-Publish` below). A worktree has its own `artifacts/` besides.
- Windows: `.\build.ps1` (add `-Test` for the unit suites); from cmd `build` (`build.cmd`, same options).
  Linux/macOS: `./build.sh [--test]`.
- Installing into the app folder (the app a NetPI runs from) is deliberate, never a side effect of testing. The
  folder is the running app's own (`<home>\server.json` says where it is, which is not this repository's `artifacts\app`
  when you build in a worktree), or `-AppDir <dir>` to name one. One install at a time: `.install.lock` in the app
  folder, so two publishers cannot interleave.
  - `.\build.ps1 -Publish` — build, then install. A reload is a **swap** (the new version starts while the old one
    still serves), so no chat loses a tool; the script still says which plugins and how many chats are mid-turn, and
    `-WaitUntilIdle` waits for them instead.
  - `.\build.ps1 -Publish -NextStart` — the running NetPI gets nothing: everything waits in the app folder's
    `.pending` for its next start, which installs it. Host files it replaces go into `.old` (a running exe or DLL
    can be renamed, not overwritten), so the next start runs the new host; after a contract change the plugins wait
    too, because the running NetPI would load them onto its old contracts. It prints what needs it.
  - `.\build.ps1 -Pending` — what a restart would bring. `.\build.ps1 -Discard` — drop the staged build.
  - `.\build.ps1 -Run` — publish and start the desktop app. `./build.sh` takes `--publish`, `--next-start`,
    `--pending`, `--discard`, `--app-dir`.
  - One plugin only, on purpose: `dotnet build plugins/<Name> -p:AppOutDir=<app>/ -p:BuildProjectReferences=false`.
    `AppOutDir` is the **app folder**, not the plugin's: a plugin's `OutDir` is `$(AppOutDir)plugins/<Name>/`, the
    folder the host loads it from.
- **A change is not done until the running app has it.** Merging into `master` and building land in `artifacts/dev/app` only;
  the running NetPI loads from the app folder, and a restart consumes `.pending` — not `master`. Before telling the user a
  change is done (or "you'll see it after a restart"), install it: `.uild.ps1 -Publish` (plugins hot-swap immediately),
  `-Publish -NextStart` (nothing moves under a live chat; the next start applies it), or the one-plugin dll copy above.
  If you cannot install it (no access to the app folder, or the user should choose when the host restarts), say so in the
  report and name the exact command. "It'll come in on your next restart" with nothing staged in `.pending` is false:
  that restart just restarts.
- **A publish from a worktree stages plugins instead of swapping them.** The built `NetPI.Abstractions.dll` never hash-matches
  the one the running app holds open, so `-Publish` reports "the contracts changed" and puts every plugin in `.pending` for the
  next start. To hot-swap one plugin anyway, copy its `.dll` and `.pdb` from the worktree's `artifacts\dev\app\plugins\<Name>\`
  into `<app>\plugins\<Name>\` — the running app reloads it (same contracts, so it is safe when only that plugin's source changed).
- **Want nothing to move under a running chat?** Set `plugins.quiet` (Settings or settings.json). Reloads are then
  recorded and not applied — the running versions keep serving, nothing swaps, no state is lost — and switching it off
  applies everything that piled up. This is the one thing a worktree cannot do: the running app is a single process
  that every session shares.
- Unit suites: `dotnet tests/NetPI.<X>.Tests/bin/<Config>/NetPI.<X>.Tests.dll [filter]` for X in Providers, Tools,
  Agent, Aux, Host, Storage (the storage port's conformance suite; a provider joins in its `Providers.All`).
  End-to-end (real server, mock model): `.\scripts\e2e.ps1` (below; `docs/TESTING.md`).
- **The test loop: one big run, then only the failures.** `.\scripts\test.ps1` builds the selected suites once
  (one generated solution), runs them (2 processes at once by default; `-Parallel 3` for three, `-Serial` for one),
  keeps the log and timings in `artifacts/testlogs`, and prints the failing names as a paste-ready `-Only` command.
  Re-run those while fixing, and the full run again before merging. `-Suite Aux`, `-Only "settings:"` (substring,
  OR-ed), `-SkipBuild`. Never re-run the whole set to check one fix. The summary shows each suite's process time
  next to its own reported test time: a gap there means the runner waited on output that never arrived.
- **End-to-end: pick what your change can reach; never the whole suite while you work.** `.\scripts\e2e.ps1 -Changed` runs
  the tests your changed files can affect (`tests/NetPI.E2E/areas.json` maps them); `-Only <id|substring>`, `-Tag <area>`,
  `-Smoke`, `-Failed` (what failed and has not passed since, across runs) and `-List` pick by hand. One test is ~2 s, one
  edited plugin plus its tests ~9 s, the smoke set ~10 s, and it builds only the projects whose sources changed. The whole
  suite (sharded, ~35 s) is the gate before a merge and after any change to `NetPI.Abstractions`: run it once, not as a
  loop. A run lists *every* failure at once, each with its evidence in `artifacts/e2elogs/<run>/failures/<id>.txt` (the
  server log, the mock model's requests and the client events since that test began): read that instead of running again
  to see what happened, fix, then `-Failed`. A changed E2E runner is checked with `.\scripts\e2e.ps1 -SelfTest`.
- **A test that fails sometimes is a bug, not weather.** Fix the test or the code it exercises, in the same piece of work that
  met it: a race in a test is usually the test observing a state the mock holds for milliseconds (the UI mock streams
  thinking in ~84 ms at `MOCK_SPEED=1` and ~28 ms at 3, so a `waitForSelector` on a live row can miss the whole window —
  give the mock a knob that holds the state, as `mock.procTailDelay` and `mock.filesDelay` do). Never write "re-run and see",
  never leave a red check explained as pre-existing in a report, and never merge on a run that only went green on the second
  try. If a test cannot be fixed where you are, say so and fix it before you finish the piece. Measure instead of retrying:
  `.\scripts\e2e.ps1 -Only <id> -Repeat 20 -Fresh` runs it on 20 fresh servers (the rate, and the evidence of every failure),
  and `-Fresh` alone runs every test on a server of its own, which finds a hidden order dependency (a test that only passes
  after another has run, or that asserts a server-wide total). Do not tell a subagent to retry either: hand it the failure
  file and this rule. The mocks have knobs that hold a state for a test to observe: `hold=<ms>` in a MockLlm scenario tag,
  `mock.thinkDelay` / `mock.procTailDelay` / `mock.filesDelay` in the UI mock.
- UI: `npm ci` once, then `npm run build` (app + plugin tabs) or `npm run dev` / `npm run mock`. The bundles are
  reproducible — Svelte hashes scoped CSS from a path under the repository root, not from where the build ran, and CI
  fails when a fresh build differs from the committed ones — so commit `web/dist` or a plugin's `wwwroot/ui.js` only
  with the *source* change under `web/src` or `ui/`.

## Inspecting the running app
- The **`diag` tool** (read-only, one action per method: overview, problems, calls, tools, journal, run, toolsets,
  messages, logs, settings, failures) — start with `diag { "action": "overview" }`. It is how an agent reads the
  harness; `node scripts/netpi.mjs` is the same surface from outside. See `docs/DEBUGGING.md`.

## Conventions
- Keep the core small; new behaviour goes into a plugin. `node scripts/core-size.mjs` checks it (the Host references
  only Abstractions, and neither `src/NetPI.Host` nor Abstractions names a plugin-owned type). Register everything
  through `IPluginContext` so hot reload can remove it; resolve other plugins' services per use.
- **A session's working directory is a workspace, not its project.** `plugins/NetPI.Workspaces` owns them and registers
  the one resolver (`IWorkspaceResolver`); ask it instead of `Sessions.GetCwd` when you need a session's root, its branch
  or its owner, and never fall back to the project path for a session that is bound to one. Adding a tool that writes
  files or runs a shell? The rule is `WorkspacePaths.CheckMutation` (native tools) and the `workspace` guard hook.
- Several agents work in this repo at once; the git rules are in `C:\AI\Projects\AGENTS.md`.
- Don't cache plugin-defined types in host-wide JSON options or `object` containers (blocks unloading) — see
  `docs/PLUGINS.md`.
- Tool results: model-facing text in `Content`, UI data in `Details` (document new shapes in `docs/TOOLS.md`).
- A tool that appears or disappears mid-chat is announced by the context plugin, and the notice names the cause
  (`plugins/NetPI.Context/ToolChanges.cs`, evidence in `plugins.reloaded` and `session.changed`): anything that can make a tool go away
  (enable/disable, settings) should say so on the bus, or the cause falls back to `unknown`.
- WebView2 quirk (desktop zoom): a navigation resets the zoom to 100% and a programmatic `ZoomFactor` set does not raise `ZoomFactorChanged` — the remembered factor is therefore applied after the first navigation, and `desktop.zoom` persists itself (see `MainForm.cs`).
- New RPC methods / events: document them in `docs/PROTOCOL.md`; new settings in `docs/SETTINGS.md`.
- Add or update a test in the owning suite for every behaviour change.
