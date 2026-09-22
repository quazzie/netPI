# Web panels — plugin-contributed right-panel tabs

Plugin-contributed tabs in the Web UI's right panel. The shell (Svelte app) owns
layout/chrome; each panel's content is isolated behind an `<iframe>` so hot-reloading
a plugin can never destabilize the chat app (`src/NetPI.Abstractions/WebUi.cs`).

> PLAN-v1.md does not cover this feature — this doc is the reference. The
> shell contains **no hardcoded tabs**: every right-panel tab is a
> `WebPanelDefinition` from the `ui.panels` catalog. The "diagnostics" tab
> (with the former "plugins" view folded in) is registered by the
> NetPI.Diagnostics plugin (PLAN §47) on its own Kestrel port, and the "background" tab by the
> NetPI.BackgroundTasks plugin — see "Reference implementation" below.

## Data flow

```
plugin.LoadAsync
   │  context.WebPanels.Register(new WebPanelDefinition(id, title, icon, entryUrl, order))
   ▼
ScopedWebPanels (one per plugin generation, PluginManager.cs:218)
   │  Unload() on reload/stop keeps the global registry clean even if the
   │  plugin forgets to dispose its registration handle
   ▼
WebPanelRegistry (global; created in HostRuntime.cs, exposed via IPluginContext.WebPanels)
   │  All() → ordered by Order, then Title
   ▼
NetPI.Web plugin (Kestrel on 127.0.0.1:5173) — PanelJson() in WebApp.cs
   │  "ui.panels" WS message (envelope { type, payload:{ panels:[...] } })
   ▼
web/netpi-web: ws.ts "ui.panels" → store.webPanels → RightPanel.svelte tab list
   └─► selected tab renders <iframe src={panel.entryUrl}>
```

## Key files

| File | Role |
|---|---|
| `src/NetPI.Abstractions/WebUi.cs` | `WebPanelDefinition(Id, Title, Icon, EntryUrl, Order=0)`, `IWebPanelRegistry` |
| `src/NetPI.Abstractions/IPluginContext.cs` | `IWebPanelRegistry WebPanels` property (the only registration surface) |
| `src/NetPI.Host/Services/WebPanelRegistry.cs` | Global lock-guarded registry + `ScopedWebPanels` (generation-scoped view) |
| `src/NetPI.Host/Plugins/PluginManager.cs` | Creates `ScopedWebPanels` per instance (~:430); calls `WebPanels?.Unload()` on every unload path (~:1007, ~:1405) |
| `plugins/NetPI.Web/WebApp.cs` | `PanelJson()` (~:1861); `ui.panels` broadcast on bootstrap (~:829), on every plugin-state change via the runner (~:407), and for `ui.panels.list` (~:1447) |
| `plugins/NetPI.Web/WebPlugin.cs` | Registers **no** panel — the former standalone "plugins" panel was folded into the Diagnostics plugin's "Plugins" sub-tab (see Reference implementation) |
| `plugins/NetPI.Diagnostics/` | Registers the "diagnostics" panel with an **absolute** `EntryUrl` (`http://127.0.0.1:5274/panel/diagnostics`) — the page + API live on the Diagnostics plugin's own Kestrel port; the panel includes the former "Plugins" sub-tab; the tab appears/disappears with the plugin generation |
| `plugins/NetPI.BackgroundTasks/` | Registers the "background" panel (`http://127.0.0.1:5275/panel/background`); the page lists background jobs and stops them via same-origin `/api/bg/*` endpoints (BgWebApp.cs) |
| `plugins/NetPI.Activity/` | Registers the "activity" panel (`http://127.0.0.1:5276/panel/activity`); runs + managed processes via same-origin `/api/activity/*` endpoints (ActivityWebApp.cs, astra-1 H) |
| `web/netpi-web/src/types.ts` | `WebPanelInfo` wire type |
| `web/netpi-web/src/store.svelte.ts` | `webPanels` state |
| `web/netpi-web/src/ws.ts` | `case "ui.panels"` → store |
| `web/netpi-web/src/components/RightPanel.svelte` | Tab rail rendering, `selectedPlugin`, iframe, vanished-tab guard |
| `tests/NetPI.Host.Tests/WebPanelRegistryTests.cs` | Registry tests (scoped unload, id replacement) |

## Plugin-author contract (C#)

```csharp
public sealed class GitPlugin : INetPiPlugin
{
    public PluginInfo Info { get; } = new("netPI.Git", "Git", "0.1.0");
    private IDisposable? _panel;

    public ValueTask LoadAsync(IPluginContext ctx, CancellationToken ct)
    {
        _panel = ctx.WebPanels.Register(new WebPanelDefinition(
            Id: "git",
            Title: "Git",
            Icon: "G",
            EntryUrl: "http://127.0.0.1:8096/panels/git/", // you serve this URL
            Order: 10));
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask UnloadAsync(CancellationToken ct)
    {
        _panel?.Dispose();   // removing it explicitly is optional — see below
        _panel = null;
        return ValueTask.CompletedTask;
    }
}
```

