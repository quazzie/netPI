# Web panels — plugin-contributed right-panel tabs

Plugin-contributed tabs in the Web UI's right panel. The shell (Svelte app) owns
layout/chrome; each panel's content is isolated behind an `<iframe>` so hot-reloading
a plugin can never destabilize the chat app (`src/NetPI.Abstractions/WebUi.cs`).

> PLAN-v1.md does not cover this feature — this doc is the reference. The
> shell contains **no hardcoded tabs**: every right-panel tab is a
> `WebPanelDefinition` from the `ui.panels` catalog. The "diagnostics" tab
> (with the former "plugins" view folded in) is registered by the
> NetPI.Diagnostics plugin (PLAN §47) on its own Kestrel port, and the "background" tab by the NetPI.Activity plugin (astra-2 §12: the combined **Work** panel) — see "Reference implementation" below.

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
| `plugins/NetPI.BackgroundTasks/` | Registers **no** panel anymore (astra-2 §12.1): it keeps process ownership, its 4 background tools, and its own Kestrel surface `GET /api/bg/jobs` + `GET /api/bg/{id}/output` + `POST /api/bg/{id}/kill` on :5275 (BgWebApp.cs), but its `panel/background.html` is no longer a registered tab |
| `plugins/NetPI.Activity/` | Registers the combined **Work** panel — `("background", "Work", "▶", "http://127.0.0.1:{port}/panel/activity", 5)` (astra-2 §12.1); one view with Agents / Background processes / Processes (foreground) / lane summary; the page + `/api/activity/*` endpoints live on its own Kestrel port (ActivityWebApp.cs). The old `/panel/background` path still resolves (302 → `/panel/activity`). **Owns the shell↔panel navigation bridge** (see below) |
| `plugins/NetPI.Ideas/` | Registers the **Ideas** panel — `("ideas", "Ideas", "★", "http://127.0.0.1:{port}/panel/ideas", 8)`: the per-project "do later" memory bank (`ideas.json` in the session workspace; agent tools `idea.add/list/get/update/remove`). Page + `/api/ideas*` endpoints on its own Kestrel (IdeasWebApp.cs), 4 s poll. Two panel actions reach the shell: **insert into chat** publishes a `ChatPrefillEvent` on the host bus (WebApp forwards `chat.prefill` to all clients; each shell applies it to its own visible session's draft + focuses the composer) — the bridge stays navigation-only, so this event is the sanctioned panel→composer path; **"do now"** calls the runner contract (`IAgentRunner.StartRunAsync`) on the project's most recent session (or creates one), so the run's events flow to the UI exactly like a chat send (a full pool = QUEUED, not rejected), and the page then navigates the shell to that session through the normal `netpi.panel.openSession` bridge |
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
  Work — NetPI.Web and NetPI.BackgroundTasks register none, see below). Tab labels
  `writing-mode: vertical-rl` (see `.vertical-tab*` in `app.css`; rail width is
  `--right-rail-width: 26px` in `:root` — the `26` in `App.svelte`'s
  `shellStyle` must stay in sync).
- Clicking a tab: `ui.setRightTab(id, true)`; clicking the active tab toggles
  the panel. Content area: shell title row + `<iframe>` when the selected id
  is in the catalog, else "No panels available."
- **Guard**: an `$effect` falls back to the first catalog panel if the active
  tab disappears (e.g. a reload dropped it).
- **Iframe semantics**: the `<iframe>` is keyed on the panel `entryUrl` — a catalog
  refresh that leaves `entryUrl` unchanged does **not** reload the visible panel, but
  switching tabs (or a panel swap) remounts the frame. Remounting is what re-registers
  the new `contentWindow` with the navigation bridge (below); a stale/unmounted frame
  can never navigate.
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
- **Navigation bridge (astra-2 §12.3)**: a panel may ask the shell to open a
  session, but only through the versioned `postMessage` envelope below — the shell
  validates the *frame* (`event.source` + `event.origin`) and the *shape* (type,
  version, bounded session id). See "Shell↔panel navigation bridge". The page still
  serves its own static assets and may open its own `ws://127.0.0.1:5173/ws`
  connection and speak the §41 protocol (as before); the bridge is the *only*
  panel→shell command channel and it is navigation-only (select/open a session —
  never spawn, resume, cancel, or change lanes).
