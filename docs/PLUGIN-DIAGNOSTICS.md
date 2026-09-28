# Diagnostics plugin (`netpi.diagnostics`)

The **Diagnostics** tab (right panel) is for looking inside a running NetPI: loaded plugins, registered tools and RPC
methods, the recent event stream and the log. It also provides the `/reload` command for hot-reloading plugins.

- Plugin: `plugins/NetPI.Diagnostics`, id `netpi.diagnostics`, start order 90.
- Tab: `{ id: "diagnostics", title: "Diagnostics", panel: "right", icon: "bug", order: 90, module: "ui.js" }`. The UI module
  goes in `plugins/NetPI.Diagnostics/wwwroot/ui.js` (source in `plugins/NetPI.Diagnostics/ui/`).
- Slash command: `{ name: "reload", argsHint: "[pluginId]", rpc: "diag.reload" }`.
- It is also the way to inspect the running app from outside: the `diag.*` overview, problems, model and tool calls, the
  event journal, runs, tool-set changes, logs, settings and failed requests (`docs/DEBUGGING.md`). The tab shows the
  problems as a strip above the views and the model calls in its Calls view.
- It registers the read-only **`diag` tool** (`docs/TOOLS.md`): the same views for the agent, as one action per method.
  Only the inspecting methods are reachable, so `/reload` and the rest stay the user's — with one exception, the `rpc`
  action, which reaches **any** method whose registration says `readOnly` (`ctx.Rpc.Register(…, readOnly: true)`, reported
  by `rpc.list`). Unmarked means "may write" and stays out; `diag.rpc` and `diag.reload` are refused outright, the answer
  is cut at 200k characters, and a method that does not answer in 30 s is called out rather than waited on. That is what
  makes "a fact diag does not wrap" one tool call instead of a hand-rolled POST with the token out of `server.json`
  (idea-de1s7t). A plugin that registers only reads should mark them.

## `diag.toolsets`

`{ sessionId }` → the context plugin's `context.toolsets`: the session's tools now, the baseline of its first model
call, and every change with the cause the "tools" notice gave the model (`plugin-reload` with the plugin ids,
`profile`, `user`, `settings`, `unknown`). Error `unavailable` when the context plugin is not loaded — it is what keeps
the record.

## `diag.snapshot`

`{ events?: number /* default 200, max 5000 */ }` →

```ts
{
  plugins: PluginInfo[] | null;   // IPluginManager.List(); null without a plugin manager
  tools: ToolRow[] | null;        // the "tools.list" RPC when present, else built from the tool registry
  rpc: { method: string; description?: string; pluginId: string }[];   // sorted by method
  events: { seq: number; type: string; sessionId?: string; time: string; source?: string }[];  // oldest → newest, no payloads
  logs: any | null;               // result of the "logs.recent" RPC { max: 200 } when present, else null
  runtime: { pid; framework; os; workingSetMb; gcHeapMb; threads; uptimeSeconds };
  time: string;                   // ISO 8601
}

interface PluginInfo { id; name; description?; version?; directory; assembly?; state: 'unloaded'|'loading'|'running'|'stopped'|'failed'|'disabled';
  error?; loadedAt?; loadCount; loadMs; order; enabled }
// From tools.list (host):     { name, label, description, category, readOnly, pluginId, active }
// Fallback (registry):        the same fields plus priority; active = this registration wins for its name
```

- If one part fails, that part becomes `{ error: string }` and the rest of the snapshot is still returned.
- `events` comes from the bus's recent-event ring buffer (`IEventBus.Recent`). Session-scoped high-volume events such as
  `stream.delta` are included, so the tab should offer a type filter (for example hide `stream.*` by default).

## `diag.event`

`{ seq }` → `{ seq, type, sessionId?, time, source?, ui: boolean, data: any }`: the full payload of one recent event,
serialized to JSON. `not_found` when the event has already dropped out of the ring buffer. Use it to show an event's
details when the user clicks a row.

## `diag.reload` (the `/reload` command)

`{ sessionId?, args?: string }` (also accepts `id`) → `string` toast.

| `args` | behaviour |
|---|---|
| empty, `all`, `*` | `IPluginManager.RescanAsync()`, then `ReloadAsync` for every enabled plugin in start order, with `netpi.diagnostics` last. Runs in the background and returns right away with `"Reloading N plugins…"`. |
| a plugin id or name | Waits for the reload and returns `"Reloaded <name> (<id>)"`, or `"Reloaded <id>, but it failed to start: <error>"`. Reloading `netpi.diagnostics` itself runs in the background. |

Plugin matching: the exact id, then the id or name ignoring case, then an id suffix (`retry` → `netpi.retry`), then a
unique substring of an id or name. Errors: `ambiguous` (the message lists the candidates), `not_found` (the message
lists the known ids), `unavailable` (no plugin manager).

For reload status, the tab can listen to `plugins.changed` and call `diag.snapshot` (or `plugins.list`) again.

## Suggested layout

Show four collapsible sections:

- **Plugins**: state badges, a reload button per row that sends `diag.reload { args: id }`, and the error text of failed
  plugins.
- **Tools / RPC**: searchable tables.
- **Events**: a live list that appends from `ctx.on('*', …)` while the tab is visible, with a type filter, and a detail
  pane (`diag.event`).
- **Logs**: only when `logs` is not null.

The Context view also shows a **Tool changes** section per chat (`diag.toolsets`): every change with its cause badge and
the plugins behind it, so the tab says the same thing the chat's notice does. Recent reloads are in the problems strip
and in `diag.overview`'s `reloads` (which plugins, when, and which chats were mid-turn).
