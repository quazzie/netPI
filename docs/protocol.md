# Web surface & WebSocket protocol (deep doc)

`AGENTS.md` keeps the command/event inventory; this is the behavioral reference:
how the surface is wired, the control boundary, and the invariants with their
"why". Source of truth: `plugins/NetPI.Web/WebApp.cs`.

## Endpoints

| route | kind | purpose |
|---|---|---|
| `/ws` | WS upgrade | the hub — bootstrap, commands, event fan-out |
| `/bootstrap` | GET | liveness probe (any status = surface is up) |
| `/api/file?path=&sessionId=` | GET | in-app file viewer for chat-embedded path links (ctrl/middle-click) |
| `/api/open?path=&sessionId=` | **POST** | shell-opens a path in the OS default app / Explorer |
| `/identity` | GET | launcher probe — proves :5173 is a netPI host, reports build id |

`/api/file` serves active content (HTML/SVG/MHTML) as a `text/plain` download —
a user-supplied path can never render/execute in the app origin. `/api/open` is
POST-only: a stray link or iframe cannot trigger a shell-open (GET → 405).
Relative paths resolve against the session workspace, falling back to the host
process CWD (project root) when the session has none.

## The control boundary (§11a F/P5)

Loopback binding is **not** origin validation — a malicious page in any browser
can open a cross-origin request to `127.0.0.1:port`. So the control routes
enforce it explicitly in `ControlOriginIsAllowed(host, origin, boundPort)`:

- **Host** must be a loopback name (`127.0.0.1`, `localhost`, `::1`, optional
  `:port`). Non-loopback Host → reject, even before the WS upgrade.
- **Origin**, when present, must be the same `http://<loopback>:<boundPort>`.
  A present-but-wrong Origin is rejected — an *absent* Origin is allowed only on
  a loopback Host (the originless CLI path). Absent/spoofed Origin is not proof
  of trust; the check is about who *can* present the right Origin.
- For a loopback-only control surface the same-origin check **is** the
  anti-forgery proof: a cross-origin page cannot present the local app's Origin
  (browsers set Origin from the page's own URL), so only a document genuinely
  served by this host can drive mutating routes. Adding a session/CSRF token
  on top would be a login-flow for a single local user, which the plan rules
  out. Origin/Host gate + POST-only `/api/open` + active-content-as-download
  together form the complete boundary.

WS-side: inbound messages are assembled until `EndOfMessage` with a total-byte
cap (`maxWsMessageBytes`, config `plugins.netpi.web.maxWsMessageBytes`, 1 MiB
default) — oversized → close `PolicyViolation`.

## Envelope

`{ type, payload, requestId?, sessionId? }` — same shape both directions.
Every command is answered with an `ack` (or `error`) carrying the `requestId`;
`chat.send`'s `operationId` makes client retries idempotent (duplicate
`operationId` + session → no second run; project-snapshot entries dedupe by it).

## Client → server commands