Semantics (verified in code + tests):

- **Register in `LoadAsync`** (or any point before unload). The registration
  handle's `Dispose()` removes the entry (matched by id **and** entryUrl).
- **Ownership is by generation.** `ScopedWebPanels.Unload()` runs on every
  unload path (reload, failed-load fallback, host stop), so a registration
  disappears automatically even if the plugin never disposes its handle.
  A failed reload keeps the previous generation Active — its panel stays.
- **Duplicate ids replace**: a `Register` with an existing id (case-insensitive)
  removes the old entry first. So a successful reload of a plugin re-registers
  cleanly over its previous generation's entry.
- **Ordering**: `All()` sorts by `Order`, then `Title` (ordinal-ignore-case).
- Panel-specific config comes from `context.OwnConfig`
  (`plugins.netpi.<plugin-id>` in `~/.netpi/config.json`) like everything else.
- **Do not reference NetPI.Web's assembly** (core invariant — AGENTS.md).
  The registry in `NetPI.Abstractions` is the whole contract you need.

## Catalog distribution (`ui.panels`)

`WebApp.cs` sends `ui.panels { panels: [...] }` to **all connected WS clients**:

- WS connect — bootstrap sequence (after `plugins.state`, before session replay)
- on **every** plugin-update completion (astra-1 §F): `WebApp` subscribes to `PluginUpdateCompletedEvent` (`OnPluginUpdateCompleted`, ~:364) — for `reload` / `reloadAll` / `scan` it broadcasts `plugins.state` + `ui.panels` to all clients. The runner publishes the event for every completed update, whether triggered by a WS command, the CLI (`reload <id>` / `reloadall`), or a `plugin.scan` — so open UIs refresh either way.
- on `ui.panels.list` request (available, but nothing in the frontend calls it today)

## Frontend rendering (`web/netpi-web`)

- `RightPanel.svelte`: tabs are rendered **entirely** from `store.webPanels`
  (the `ui.panels` catalog) — the shell has no hardcoded tabs and no
  per-tab-id content branches; every tab renders through the same
  `<iframe src={panel.entryUrl}>` path (the catalog today: Diagnostics,
  Background, Activity — NetPI.Web registers none, see below). Tab labels
  `writing-mode: vertical-rl` (see `.vertical-tab*` in `app.css`; rail width is
  `--right-rail-width: 26px` in `:root` — the `26` in `App.svelte`'s
  `shellStyle` must stay in sync).
- Clicking a tab: `ui.setRightTab(id, true)`; clicking the active tab toggles
  the panel. Content area: shell title row + `<iframe>` when the selected id
  is in the catalog, else "No panels available."
- **Guard**: an `$effect` falls back to the first catalog panel if the active
  tab disappears (e.g. a reload dropped it).
- **Iframe semantics**: no `key` on the `<iframe>` — a catalog refresh that
  leaves `entryUrl` unchanged does **not** reload the visible panel; switching
  tabs away and back remounts (fresh load).
- `ui.rightTab` is persisted in `localStorage` (`netpi.ui.v2`); panel width is
  clamped to 260–760 px.

## Serving panel content

`entryUrl` is an arbitrary URL string; **the host does not proxy panel content**.
Recommended: the plugin hosts its own static files/listener (own Kestrel on a
fixed localhost port) and points `entryUrl` there — the iframe is then
cross-origin.

Gotchas:

- **Same-origin SPA fallback**: under the host origin (`127.0.0.1:5173`), any
  unmatched path serves the app's `index.html` via a fallback route
  (`WebApp.cs`, ~:256 — only when `staticRoot` is configured).
  A panel URL there only works if it matches an actual
  static file in a served root.
- **No bridge**: no `postMessage` protocol, no shared store access, no auth.
  What a panel page *can* do: serve its own static assets, open its own
  `ws://127.0.0.1:5173/ws` connection and speak the §41 protocol (expect the
  full bootstrap incl. latest-200-entries session replay; client→server
  commands per AGENTS.md), and use `GET /api/file?path=…&sessionId=…`
  (localhost-only).
- A same-origin panel page can read the shell's `localStorage` (`netpi.ui.v2`)
  — keep panel content cross-origin to preserve the isolation the design
  intends.

## Reference implementation: the panel catalog today

