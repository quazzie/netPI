# AGENTS.md — netPI

Agent operating instructions for this repository. netPI is a small .NET 10
agent harness: a permanently-loaded host loads **reloadable plugins** from
`plugins/`, runs an agent loop over an external AiProxy (OpenAI-compatible,
usually at `127.0.0.1:8090`), and serves a Svelte Web UI over WebSocket +
static files on Kestrel `127.0.0.1:5173`.

The authoritative design doc is `docs/archive/PLAN-v1.md` (2790 lines, §-numbered
— cite sections when referring to behavior). Active plan: `docs/plans/responses-wire.md`.
Web panels (plugin-contributed right-panel tabs): `docs/web-panels.md`.

## Repo layout

```
src/NetPI.Abstractions/   Core contracts (plugins may reference; nothing else may).
                          INetPiPlugin, IPluginContext, IAgentRuntime, IAgentRunner,
                          ISessionStore, IToolRegistry, ISteeringQueue, ICompaction,
                          IModelRetryPolicy, AgentEvent, ModelEvents, wire types.
src/NetPI.Host/           netPI.Host.dll — the only always-loaded runtime.
                          PluginManager (collectible ALCs, gen staging, reload),
                          HostRuntime, ConfigService, event bus, CLI loop.
                          Entry: src/NetPI.Host/Program.cs
src/NetPI.Desktop/        WinForms + WebView2 window that spawns the host and
                          points WebView2 at :5173. (The "app" users see.)
plugins/                  One folder per plugin; each now holds only a `current.json`
                          build POINTER (astral-1 P1) naming an immutable artifact
                          under `.artifacts/plugins/<id>/<buildId>/`. `NetPI.TestPlugin`
                          is not published (test fixture, loads legacy). Per-plugin payloads:
                            NetPI.Agent          agent runtime, runner, event loop (§10-12)
                            NetPI.Context.Pi    system-prompt + workspace-context layering (§15-17)
                            NetPI.Provider.AiProxy  provider + model catalog (§13-14, §14b/§14c)
                            NetPI.Storage.Sqlite  session store ("sessions") (§29-30)
                            NetPI.Tools         read/write/edit/grep + bash/powershell (§18-26)
                            NetPI.AutoCompact   context compaction (§31-33)
                            NetPI.Retry         model-retry policy (§34)
                            NetPI.BackgroundTasks  background jobs + 4 tools (§27-28); "background"
                                                              panel on its own :5275 Kestrel
                                                              (docs/web-panels.md)
                            NetPI.Web           Kestrel Web surface, /ws, /api/file (§36, §41).
                                                              Registers no panel (its former
                                                              "plugins" panel was folded into
                                                              NetPI.Diagnostics, docs/web-panels.md);
                                                              shell has no hardcoded tabs.
                            NetPI.Diagnostics     standalone :5274 diagnostics Kestrel, registers
                                                              the "diagnostics" panel (tabs: overview,
                                                              wire, plugins, models, events, logs,
                                                              sessions).
                            NetPI.TestPlugin    reload/lease test fixture
web/netpi-web/            Svelte 5 + Vite frontend (pnpm). Built into dist/ (git-ignored).
tests/NetPI.Host.Tests/   150 xunit tests; the integration surface.
tools/publish-plugins.ps1 Publishes each plugin as an IMMUTABLE build (astra-1 P1):
                              marker-discovers `plugins/*/ *.csproj` (no hardcoded list;
                              TestPlugin excluded unless -IncludeTestPlugin), `dotnet publish`
                              → `.artifacts/plugins/<id>/<buildId>/` + `artifact.json`
                              manifest (every file pinned by sha256+size) → validate →
                              atomically flip `plugins/<id>/current.json`. Idempotent (same
                              bytes → same buildId → no rewrite). `-Reload` asks a running
                              host to swap the build and confirms the new buildId; `-NoBuild`
                              re-consumes a prior artifact; `-Plugins` selects a subset. The
                              host resolves the pointer, snapshots into per-instance
                              plugin-cache dirs (what the ALCs actually load), never from
                              `.artifacts/` or `plugins/` directly.
tools/keep-alive-host.ps1 Runs the host with stdin held open (background job).
```

Runtime home: `~/.netpi/` — `config.json`, `netpi.db` (SQLite), `logs/`,
`plugin-cache/` (immutable per-host-instance snapshots: `plugin-cache/<host-instance>/<plugin>/<attempt>-<buildId>/`
— the host loads from these, never from `.artifacts/` or `plugins/` directly), and
`app-cache/` (host launch snapshots, P0). Env overrides: `NETPI_HOME`, `NETPI_PLUGINS`.

## Build & run (verified on this machine)

```bash
# 1. Build everything (.NET 10 SDK, pinned by global.json)
dotnet build NetPI.sln

# 2. Build the Svelte frontend → web/netpi-web/dist (the Web plugin serves this dir;
#    staticRoot is configured in ~/.netpi/config.json → plugins.netpi.web.staticRoot)
cd web/netpi-web && npx vite build && cd ../..

# 3. Publish plugins (immutable artifacts + build pointers; see publish-plugins.ps1)
pwsh tools/publish-plugins.ps1 -Configuration Debug [-Reload]

# 4a. Headless host in background (used in agent sessions)
pwsh tools/keep-alive-host.ps1          # via a background job; serves :5173

# 4b. The real desktop app (WinForms window; spawns its own host, or reuses a
#     running one if :5173 already answers)
dotnet build src/NetPI.Desktop
dotnet src/NetPI.Desktop/bin/Debug/net10.0-windows/netPI.Desktop.exe
```

- The AiProxy provider plugin requires `plugins.netpi.provider.aiproxy.baseUrl`
  in `~/.netpi/config.json` (e.g. `http://127.0.0.1:8090`) and throws at load if
  missing. The external proxy process is NOT started by netPI — it must already
  be running.
- `~/.netpi/config.json` keys live under `plugins.<lowercase-plugin-id>` (see
  Config reference below).
- **Never commit** `web/netpi-web/dist/`, `plugins/*/bin|obj`, staged plugin
  DLLs, or `~/.netpi` state (all git-ignored).

## Plugin architecture (the core invariant)

- Lifecycle per generation: `LoadAsync → StartAsync … StopAsync → UnloadAsync`
  (`INetPiPlugin`). LoadAsync registers services/tools/commands and subscribes
  to the event bus; StartAsync is where long-running work begins.
- Each plugin runs in its own **collectible `AssemblyLoadContext`**
  ("`<id>-gen<n>`"); a reload drains leases (poll 10 ms / 30 s), applies the
  plugin's `ReloadPolicy` (netPI.Agent and netPI.Web are `AgentIdle` — reload is
  deferred while `runner.IsRunning`), unloads, and loads a new generation.
  A load failure keeps the previous generation Active. `ScanAsync` (host
  `scan` CLI / `plugin.scan` WS) re-discovers plugins/ and loads+starts any id the
  host has never seen (no restart); a plugin left in `Failed` state can be
  retried with `plugin.reload` after fixing its config/bytes.
- Plugins talk only through `IPluginContext`: `Services` (id-keyed registry),
  `Commands`, `Events` (bus), `OwnConfig` (raw JSON section), `Log`.
  Service ids: agent → `agent`, `steering`, `runner`; AutoCompact → `compaction`;
  Retry → `retry`; Sqlite → `sessions`; AiProxy → `provider`, `catalog`;
  Tools → `tools`, `resolver:bash`, `resolver:powershell`; BackgroundTasks →
  `background`; Context.Pi → `system-prompt`, `workspace-context`.
  Host-owned: `plugins` (facade), `host-config`, `commands`.
- **Never reference another plugin's assembly.** Cross-plugin coupling is via
  the service registry and the event bus only.

### Agent event flow (per turn, PLAN §10-§12, §45)

```
AgentStarting
loop:  drain steering (append as new user msg if non-empty → TurnBoundary)
       BeforeModelRequest
       ModelStreamEvent*            # deltas coalesced 40 ms, lanes text/thinking
       [failure: ModelRequestFailed → ModelRetrying → backoff, or terminal]
       AssistantCompleted
       BeforeToolBatch → BeforeToolCall* (preflight: tool exists, args JSON object)
       → concurrent tool execution (results in original call order)
       → AfterToolCall* → AfterToolBatch
       → compaction checkpoint (if "compaction" service present) → ContextBuilt
