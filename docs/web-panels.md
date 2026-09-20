# Web panels — plugin-contributed right-panel tabs

Plugin-contributed tabs in the Web UI's right panel. The shell (Svelte app) owns
layout/chrome; each panel's content is isolated behind an `<iframe>` so hot-reloading
a plugin can never destabilize the chat app (`src/NetPI.Abstractions/WebUi.cs`).

> PLAN-v1.md does not cover this feature — this doc is the reference. No plugin
> registers a panel yet (the `ui.panels` catalog is `[]` in practice); the
> architecture is fully scaffolded, wired, and registry-tested.
>
> **Not to be confused with the two always-present tabs** ("Plugins",
> "Diagnostics"): those are *built-in tabs* hardcoded in
> `RightPanel.svelte` and rendered by the Svelte shell directly (no iframe,
> no registry) from store data. They are not `WebPanelDefinition`s and do not
> appear in `store.webPanels`. "No panel exists yet" means: the
> plugin-contributed (iframe) catalog is empty.

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

- `RightPanel.svelte`: `allTabs = [plugins, diagnostics] + store.webPanels`.
  The first two are the hardcoded `builtins` (shell features); the rest are
  plugin-contributed panels. Tab labels render the title in
  `writing-mode: vertical-rl` (see `.vertical-tab*` in `app.css`; rail width is
  `--right-rail-width: 26px` in `:root` — the `26` in `App.svelte`'s
  `shellStyle` must stay in sync).
- Clicking a tab: `ui.setRightTab(id, true)`; clicking the active tab toggles
  the panel. Content area: `{#if ui.rightTab === "plugins"} … {:else if
  "diagnostics"} … {:else if selectedPlugin} <iframe src=…> {:else} "This panel
  is no longer available."`
- **Guard**: an `$effect` resets `rightTab` to `"plugins"` if the active tab
  disappears from the catalog (e.g. a reload dropped the panel).
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

## Testing & verification

- `dotnet test NetPI.sln` — `WebPanelRegistryTests` covers scoped-unload and
  same-id replacement (the two core invariants).
- Not covered: `ui.panels` broadcast behavior in `WebApp` (would be an
  integration test), and end-to-end iframe rendering.
- Natural first reference implementation: `plugins/NetPI.TestPlugin` (the
  existing reload/lease fixture) — register a panel in `LoadAsync` serving a
  trivial page, then smoke-test via the running host (`/ws` → `ui.panels`).
- After any frontend change: `npx vite build` + reload the Web plugin or
  refresh the browser (hashed assets) — see AGENTS.md.

## Known gaps (TODO for future agents)

1. No reference *plugin panel* exists yet — `ui.panels` is `[]` (the visible
   "Plugins"/"Diagnostics" tabs are built-ins, see above).
2. No iframe reload on plugin reload (stale content if `entryUrl` is unchanged;
   could key the iframe on the plugin's generation).
3. `ui.panels` is not pushed on non-Web-UI plugin lifecycle events.
4. No shell↔panel `postMessage` bridge protocol.