There are exactly **three** production panels — NetPI.Web registers **no** panel
of its own (its former standalone "plugins" panel was folded into the
Diagnostics plugin's "Plugins" sub-tab; `WebPlugin.cs` registers nothing):

- `plugins/NetPI.Diagnostics/DiagnosticsPlugin.cs` — `LoadAsync` registers
  `("diagnostics", "Diagnostics", "◌", "http://127.0.0.1:{port}/panel/diagnostics", 10)`
  with an **absolute** entry URL because the page is served by the Diagnostics
  plugin's own Kestrel instance (default port 5274, `plugins.netpi.diagnostics.port`),
  not by NetPI.Web. The tab appears/disappears with the plugin generation.
- `plugins/NetPI.Web/` — `PanelJson()` (~:1861) builds the `ui.panels` catalog
  from the global registry; `WebApp` broadcasts `ui.panels` on bootstrap
  (~:829) and on **every** plugin-state change via the runner (~:407), so the
  catalog refreshes whenever a plugin reloads or a scan loads new ids —
  including a reload of netpi.web itself (the broadcast fires in the still-
  alive generation before it is swapped).
- `plugins/NetPI.Diagnostics/panels/diagnostics.html` — live panel served by
  the Diagnostics plugin's own Kestrel (`/panel/diagnostics` on :5274).
  Self-contained, polls its own same-origin `/api/diag/*` endpoints (overview,
  model-wire decisions, agent events, log tail with level/plugin filters,
  sessions) every 4 s. Sub-tabs: **Overview / Wire / Plugins / Models /
  Events / Logs / Sess** (deep-linkable via `#tab=…`). The **Plugins**
  sub-tab — the former standalone NetPI.Web "plugins" panel — additionally
  opens a cross-origin `ws://<host>:5173/ws` connection to the host hub for
  live `plugins.state` / `plugin.state` / `plugin.reloaded` /
  `plugin.scanned` events and drives `plugin.reload` / `plugin.reloadAll` /
  `plugin.scan` from its "Reload all" / "Scan for new" buttons. It re-opens
  that WS 3 s after any drop (`hostWs.onclose`), self-healing across plugin
  reloads and host restarts; `#tab=plugins` deep-links straight to the former
  plugins view.
- `plugins/NetPI.BackgroundTasks/` — `LoadAsync` registers
  `("background", "Background", "▶", "http://127.0.0.1:{port}/panel/background", 5)`
  (absolute URL; default port 5275, `plugins.netpi.backgroundtasks.port`).
  `BgWebApp.cs` serves the embedded `panels/background.html` plus
  `GET /api/bg/jobs`, `GET /api/bg/{id}/output?chars=` (tail of the bounded
  output ring) and `POST /api/bg/{id}/kill`. The page polls `/api/bg/jobs`
  every 2 s, re-points the registration at the real bound URL when port 0 is
  used, and `location.reload()`s itself if the surface stays unreachable
  (plugin reload / host restart) so it self-heals across generations.
- `plugins/NetPI.Activity/` — `LoadAsync` registers
  `("activity", "Activity", "✦", "http://127.0.0.1:{port}/panel/activity", 7)`
  (absolute URL; default port 5276, `plugins.netpi.activity.port`).
  `ActivityWebApp.cs` serves the embedded `panels/activity.html` plus
  `GET /api/activity/agents` (run-query: all runs from every session),
  `POST /api/activity/agents/{runId}/cancel`, `GET /api/activity/processes`
  (background jobs + foreground shell processes),
  `GET /api/activity/processes/background/{id}/output?chars=` and
  `POST /api/activity/processes/background/{id}/stop`. All services are
  resolved LAZILY — a missing Agent/BackgroundTasks/Tools plugin degrades a
  section to "unavailable" instead of erroring, so Activity never blocks
  chat. It is presentation-only: unloading it never touches the runs it shows.

**Deploying a new NetPI.Web build:** `dotnet build` the plugin →
`pwsh tools/publish-plugins.ps1 -Configuration Debug` → `plugin.reload` in the
UI. No ordering constraint: every generation loads from an immutable
snapshot (`~/.netpi/plugin-cache/<plugin>/<gen>/`, see AGENTS.md "Gotchas"),
so the staged folder is free to be overwritten while a generation is live —
the reload snapshots the new bytes as generation N+1.

## Testing & verification

- `dotnet test NetPI.sln` — `WebPanelRegistryTests` covers scoped-unload and
  same-id replacement (the two core invariants).
- `PluginHotSwapTests` (same suite) prove the snapshot hot-swap: load from
  snapshot, staged folder rewritable while a generation is live (reload picks
  up new bytes), prune-to-two with ALC collectibility.
- Not covered: `ui.panels` broadcast behavior in `WebApp` (would be an
  integration test), and end-to-end iframe rendering.
- The reference implementation is the Diagnostics "Diagnostics" panel (above);
  `plugins/NetPI.TestPlugin` (the reload/lease fixture, published only with
  `-IncludeTestPlugin`) is the place to add a *second*-plugin panel test if
  cross-plugin catalog behavior ever changes.
- After any frontend change: `npx vite build` + reload the Web plugin or
  refresh the browser (hashed assets) — see AGENTS.md.

## Known gaps (TODO for future agents)

1. No iframe reload on plugin reload (stale content if `entryUrl` is unchanged;
   the panel pages mitigate this by self-healing — the diagnostics page
   re-opens its host WS 3 s after a drop, background/activity pages
   `location.reload()` when their surface is unreachable — but the shell
   iframe is not keyed on plugin generation).
2. No shell↔panel `postMessage` bridge protocol (panels open their own `/ws`).
3. No integration-level test for the `ui.panels` broadcast or the diagnostics
   panel routes — registry mechanics are unit-tested.