TurnBoundary
finally: AgentCompleted | AgentCancelled
```

Model-level wire kinds (`ModelEvents.cs`): `model-started`,
`thinking-started/delta/completed`, `text-started/delta/completed`,
`tool-call-started`, `tool-call-arguments-delta`, `tool-call-completed`,
`usage-updated`, `model-completed`, `model-failed`. Runtime-invented kinds:
`tool-output-chunk` (progressive shell stdout, PLAN §25) and `model-retrying`
(carries attempt/maxAttempts/delayMs in the token fields).

### Steering & compaction

- Steering: per-session channel (`chat.steer` → `ISteeringQueue`); drained only
  at the top of a turn — a steer is appended as a fresh user message, never
  cancels a running tool batch.
- Compaction (AutoCompact, PLAN §31-33): after each tool batch, if
  estimated context > `contextWindow − reserveTokens`, summarize the old part
  (T=0.3, no tools), persist an `EntryKind.Compaction` entry, and rebuild the
  transcript as [run system msg] + [summary as System msg] + retained tail.

## Web surface / WebSocket protocol (PLAN §36, §38, §41)

Kestrel on the configured port (5173): `/ws` (the hub), `/bootstrap` (health),
`/api/file` (localhost-only file viewer for chat-embedded path links) and `/api/open`
(localhost-only; shell-opens a path in the OS default app / Explorer — files
launch their registered handler, folders open in Explorer).

**astra-1 §11a (F/P5) browser control boundary** — loopback binding is NOT origin
validation, so the control routes enforce it explicitly: the `/ws` upgrade and
`/api/open` validate Host/Origin (Host must be a loopback name; an Origin, if
present, must be the same http:// loopback host:port — an absent Origin is allowed
only on a loopback Host = the originless CLI path, and a present-but-wrong Origin
is rejected). `/api/open` is a **POST** (never GET — a stray link/iframe can't
trigger a shell-open; GET → 405). `/api/file` serves active content (HTML/SVG/
MHTML) as a `text/plain` download so a user-supplied path can never render/execute
in the app origin. `chat.send` carries a client `operationId` for idempotent
retries (see below). Outbound (F, §11a): the broadcast/delta fan-out is
**concurrent** (one non-reading client can no longer stall the others —
each delivery is internally bounded) and every delivery is gated by a token
linked to the client-lifetime, the operation, and a bounded send timeout, so
a dead / non-reading client times out instead of holding the send forever
(per-client ordering is preserved by the per-socket write gate; dropped
deliveries resync on the next full broadcast). These gates are tested by
`ControlBoundaryTests` and `OutboundBackpressureTests`.

Envelope: `{ type, payload, requestId?, sessionId? }`.

Client→server commands: `chat.send`, `chat.steer`, `agent.cancel`,
`session.create`, `session.open`, `session.older` (scroll-up pagination,
`beforeSequence`), `session.rename` (title or workspace; fresh sessions are also auto-titled server-side from the first user message — `ChatSendAsync` renames + broadcasts `session.updated` when the session has no title, and a one-shot backfill at Web-surface start renames pre-existing untitled sessions from their first user message), `session.delete`
(rejected while a run is in progress on that session), `session.list` (page of
50; request `offset`, response carries `offset`/`total`/`hasMore` — the drawer
"load more" button fetches continuation pages and auto-advances after a
visible delete),
`session.model`, `session.reasoning`, `session.compact`, `models.refresh`,
`plugin.reload` / `plugin.reloadAll` / `plugin.scan` (rescan plugins/ for folders staged after startup — loads new plugins without a host restart; existing plugins are untouched) / `plugins.list`, `commands.list`,
`workspace.files`, `config.update`.

Server→client events: `agent.state` (Idle/Preparing/CallingModel/
ExecutingTools/Compacting/Retrying/Cancelling), `assistant.started` /
`assistant.completed` (with `usage?`), `thinking.started/delta/completed`,
`text.delta`, `text.completed`, `tool.args` (streamed arg deltas),
`tool.started`, `tool.output` (`append:true` = grow live), `tool.completed`
(with `durationMs`), `usage.updated`, `model.requestFailed`, `model.retrying`,
`session.created/updated/entry/entries/older/compact.result`,
`session.deleted` (client removed the session from the store; if it was the
open session, the drawer starts a fresh one in the same workspace),
`models.updated/refreshFailed`, `plugins.state`, `plugin.state`,
`plugin.reloaded/Failed`, `plugin.scanned` (`loaded` = ids newly scanned in), `ack`, `error`.

Bootstrap on WS connect: `agent.state`, `models.*`, `plugins.state`,
`session.list`, then replay of the active session's **latest 200 entries**.

Invariants worth knowing (see commit history for the fixes behind these):
- `assistant.completed` is emitted when the assistant **turn** closes — after
  the tool batch (via `AfterToolBatch`) or at `AgentCompleted/Cancelled` — NOT
  when the model stream ends. A completed model turn may be followed by tools
  and another model turn within the same assistant message.
- The client must not treat `assistant.completed` as agent idle; `agent.state`
  is the source of truth.
- Scroll-up history: `session.older` takes `beforeSequence` (sequence of the
  oldest loaded entry) + `count` and returns older entries; the client must send
  the `sessionId`. The viewport preserves the visual anchor when a page is
  prepended.

## Frontend conventions (web/netpi-web)

Svelte 5 runes, no framework. `src/store.svelte.ts` is the single store;
`ws.ts` is the socket client; `components/conversation/*` renders transcript
blocks. Transcript rendering is capped: after (re)load only the last
`REVEAL_INITIAL` (40) blocks render; the hidden head reveals `REVEAL_STEP` (40)
at a time via the "Load earlier" button (`store.hidden` / `store.revealed`),
which also drives the scroll-to-top fetch of further `session.older` pages. Markdown goes through `marked` + `DOMPurify` (throttled re-parse while
streaming). File-like paths in assistant text/tool args become file links:
a plain click shell-opens via a **POST** to `/api/open?path=...&sessionId=...` (no page
navigation — the anchor is intercepted and the open is a fetch; §11a F/P5: POST, never
GET, so a stray link/iframe can't trigger a shell-open); ctrl/middle
click keeps the in-app `/api/file?path=...&sessionId=...` viewer (active content
HTML/SVG is served as a `text/plain` download, never rendered in the app origin). Same for
tool-call file names and write/edit artifact pills. Relative paths resolve
against the session workspace; when the session has none (or the id is
missing/unknown) they fall back to the host process CWD (the project root —
the desktop shell and tools/keep-alive-host.ps1 both start the host from
there). Link decoration happens only in the final (done) render, not while
streaming, so file links never reflow the line as tokens arrive (streaming
renders strip hrefs from file-like anchors, keeping them non-navigable).
Streaming text uses stable-prefix incremental rendering: only the paragraph
currently being written is re-parsed each tick; a paragraph is promoted to
the immutable stable prefix at its blank-line boundary (never inside a code
fence), so completed text never re-lays-out. The viewport pins to the bottom
directly (no rAF -- it is throttled in background/hidden WebView2 windows)
and, while a run is busy, re-pins on a 30 ms interval so open thinking
bodies stay followed.
Desktop shell (src/NetPI.Desktop): the WinForms host handles
CoreWebView2.NewWindowRequested -- target=_blank / window.open intents never
open a second window; /api/file viewer URLs are mapped to the referenced
path (relative ones against the project root) and opened via the OS
(default app, Explorer for folders); any other URL opens in the default
browser.
Thinking blocks collapse
by default; Settings → "Keep thinking open" (left panel) expands them —
including while streaming, replacing the one-liner. Tool calls are collapsed
by default (a manual toggle always wins; running shell calls no longer auto-expand). Settings →
"Keep tool calls open" keeps them expanded. Both settings persist in
`localStorage` (`netpi.ui.v2`) via `src/ui.svelte.ts`. `dist/` is git-ignored: after frontend
changes run `npx vite build` and **reload the Web plugin** (Web UI → reload, or
`plugin.reload` with id `netpi.web`) — no host restart needed.

## Config reference (`~/.netpi/config.json` → `plugins.<id>`)

| plugin | keys |
|---|---|
| `netpi.web` | `port` (5173), `staticRoot` (path to `web/netpi-web/dist`), `maxWsMessageBytes` (1 MiB default; a larger WS message → `PolicyViolation` close, §11a F) |
| `netpi.provider.aiproxy` | `baseUrl` (**required**), `apiKey`, `wire` = auto\|chat\|responses (default auto: responses with probe fallback; §14c `previous_response_id` chaining per session\|model) |
| `netpi.storage.sqlite` | `database` (default `~/.netpi/netpi.db`) |
| `netpi.autocompact` | `enabled`, `reserveTokens` (16384), `keepRecentTokens` (20000), `defaultContextWindow` (131072), `maxContextMessages` (4096) |
| `netpi.retry` | `enabled`, `maxAttempts` (3), `baseDelayMs` (500), `maxDelayMs` (5000) |
| `netpi.tools` | `bash.executable`, `powershell.executable` (auto-detected otherwise) |
| `netpi.diagnostics` | `port` (5274) |
| `netpi.testplugin` | `loadFail`, `generation`, `register` |
| `netpi.backgroundtasks` | `port` (5275) |
| `netpi.agent`, `netpi.context.pi` | (none) |

## System-prompt layering (NetPI.Context.Pi, PLAN §16-17)

Base prompt ← `.netpi/AGENTS.md` layers discovered broad→specific (home
`~/.netpi` → drive root → … → workspace; `AGENTS.override.md` replaces, not
appends) ← `SYSTEM.md` (replaces base) ← `APPEND_SYSTEM.md` (global→project
append) ← tool guidelines + available tools + environment. This file (the
workspace-level `AGENTS.md`) is one of those layers — keep it accurate to the
repo, it feeds every run in this workspace.

## Tests & verification

```bash
dotnet test NetPI.sln        # 150 tests (agent runtime scenarios, session
                             # store, plugin manager, shell detection, …)
cd web/netpi-web && npx svelte-check --tsconfig ./tsconfig.app.json
```

Known pre-existing svelte-check errors in `Composer.svelte` (implicit `any`)
exist on master and are unrelated to UI work.

## Git discipline

- Commit only when a thing is fully verified AND done; never as a mid-task
  checkpoint.
- Push only when the user says to. Never `git push` on its own.

## Gotchas learned the hard way

- `plugins/NetPI.Web/WebApp.cs` has been edited without ever being compiled —
  **always `dotnet build`** after touching it (it was shipped with literal
  `\n` text in source, an unregistered `/api/file` route, and a call to a
  non-existent `ContentTypes` helper).
- The desktop shell reuses an already-running host on :5173 (it does not
  double-spawn); `tools/publish-plugins.ps1` publishes an immutable artifact and flips
  the plugin's `current.json` pointer — after changing plugin code, run it and reload
  the plugin (or restart the host). A failed/missing build never touches a live pointer.
- Stale plugin generations: a failed load keeps the previous gen Active, so
  "loaded" in the log may mean your code change isn't actually running —
  check the log's generation/timestamps.
- Reloading a plugin that registers a service other plugins hold (e.g. netpi.storage.sqlite
  "sessions") is safe only because stores are NOT disposed in StopAsync and
  consumers re-resolve lazily. Disposing shared state in StopAsync strands
  consumers on a dead instance until they reload too.
- `~/.netpi/plugin-cache/<host-instance>/<plugin>/<attempt>-<buildId>/` is the snapshot every
  generation loads from (immutable once snapshotted) — the host never loads from
  `.artifacts/plugins/<id>/<buildId>/` or `plugins/<name>/` directly. Publishing a new
  build only flips `plugins/<id>/current.json`; a `plugin.reload` re-resolves the pointer
  and snapshots the new bytes as the next attempt. Stale per-plugin snapshots are pruned
  to the newest `MaxCachedGenerations` (default 2) once their ALCs are finalized;
  ownership-aware prune (a `.owner` token) keeps one host from deleting another's cache.
  (`NETPI_SKIP_CACHE` is gone — the snapshot copy is the hot-swap mechanism.)