- A same-origin panel page can read the shell's `localStorage` (`netpi.ui.v2`)
  — keep panel content cross-origin to preserve the isolation the design
  intends.

## Shell↔panel navigation bridge (astra-2 §12.3)

A cross-origin panel (today the Work panel) can navigate the shell to a session
by activating an agent row. The protocol is a versioned `postMessage` envelope,
checked on **both** the frame and the shape:

```
page → shell: { type: "netpi.panel.openSession", version: 1, payload: { sessionId: "…" } }
shell → page: { type: "netpi.panel.init", version: 1 }            (one-time handshake)
```

**Frame checks (shell, `panel-bridge.ts` + `App.svelte` + `RightPanel.svelte`):**

- The shell registers the **currently mounted** panel iframe's `contentWindow`
  and its registered entry-URL origin (in `panel-bridge.setActivePanel`, driven by
  `RightPanel`'s keyed `<iframe>` `onload` / effect-cleanup). A message is
  accepted **only** when `event.source === that window` **and**
  `event.origin === that registered origin`. A matching type string from any other
  origin, a stale (unmounted) frame, or an unregistered origin is rejected.
- The shell's origin reaches the page via a one-time `netpi.panel.init` handshake
  posted to the registered frame — **never** `"*"`, **never** a hardcoded port.
  The page then echoes navigation back to that exact origin only.

**Shape checks (shared: `ActivityBridge.Validate` in C# = `panel-bridge.handlePanelMessage`
in the shell):**

- `netpi.panel.openSession` requires `version: 1`; `payload.sessionId` must be a
  bounded string (≤ 128 chars, no whitespace/control chars).
- The **legacy** Activity envelope `netpi.activity.openSession` (no version field)
  is accepted **temporarily** with the same frame + shape checks. It is deprecated
  (this doc) and will be removed in a later round once no old Activity page build
  is in use.

**Navigation is navigation-only.** An accepted message opens the session through
the existing `ws.openSession` / `session.open` machinery — selecting an existing
tab or adding a session by ID (a session absent from the loaded first-50 rows
still opens by ID). A deleted/unknown session surfaces a small visible notice —
the shell never silently navigates elsewhere. Row activation never spawns,
resumes, cancels, or changes lanes.

**Frontend migration (astra-2 §12.4):** the persisted right-panel tab selection
migrates one-time `activity` → `background` (the Work panel id) in
`ui.svelte.ts`, preserving the panel's width and open state. This is a stored
key remap, not a hardcoded content branch.

**Frontend state (astra-2 §13):** `store.assignments` tracks nonterminal
assignments per session (from `agents.state` / `agent.updated` events); a session
with a queued / waiting / suspended (non-live) assignment shows a tab badge
**distinct** from the running dot. A background child session's creation is
metadata/intent only and must not steal focus from the parent.

## Reference implementation: the panel catalog today

There are exactly **two** production panels — NetPI.Web registers **no** panel
of its own (its former standalone "plugins" panel was folded into the
Diagnostics plugin's "Plugins" sub-tab; `WebPlugin.cs` registers nothing), and
NetPI.BackgroundTasks no longer registers one either (astra-2 §12.1):

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
- `plugins/NetPI.BackgroundTasks/` — **registers no panel** (astra-2 §12.1).
  `BgWebApp.cs` still serves the embedded `panels/background.html` plus
  `GET /api/bg/jobs`, `GET /api/bg/{id}/output?chars=` (tail of the bounded
  output ring) and `POST /api/bg/{id}/kill` on its own port (default 5275, `plugins.netpi.backgroundtasks.port`);
  it keeps **process ownership** and its 4 background tools. The old
  `panel/background.html` is no longer a registered tab — the combined Work panel
  (below) owns that tab id now.
- `plugins/NetPI.Activity/` — **the combined Work panel** (astra-2 §12.1).
  `LoadAsync` registers
  `("background", "Work", "▶", "http://127.0.0.1:{port}/panel/activity", 5)`
  (absolute URL; default port 5276, `plugins.netpi.activity.port`).
  `ActivityWebApp.cs` serves the embedded `panels/activity.html` plus
  `GET /api/activity/agents` (lifecycle rows + pool snapshots + monotonic revision +
  per-service availability; legacy `runs` kept; each live row carries its `lifecycle` (lower-case wire string: running, queued, waiting, suspended, …), `executionMode` (`pooled` or `cloud-direct`), and `reason` (the waiting/suspended cause), and the response also carries a bounded `history` of recent TERMINAL rows — terminal assignments never appear in the live `agents` list),
  combined `GET /api/activity/work` snapshot plus the coalesced
  `GET /api/activity/events` Server-Sent Events feed for agent, lane, and managed
  process changes), `POST /api/activity/agents/{id}/cancel` (orchestration contract
  with subtree semantics; legacy runner fallback), `GET /api/activity/processes`
  (background jobs + foreground shell processes, newest-first — no running-first
  regrouping), `GET /api/activity/processes/background/{id}/output?chars=` (the
  **latest** tail of the bounded ring) and
  `POST /api/activity/processes/background/{id}/stop`. The old `/panel/background`
  path still resolves (302 → `/panel/activity`). All services are resolved LAZILY —
  a missing orchestration/lanes/BackgroundTasks/Tools/Agent plugin degrades the
  relevant section to "unavailable" instead of erroring, so the panel never blocks
  chat. It is presentation-only: unloading it never touches the work it shows.

**Deploying a new NetPI.Web build:** `dotnet build` the plugin →
`pwsh tools/publish-plugins.ps1 -Configuration Debug` → `plugin.reload` in the
UI. No ordering constraint: every generation loads from an immutable
snapshot (`~/.netpi/plugin-cache/<plugin>/<gen>/`, see AGENTS.md "Gotchas"),
so the staged folder is free to be overwritten while a generation is live —
the reload snapshots the new bytes as generation N+1.

## Testing & verification

- `dotnet test NetPI.sln` — `WebPanelRegistryTests` covers scoped-unload and
  same-id replacement (the two core invariants).
- `WorkPanelDataTests` / `WorkPanelBridgeTests` (astra-2 §12/§13) cover the
  combined Work panel: agents newest-first ordering + stable id tiebreak, the
  monotonic revision + bounded terminal history, processes newest-started-first
  with no running-first regrouping, cancel through the orchestration contract
  (subtree) with the legacy runner fallback, per-service independence (kill one
  service → its section degrades, the others still serve), the **latest-tail**
  output contract (not the tail of the first fetched chunk), the panel
  registration (`background`/Work/order 5) + `/panel/background` 302, and the
  bridge envelope shape (current + legacy type, version, id bounds).
- `PluginHotSwapTests` (same suite) prove the snapshot hot-swap: load from
  snapshot, staged folder rewritable while a generation is live (reload picks
  up new bytes), prune-to-two with ALC collectibility.
- Not covered: `ui.panels` broadcast behavior in `WebApp` (would be an
  integration test), end-to-end iframe rendering, and the **frame** half of the
  navigation bridge (source/origin checks live in the Svelte shell —
  `panel-bridge.ts`; its SHAPE half is the C# `ActivityBridge` covered above).
- The reference implementation is the Diagnostics "Diagnostics" panel (above);
  `plugins/NetPI.TestPlugin` (the reload/lease fixture, published only with
  `-IncludeTestPlugin`) is the place to add a *second*-plugin panel test if
  cross-plugin catalog behavior ever changes.
- After any frontend change: `npx vite build` + reload the Web plugin or
  refresh the browser (hashed assets) — see AGENTS.md.

## Known gaps (TODO for future agents)

1. The shell iframe is keyed on the panel `entryUrl`, so a plugin reload that
   keeps the SAME entry URL (a fixed port) does NOT force a frame reload; the
   panel pages mitigate with self-healing (the Work page reconnects its event
   feed and resyncs on visibility; diagnostics re-opens its host WS after a drop). A stale/unmounted frame
   is de-registered from the navigation bridge, so it can't navigate — but the
   frame itself is not keyed on plugin generation.
2. No integration-level test for the `ui.panels` broadcast or the panel routes
   beyond `WorkPanelDataTests` — registry mechanics are unit-tested.
3. The navigation bridge's **frame** checks (source/origin) live in the Svelte
   shell (`panel-bridge.ts`) and are not unit-tested here (no JS test runner); the
   envelope **shape** is shared with the C# `ActivityBridge` and IS covered by
   `WorkPanelBridgeTests`.