| command | notes |
|---|---|
| `chat.send` | `operationId` idempotency; fresh sessions auto-title server-side from the first user message (rename + `session.updated` broadcast, one-shot backfill of pre-existing untitled sessions at surface start). astra-2 §13: a full pool is an **accepted queue, not a rejection** — the run starts with disposition `admitted` or `queued`; a `queued` start persists a durable `Queued` assignment (root agent + assignment row, `runId` = `operationId`), echoes the user entry, and ACKs (no `error` frame, no `agent.state` — the tab badge arrives via `agents.state`/`agent.updated` from the durable row). Retrying the same `operationId` replays the stored accept and never creates a second assignment. Validation/policy/queue-limit errors remain `error` frames |
| `chat.steer` | per-session; rejected without `sessionId` while >1 run is active (astra-1 E — no global "active session" fallback) |
| `agent.cancel` | optional `runId`: present → cancels that run, **including queued and suspended records** (astra-2 §13: the runner's `CancelRun` + `CancelQueuedRun` pair purges the live record, the runner's queue record and the lane scheduler's queue entry); optional `sessionId` → cancels every running run in that session; absent → legacy cancel of all active runs. The orchestrator (Work panel) cancels by `assignmentId` through the same runId-matched path |
| `session.create` / `session.open` / `session.rename` / `session.delete` | delete rejected while a run is in progress on the session; if the open session is deleted the drawer starts a fresh one in the same workspace |
| `session.older` | scroll-up pagination (`beforeSequence` + `count`) |
| `session.list` | page of 50 (`offset` → `offset`/`total`/`hasMore`); optional `query` = **server-side search over ALL stored sessions** (astra-1 G1: title + workspace, case-insensitive, `ESCAPE`d LIKE in SQLite) — the picker searches the store, not just loaded pages; a `query`-carrying frame is echoed back in the payload |
| `session.model` / `session.reasoning` / `session.compact` | session-scoped settings |
| `session.mode` | astra-2 §10: persist the session's mode — `"chat"` (default) or `"orchestrate"` (coordinator guidance); `null`/empty clears back to chat. Stored by the session store (`SetModeAsync`) and echoed in the session snapshot, shaped like `session.model` |
| `session.project` / `session.project.refresh` | select/clear the session's project (`operationId` idempotent); idle sessions apply at once, running ones go **pending** and apply at the run's safe boundary; refresh re-snapshots the project's instructions |
| `runs.list` | all active + recent runs, every session (Activity surface's run query) |
| `project.list` / `project.create` / `project.update` | project management surface (astra-1 C/D); `create` upserts by canonical workspace path |
| `models.refresh` | re-probe the catalog |
| `plugin.reload` / `plugin.reloadAll` / `plugin.scan` | hot-swap a plugin build without restart; `scan` re-discovers `plugins/` and loads ids the host never saw |
| `plugins.list` / `commands.list` / `workspace.files` / `config.update` | introspection + workspace listing + config (reject+atomic, last-known-good) |

## Server → client events

`agents.state` (astra-2: `{agents:[row]}` assignment snapshot, grouped per session), `agent.updated` (astra-2: `{row}` single-assignment delta), `lanes.state` (astra-2: pool capacity snapshot) - published by NetPI.Orchestration, forwarded by the Web hub; `agent.state` (Idle/Preparing/CallingModel/ExecutingTools/Compacting/Retrying/
Cancelling — the **source of truth** for run state), `assistant.started` /
`assistant.completed` (with `usage?`), `thinking.*`, `text.delta` /
`text.completed` (deltas coalesced 40 ms, text/thinking lanes), `tool.args`
(streamed arg deltas), `tool.started` / `tool.output` (`append:true` = grow
live) / `tool.completed` (`durationMs`), `usage.updated`, `model.requestFailed`
/ `model.retrying` (attempt/maxAttempts/delayMs), `session.created/updated/
entry/entries/older/compact.result/deleted`, `session.project.pending` /
`session.project.applied`, `models.updated/refreshFailed`, `plugins.state`,
`plugin.state/reloaded/Failed/scanned`, `ack`, `error`.

**Bootstrap on connect**: `agent.state`, `models.*`, `plugins.state`,
`ui.panels`, `session.list` (queryless page 1), then replay of the active
session's latest 200 entries. The bootstrap's `session.list` is queryless — a
search response carries `query` in its payload and is the one to await.

## Invariants (and why)

- **`assistant.completed` fires when the assistant *turn* closes** — after the
  tool batch (`AfterToolBatch`) or at `AgentCompleted/Cancelled` — NOT when the
  model stream ends. A completed model turn may be followed by tools and another
  model turn inside the same assistant message.
- **The client never treats `assistant.completed` as idle** — `agent.state` is
  the only idle signal.
- **Outbound fan-out is concurrent** (§11a F, astra-2): one non-reading client
  cannot stall the others. Every client-directed send is enqueued to that client's
  unbounded outbox (O(1), never blocks the publisher), and a SINGLE writer per
  connection drains it in FIFO order — so per-client wire order equals enqueue
  order, independent of task scheduling (the old per-socket `_sendGate` only
  serialized writes, it did not order them; two fire-and-forget fan-out tasks
  could take it out of emission order — the captured `text.completed`-before-
  `text.delta` flake). Each send is gated by a token linked to the client
  lifetime + a bounded delivery timeout (`DeliveryTimeoutMs` = 5 s), so a dead
  client times out instead of holding the queue. Dropped deliveries resync on
  the next full broadcast. DRAIN ORDERING: delta flushes enqueue their events
  while holding the per-session batcher gate, and boundary events flush
  synchronously on the publishing thread — so every `text.delta` of a drain
  precedes the `text.completed` a boundary emits next, on every client.
- **Reloaded services re-resolve**: after a plugin reload completes the Web
  surface re-resolves runner/agent/catalog/steering/compaction (`RefreshReloadableServices`),
  because the host registry removes the old generation's services. Consumers of
  shared stores (e.g. `sessions`) rely on stores NOT being disposed in
  `StopAsync`.
- **Old-run event gating**: per-session active-run claim (`_activeRun`); a
  `model-started` claims the run and flushes any stale delta batcher, any
  subsequent event from a non-owning run is dropped — a stale/cancelled run's
  tail cannot bleed into a new run's stream.
- **Per-client ordering survives concurrent fan-out** because each `Client`
  owns a FIFO outbox drained by one writer (astra-2); concurrency is across
  clients, never within one. Drains and boundary events enqueue in gate order,
  so the wire is deltas-then-completed even when two tasks publish.
- **A disabled direct-cloud deployment is rejected BEFORE inference** (astra-2 §11.2): the runner asks the trusted policy source (`deployments`) for the model's execution mode; a `DirectCloud` deployment recorded as disabled refuses the request with an actionable note and the provider is never called — no paid call even while the local queues are full. The same gate is enforced at the provider by the `cloud-gate` reservation: a direct-cloud model no team's budget policy claims has no budget, so the reservation is denied before the request goes on the wire.
- **Full capacity is an accepted queue, not a rejection** (astra-2 §13/§16): a `chat.send` that cannot admit a lane persists a durable `Queued` assignment and ACKs — it never fails with "all concurrent runs busy". The `Queued` row is the source of truth the UI reads (tab badge via `agents.state`/`agent.updated`); the run executes NOTHING until the lane scheduler admits it from the queue, and a queued record is cancelable by `agent.cancel` (runner + lane queue purged) before it ever starts. Only validation/policy/queue-limit errors surface as `error` frames.
- **The Work panel reads, never drives** (astra-2 §12): `GET /api/activity/*` are presentation-only query surfaces on the Activity plugin's own Kestrel port; the only stateful surface is `POST /api/activity/agents/{id}/cancel`, which delegates to the orchestration contract (subtree semantics) with a legacy runner fallback.
