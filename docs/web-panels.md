# Web panels — plugin-contributed right-panel tabs

Plugin-contributed tabs in the Web UI's right panel. The shell (Svelte app) owns
layout/chrome; each panel's content is isolated behind an `<iframe>` so hot-reloading
a plugin can never destabilize the chat app (`src/NetPI.Abstractions/WebUi.cs`).

> PLAN-v1.md does not cover this feature — this doc is the reference. The
> shell contains **no hardcoded tabs**: every right-panel tab is a
> `WebPanelDefinition` from the `ui.panels` catalog. NetPI.Web self-registers
> two panels ("plugins", "diagnostics") — see "Reference implementation"
> below.

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
| `src/NetPI.Host/Plugins/PluginManager.cs` | Creates `ScopedWebPanels` per instance (:218); calls `Unload()` on every unload path (:437, :549, :699) |
| `plugins/NetPI.Web/WebApp.cs` | `PanelJson()` (~:959); `ui.panels` broadcasts (:423 bootstrap, :691 after `plugin.reload`, :715 after `plugin.reloadAll`, :732 for `ui.panels.list`) |
| `plugins/NetPI.Web/WebPlugin.cs` | Self-registers the "plugins"/"diagnostics" panels (see Reference implementation) |
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
- after `plugin.reload` / `plugin.reloadAll` commands
- on `ui.panels.list` request (available, but nothing in the frontend calls it today)

There is **no push on other lifecycle events** — `WebApp` subscribes only to the
`AgentEvent` bus. Consequence: a plugin reload triggered outside the Web UI
(e.g. CLI) does not refresh open UIs; the catalog updates on next connect,
next `plugin.reload*` command, or a manual `ui.panels.list`.

## Frontend rendering (`web/netpi-web`)

- `RightPanel.svelte`: tabs are rendered **entirely** from `store.webPanels`
  (the `ui.panels` catalog) — the shell has no hardcoded tabs and no
  per-tab-id content branches; every tab renders through the same
  `<iframe src={panel.entryUrl}>` path (NetPI.Web self-registers its
  "plugins"/"diagnostics" panels, see below). Tab labels render the title in
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
  unmatched path serves the app's `index.html` via `MapFallback`
  (`WebApp.cs:134-139`). A panel URL there only works if it matches an actual
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

## Reference implementation: NetPI.Web self-panels

The "Plugins" and "Diagnostics" tabs are registered by the Web plugin itself —
the Svelte shell has zero hardcoded tabs:

- `plugins/NetPI.Web/WebPlugin.cs` — `LoadAsync` registers
  `("plugins", "Plugins", "◇", "/panel/plugins", 0)` and
  `("diagnostics", "Diagnostics", "◌", "/panel/diagnostics", 10)` **before**
  the Kestrel app starts (so the first bootstrap already includes them);
  `StopAsync` disposes the handles (the scoped registry would also clean up
  on unload).
- `plugins/NetPI.Web/WebApp.cs` — `MapGet("/panel/plugins")` /
  `MapGet("/panel/diagnostics")` serve the pages; `PanelHtml(name)` loads them
  from **embedded resources** in the plugin assembly
  (csproj `<EmbeddedResource>`), so no extra staging files are needed.
- `plugins/NetPI.Web/panels/plugins.html` / `diagnostics.html` — self-contained
  vanilla-JS pages. Each opens its **own `/ws` connection** to the host,
  renders from broadcast events (`plugins.state`, `plugin.state`,
  `agent.state`, `session.updated`, `usage.updated`, …) and sends commands
  (`plugin.reload`, `plugin.reloadAll`). They render **content only** — the
  shell's iframe wrapper supplies the title row. On `/ws` close they
  `location.reload()` after 2 s, self-healing across plugin reloads and host
  restarts.

**Deploying a new NetPI.Web build (Windows gotcha):** the running host holds
`plugins/NetPI.Web/netPI.Web.dll` open, so `tools/publish-plugins.ps1` cannot
overwrite it while the current generation is loaded. Sequence: reload
NetPI.Web in the UI (unloads the generation, releases the DLL) → run
`tools/publish-plugins.ps1 -Configuration Debug` → reload NetPI.Web again to
load the new build.

## Testing & verification

- `dotnet test NetPI.sln` — `WebPanelRegistryTests` covers scoped-unload and
  same-id replacement (the two core invariants).
- Not covered: `ui.panels` broadcast behavior in `WebApp` (would be an
  integration test), and end-to-end iframe rendering.
- The reference implementation is NetPI.Web's self-panels (above).
  `plugins/NetPI.TestPlugin` (the reload/lease fixture) is the place to add a
  *second*-plugin panel test if cross-plugin catalog behavior ever changes.
- After any frontend change: `npx vite build` + reload the Web plugin or
  refresh the browser (hashed assets) — see AGENTS.md.

## Known gaps (TODO for future agents)

1. No iframe reload on plugin reload (stale content if `entryUrl` is unchanged;
   the self-panels mitigate this by reloading themselves when their `/ws`
   drops, but the shell iframe is not keyed on plugin generation).
2. `ui.panels` is not pushed on non-Web-UI plugin lifecycle events.
3. No shell↔panel `postMessage` bridge protocol (panels open their own `/ws`).
4. No integration-level test for the NetPI.Web self-panel routes
   (`/panel/*`, `ui.panels` broadcasts) — registry mechanics are unit-tested.
