# NetPI UI ⇄ Server protocol

Agent rework additions (2026-10-01): `agents.resources` returns distinct resource rows `{key,model,capacity,busy,queued,available,unavailable,owners}`; `agents.list` adds `resource`, `resourceCapacity`, `resourceBusy`, `resourceQueued`, and waiter `priority`/`waitingFor`. `agents.changed` includes both `agents` and `resources`. Instance counts are per agent; resource counts are shared across names and plugin versions.

`ideas.work` returns background work rows `{id,purpose,model,sessionId,projectId,status,reason,since,finishedAt}`; `ideas.workChanged` carries `{work}`. `ideas.unread` returns the commits the sweep could not decide after its bound of attempts, `[{repo,hash,subject,tries,error,at}]` — the cursor has moved past them, so later commits are read; the record is what is re-read on demand. `ideas.capabilities` reports optional decision/history/catalog presence. `ideas.review {id}` returns `{ideaId,revision,review}` — why an idea's completion is not judged automatically and what a reviewer has to read: `{limit,at,revision,commits:[{hash,short,subject,repo,files?,note?}],requirements:[…]}`, or `review:null`; the same state is a "Needs review" section on the idea (idea-n2jj97). `ideas.verifyUpdate {id,expectedRevision,patch,evidence}` verifies a proposed update and returns `{applied,reason,idea?}`; revision conflicts fail rather than overwrite. Verified suggestions add `verified` and `verification`.

`files.commits {sessionId?,cwd?,hash}` additionally reads bounded patch evidence `{hash,patch,truncated}` or null. `diag.capabilities` reports plugin registrations and optional feature presence, which is separate from endpoint health. `budget.allow` adds `executionAvailable` and `continuationReason` when continuation cannot start. Goals add status `execution-unavailable`, requiring explicit resume after executor return.

Host registry events `services.changed {contract}` and `rpc.changed {method}` contain strings only. `resources.released {resource}` wakes replacement schedulers. Plugin UI contexts add `hasRpc(method)`; app discovery refreshes on registry/plugin changes and reconnect. Unknown discovery on older hosts remains compatible.

The server (NetPI.Host, ASP.NET Core/Kestrel on `127.0.0.1`) serves the Svelte app from `wwwroot`,
plugin UI bundles from `/plugins/{pluginId}/…`, and a single WebSocket at `/ws`.

## Auth

The server generates a random token per run. The desktop shell opens `http://127.0.0.1:{port}/?token=…`;
the server sets an HttpOnly cookie `netpi_token` and redirects to `/`. All `/ws` and `/api/*` requests need the
cookie (or header `X-NetPI-Token`, or `?token=`). WebSocket connections with a foreign `Origin` are rejected.

## WebSocket envelope

Client → server

```jsonc
{ "t": "rpc", "id": 1, "m": "sessions.list", "p": { "limit": 50 } }
{ "t": "sub", "sessions": ["ses_…", "ses_…"] }   // replace the set of sessions whose scoped events you want ("*" = all)
{ "t": "ping" }
```

Server → client

```jsonc
{ "t": "hello", "clientId": "c_…", "version": "0.1.0" }
{ "t": "res", "id": 1, "r": <result> }
{ "t": "res", "id": 1, "e": { "code": "not_found", "message": "…" } }
{ "t": "ev", "type": "agent.status", "sid": null, "d": { … }, "seq": 42, "ts": 1790000000000 }
{ "t": "pong" }
```

Events with a non-null `sid` are **session scoped** and only delivered to clients that subscribed to that session.
All other events are broadcast.

HTTP fallback: `POST /api/rpc/{method}` with the params object as body → result JSON (`{ "error": {code,message} }` with 4xx on failure).

A single response that would not fit into the per-client WebSocket backlog (32 MiB) is not sent: it would trip the queue limit on the next message and cut off a reading client, and it would not fit into a client that is not. Such a request is answered with `e.code "too_large"` instead, and the client requests less (a shorter or earlier page). The host keeps normal pages below the limit (the byte budget of `sessions.messages`), so a response only becomes `too_large` when one message by itself is that big.

A method's parameters are read leniently where it is unambiguous: a number or boolean that arrives **quoted** (`"2"`,
`"true"`) is read as the value, because a model quotes a number it nests inside an object and a filter that is silently
ignored is worse than one that errors. Everything else is strict: an absent parameter is the method's default, a string
where a number belongs is not a number, and a number too large for an `int` is not one either.

## Data shapes (camelCase JSON)

```ts
type Role = 'user' | 'assistant' | 'tool' | 'notice' | 'summary';
interface ChatMessage {
  id: number; seq: number; sessionId: string; role: Role; parts: Part[]; createdAt: string;
  provider?: string; model?: string; stopReason?: 'stop'|'tool_use'|'length'|'aborted'|'error'|'content_filter';
  usage?: { inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number; reasoningTokens: number; costUsd?: number /* as the provider reported it (OpenRouter) */ };
  durationMs?: number; compacted: boolean;
  meta?: { kind?: string; agentId?: string; ttftMs?: number /* assistant: from the call's start to its first thinking, text or tool call */; [k: string]: any };   // notice kinds: project | instructions | tools | todo | goal | budget | profile | nudge | agent-message | agent-result | steer | retry | error | compaction
}
type Part =
  | { type: 'text'; text: string }
  | { type: 'thinking'; text: string; durationMs?: number; signature?: string; redacted?: string }
  | { type: 'tool_call'; id: string; name: string; arguments: string /* raw JSON */ }
  | { type: 'tool_result'; callId: string; name: string; content: string; isError: boolean; details?: any; durationMs?: number; images?: ImagePart[] }
  | { type: 'image'; mediaType: string; data: string /* base64 */ };

interface SessionInfo { id; title; projectId?; parentSessionId?; kind: 'chat'|'subagent'; model?; reasoning?; createdAt; updatedAt; archived; pinned; messageCount; contextTokens;
  meta?: { goal?: Goal; toolsOff?: string[] /* tools switched off for this chat */; profile?: string|null /* its profile */;
    identity?: string /* the opening of its system prompt, from the profile */; agent?: string /* the agent it runs on (agents.<id>) */;
    cwd?: string /* the folder it runs in: the workspace root when it is bound to one, else the project's */;
    workspaceId?: string /* the checkout it works in (WorkspaceInfo), when it is bound to one */;
    budgetAllowedFrom?: string; agentId?; parentAgentId?; [k: string]: any } }
interface ProjectInfo { id; name; path; createdAt; updatedAt; lastUsedAt?; meta?: { profile?: string /* default profile of new sessions, or "none" */; [k: string]: any } }
interface WorkspaceInfo { id; name; path /* the checkout root */; projectId?; kind: 'folder'|'worktree'|'attached'; branch?; baseCommit?; repoCommonDir?;
  ownerSessionId?; ownerAgentId?; managed: boolean /* NetPI created it, and may therefore retire it */; createdAt; updatedAt; meta? }
interface ModelInfo { provider; id; ref /* "provider/id" */; displayName?; contextWindow?; maxOutputTokens?; concurrency?;
  inputModalities: string[]; reasoning?: { supported: boolean; efforts: string[]; default?: string }; status?; isLocal: boolean }
type AgentStatus = 'idle'|'queued'|'running'|'yielded'|'completed'|'failed'|'cancelled';
interface AgentInfo { id; sessionId; name; parentAgentId?; parentSessionId?; isSubagent; depth; status: AgentStatus; model?; agent? /* the agent (agents.<id>) it runs on, or the model's slot key */;
  activity?; createdAt; startedAt?; finishedAt?; runs; turns; toolCalls; inputTokens; outputTokens; queuedMessages; task?; result?; error?; children: string[] }
interface QueuedInput { id; text; mode: 'steer'|'queue'; source; createdAt }
// an agent (configured: key = its id, capacity = its instances) or the slots of model calls without an agent (listed while busy)
interface AgentSlots { key; provider?; capacity; busy; queued; models: string[]; owners: SlotHolder[]; waiters: SlotHolder[]; source;
  status?: 'idle'|'busy'|'full'|'queued'|'disabled'|'unavailable';
  configured: boolean /* an agent set up in settings (agents.<id>) */; model?; use? /* the note on when to use it */;
  available: boolean /* can take work: not switched off, its model loaded (local) or reachable (cloud) */;
  unavailable?: string /* why not: "disabled", "qwen3.8-27b isn't loaded", … */; disabled: boolean;
  priceInput?; priceOutput? /* $ per Mtok */; priceSource?: 'settings'|'catalog'|'local'|'unknown'; free: boolean; spentTodayUsd?; dailyLimitUsd? }
interface SlotHolder { agentId; sessionId?; label?; since }
interface UiTabInfo { id; title; panel: 'left'|'right'; icon?; module; export?; order; pluginId; version }
interface SlashCommandInfo { name; description; rpc?; clientAction?; argsHint?; pluginId }
interface PluginInfo { id; name; description?; version?; directory; state; error?; loadedAt?; loadCount; loadMs; order; enabled }
// settings.schema: what the settings dialog renders as controls (the host's section first, then each plugin's)
interface SettingsSection { id; title; group: 'General'|'Models'|'Agents'|'Context'|'Tools'; order; help?; settings: SettingInfo[] }
interface SettingInfo { key /* dotted path */; type: 'bool'|'int'|'number'|'string'|'text'|'secret'|'choice'|'list'|'model'|'folder'|'file';
  label; help?; default?; placeholder?; min?; max?; unit?; options?: string[]; applies?: 'restart'|'new sessions'|string }
```

## Core RPC methods (host)

| method | params | result |
|---|---|---|
| `app.info` | – | `{ version, os, osDescription, home, appDir, defaultWorkspace, settingsFile, desktop, pid, dotnet, sqlite, pathSeparator, maxMessageBytes }` — `sqlite` is the SQLite library's version when the `sqlite` storage provider is the one in use (null for any other provider), and `maxMessageBytes` is the largest single WebSocket message the host will read; anything larger is closed with `1009 message too big`, so the UI fits its payloads into it (base64 images, for one) instead of being cut off |
| `projects.list` | – | `ProjectInfo[]` |
| `projects.create` | `{ name, path, create? }` | `ProjectInfo` |
| `projects.update` | `{ id, name?, path?, meta? }` | `ProjectInfo` (`meta` is merged key by key; a null value removes a key) |
| `projects.delete` | `{ id }` | `true` (its sessions are detached) |
| `sessions.list` | `{ projectId?, search?, includeSubagents?, parentSessionId?, includeArchived?, archivedOnly?, attachedKey?, attachedValue?, includeUnmaterialized?, limit?, offset? }` | `SessionInfo[]` (pinned first, then newest first) — `archivedOnly` returns archived sessions only (takes precedence over `includeArchived`), so an archived list pages over the archives alone instead of a newest-first window of active + archived. `attachedKey`/`attachedValue` keep the sessions whose `meta[key]` is that string (how a plugin finds the chats it attached something to), and `includeUnmaterialized` also matches message-less sessions, which are not stored |
| `sessions.create` | `{ title?, projectId?, model?, reasoning? }` | `SessionInfo` — until its first message the session is **transient**: nothing stored, not in `sessions.list`, no `session.created`, and the project's `last_used_at` is untouched (the creating client gets the `SessionInfo` as the RPC result). The first message materializes it |
| `sessions.fork` | `{ id, upToSeq? (the last) }` | `SessionInfo`: a new chat with the messages up to `upToSeq` (same seqs, times, parts and meta; compaction as it was at that point); the original is unchanged. It takes the setup (project, model, reasoning, meta such as the profile, `toolsOff`, the agent), not the run state (`goal`, `todo`, `budgetAllowedFrom`, `guardrailsAllowed`, subagent keys — each plugin declares its own fork-reset keys at start), and no workspace: a fork is a new writer, so it starts on the project's path (inheriting the original's checkout, or its `meta.cwd`, would put two writers in one tree), and gets `meta.forkedFrom { sessionId, title, seq }` and the title "Title (fork)", "Title (fork 2)"…. A subagent's chat gives `bad_request`. Publishes `session.created` (once the copy is complete), then `session.forked` — unless zero messages are copied, when the fork stays transient like a fresh `sessions.create` |
| `sessions.get` | `{ id }` | `SessionInfo` |
| `sessions.update` | `{ id, title?, model?, reasoning?, archived?, pinned?, meta? }` | `SessionInfo` (null clears model / reasoning; `meta` is merged key by key and a null value removes a key — the keys plugins keep there survive; a `meta` that is not an object is `bad_request`) |
| `sessions.delete` | `{ id }` | `true` (its subagent sessions are deleted too) |
| `sessions.setProject` | `{ id, projectId: string\|null }` | `SessionInfo`; publishes `session.project` (the context plugin appends a `project` notice) |
| `workspaces.list` | `{ projectId? }` | `WorkspaceInfo[]` newest first (all, without `projectId`) — registered by the Workspaces plugin |
| `workspaces.get` | `{ id }` | `WorkspaceInfo` (`not_found` when it does not exist) — registered by the Workspaces plugin |
| `sessions.setWorkspace` | `{ id, workspaceId: string\|null }` | `SessionInfo`: binds a session to a workspace (at most one), or unbinds it with null (it works in its project's path again); publishes `session.workspace` with the resolved binding. Registered by the Workspaces plugin, which writes `meta.workspaceId` and `meta.cwd`. Binding a workspace that does not exist is an error: a bound session never falls back to the project checkout, it gets a clear failure instead |
| `sessions.messages` | `{ id, beforeSeq?, limit? (default 60) }` | `{ messages: ChatMessage[], hasMore: boolean }` ascending by seq. The page is bounded in messages AND in serialized size (8 MiB): an image-heavy page comes back shorter, `hasMore` set, keeping the NEWEST messages and dropping the oldest — the client follows with `beforeSeq` at the page's oldest seq, the same call its load-earlier already makes |
| `sessions.stats` | – | `{ contextCache: { hits, reads } }` — the session service's context cache: `hits` is a read served from it (no store access), `reads` went to the store |
| `models.list` | `{ refresh? }` | `{ models: ModelInfo[], defaultModel: string\|null }` |
| `ui.tabs` | – | `UiTabInfo[]` |
| `ui.commands` | – | `SlashCommandInfo[]` |
| `ui.state.get` | `{ key }` | any JSON or null |
| `ui.state.set` | `{ key, value }` | `true` (a null value deletes the key) |
| `plugins.list` | – | `PluginInfo[]` |
| `plugins.reload` | `{ id }` | `true` |
| `plugins.setEnabled` | `{ id, enabled }` | `true` |
| `plugins.rescan` | – | `true` |
| `settings.get` | – | `{ path, settings: object }` |
| `settings.set` | `{ path, value }` | `true` (dotted path; a null value removes the key). Fails while `settings.json` does not parse: the broken file is kept, not replaced |
| `settings.replace` | `{ settings: object, base?: the document as loaded }` | `true`. With `base`, a document that changed since the load is a `conflict` (409) instead of a lost update — the raw editor sends the document it loaded; same refusal while the file does not parse |
| `settings.schema` | – | `SettingsSection[]`: the settings the host and the loaded plugins declare, for the settings dialog |
| `fs.dirs` | `{ path? }` | `{ path, parent, dirs: {name,path}[], roots: string[] }` (folder picker) |
| `tools.list` | – | `{ name, label, description, category, readOnly, pluginId, active, disabled, priority }[]` |
| `rpc.list` | – | `{ method, description, pluginId, readOnly }[]` — `readOnly` is the registration's own claim that the method only reads, and the read-only paths honour it: `node scripts/netpi.mjs` refuses the rest without `--write`, and the `diag` tool's `rpc` action calls only these. **Unmarked means "may write"** (idea-de1s7t). The host's own read-only methods: `app.info`, `projects.list`, `sessions.list`, `sessions.get`, `sessions.messages`, `sessions.stats`, `models.list`, `ui.tabs`, `ui.commands`, `ui.state.get`, `plugins.list`, `settings.schema`, `fs.dirs`, `tools.list`, `rpc.list`, `services.list`, `events.recent`, `events.flush`, `logs.recent`. `settings.get` is deliberately **not** among them: it returns the document as it is, API keys included, which is why `diag.settings` redacts. A plugin marks its own with `ctx.Rpc.RegisterReadOnly(method, handler, description)` (the `Register(..., readOnly: true)` overload underneath); the marked plugin methods are the `diag.*` views except `diag.reload`, `goal.get`, `ask.pending`, `agents.list`, `usage.summary`, `usage.session`, `budget.status`, `files.list`, `files.search`, `files.git`, `files.commits`, `files.scope`, `mcp.list`, `mcp.tools`, `workspaces.list`, `workspaces.get`, `workspaces.resolve`, `workspaces.git`, `workspaces.listForProject`, `workspaces.canRetire`, `ideas.work`, `ideas.capabilities`, `ideas.unread`, `ideas.list`, `ideas.get`, `ideas.review`, `ideas.suggestions`, `ideas.toPrompt` and `ideas.image`. Everything else a plugin registers is treated as writing, on purpose: a read surface nobody marked is one no tool can reach (idea-o934y1) |
| `services.list` | – | `{ type, implementation, priority, owner }[]` (registered services) |
| `events.recent` | `{ max? (200, up to 500) }` | `{ type, sid, d, seq, ts, source, ui }[]` recent bus events |
| `events.flush` | – | `true`, once every event published before the call has been delivered. Delivery is in publish order per subscriber and a connection's queue is in order too, so on a WebSocket every earlier event reaches the client **before** this answer: the way to know that an RPC's own events (`settings.set` returns before its `settings.changed` is delivered) are not still on their way. A subscriber's wedged handler no longer holds the bus — only its own line, and this wait: it times out after 10 s if any line cannot pass the marker, and the log says which one (see `docs/DEBUGGING.md`). Read-only |
| `logs.recent` | `{ max? (200, up to 2000) }` | `{ time, level, category, message, exception? }[]` |
| `desktop.capture` | `{ maxWidth? (1600) }` | `{ mediaType, data /* base64 PNG */, width, height }`: the window as shown; registered by the desktop app only (the `screenshot` tool uses it) |
| `desktop.zoom` | `{ factor? (0.5–3) }` | `{ factor }`: the window's zoom, set when `factor` is given; remembered in `window.json`; desktop app only |

## Plugin RPC methods

| method | plugin | params → result |
|---|---|---|
| `agent.send` | netpi.runtime | `{ sessionId, text, images?: {mediaType,data}[], mode?: 'auto'\|'steer'\|'queue' }` → `AgentInfo` |
| `agent.abort` | netpi.runtime | `{ sessionId }` → `bool` |
| `agent.queue` | netpi.runtime | `{ sessionId }` → `QueuedInput[]` |
| `agent.dequeue` | netpi.runtime | `{ sessionId, id }` → `bool`: removes one of the person's queued inputs (`source: "user"`); an internal one (a subagent's report, a harness notice) is refused with `forbidden` |
| `runs.list` | netpi.runtime | `{ includeFinished? }` → `AgentInfo[]` |
| `agent.get` | netpi.runtime | `{ id? , sessionId? }` → `AgentInfo\|null` |
| `profiles.list` | netpi.profiles | → `{ defaultProfile, profiles: { id, name, prompt, toolsOff }[] }` |
| `profiles.apply` | netpi.profiles | `{ sessionId, profile: string\|null }` → `SessionInfo`: sets the chat's `meta.profile`, `meta.identity` and `meta.toolsOff` from the profile; in a started chat the prompt revision is invalidated (`meta.promptRevision`), so the next model call renders the system prompt again (one full re-read, without a `context.reset` call) and a `profile` notice is appended. Chats only |
| `context.reset` | netpi.context | `{ sessionId }` → `true`: forget the session's frozen system prompt and tool baseline; the next model call renders them again |
| `agent.tools` | netpi.runtime | `{ sessionId }` → `{ sessionId, started, contextTokens, off: string[], tools: { name, label, category, description, readOnly, pluginId, on }[] }`: the tools the session's agent can have, each with its switch |
| `agent.setTools` | netpi.runtime | `{ sessionId, off?: string[], on?: string[] }` → like `agent.tools`: switches tools off (or back on) for one session (`meta.toolsOff`); a started chat gets the change at its next model call, with a `tools` notice (the model re-reads the conversation once); subagents start with their parent's list |
| `agents.list` | netpi.agents | → `AgentSlots[]`: the agents (always, with `available`/`unavailable`/`disabled`, `priceInput`/`priceOutput` ($ per Mtok), `priceSource`, `free`, `spentTodayUsd`, `dailyLimitUsd`), then model calls without an agent while they run |
| `agents.use` | netpi.agents | `{ sessionId, agent: string|null }` → `SessionInfo`: the chat prefers the agent (`meta.agent`, where it last ran: a run goes to the first agent on that model with a free instance) and takes its model; `"any"`: every run goes to whichever agent has a free instance, and the chat's model follows it; `null` clears it (the chat then takes an agent on its model at its next run) |
| `agents.unassigned` | netpi.agents | → `SlotHolder[]`: runs waiting for any of several agents, in nobody's queue yet (`agentId` is the run, `waitingFor` "a free agent") |
| `agents.setEnabled` | netpi.agents | `{ id, enabled }` → `AgentSlots[]`: switch an agent off (`agents.<id>.disabled`; runs on it finish, new ones stop with a notice) or back on |
| `usage.summary` | netpi.agents | → `{ day, providers: { provider, inputTokens, outputTokens, cacheReadTokens, calls, budgetTokens? }[] /* today */, budget: BudgetStatus, models: { agent, provider, model, calls, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd, unknownCost }[] /* this period */ }` |
| `usage.history` | netpi.agents | `{ period? (the day a budget period starts, "yyyy-MM-dd"; default: the current one), days? (30) }` → `{ period, current, end, resetDay, totals, allTime: { …, since }, periods: { period, end, calls, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd, unknownCalls }[] /* newest first, up to 36 */, agents: { agent|null, …same figures }[], models: { provider, model, …same figures }[] /* the chosen period, by cost */, days: { day, costUsd, calls, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens }[] /* oldest first, a quiet day is zeros */, budget: BudgetStatus }`: the usage tab (read-only). Periods, agents and models are read from the `period_usage` roll-up, the days from the day indexes of the calls and the daily token roll-up, so it costs a few indexed reads whatever the call count; `not_found` for a period with no usage |
| `usage.chats` | netpi.agents | `{ period?, limit? (10, at most 50) }` → `{ period, chats: { sessionId, title, project?, deleted, calls, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd, unknownCalls }[], chatCount, noChat: {…}, totals: {…}, truncated }`: the chats that cost most in a period (read-only; a subagent's calls count for the chat that started it). The one usage read that goes through the period's calls (at most 200 000), so the tab asks for it on its own |
| `usage.session` | netpi.agents | `{ sessionId }` → `{ sessionId, costUsd, calls, withSubagentsUsd, withSubagentsCalls }` |
| `budget.status` | netpi.agents | → `BudgetStatus`: `{ monthlyUsd, dailyUsd, warnPercent, resetDay, onLimit, periodStart, periodEnd, spentUsd, todayUsd, reservedOrUnsettledUsd, interruptedEstimateUsd, interruptedEstimateCalls, unknownCostCalls, warning, exhausted }` |
| `budget.allow` | netpi.agents | `{ sessionId }` → `BudgetStatus`: the chat may go over the budget until the period ends, and continues (`budget.onLimit: "ask"`) |
| `agentsmd.list` | netpi.agentsmd | `{ sessionId }` or `{ projectId }` → `{ path, bytes, scope }[]` (instruction files for the session's working directory or the project folder; scope `global`, `project` or `extra`) |
| `skills.list` | netpi.skills | `{ sessionId }` or `{ projectId }` → `{ skills: [{ name, description, path, scope, listed, userOnly, disabled, license?, compatibility?, allowedTools? }], problems: [{ path, level, message }] }` (the skills for the session's working directory or the project folder, in precedence order; scope `project`, `extra` or `global`; level `warning` or `error`; see [PLUGIN-SKILLS.md](PLUGIN-SKILLS.md)) |
| `compaction.run` | netpi.compaction | `{ sessionId, args? /* extra focus for the summary */ }` → `string` (error `busy` while the agent runs) |
| `context.preview` | netpi.context | `{ sessionId }` → `{ systemPrompt, frozen, tools: {name, description, chars, schemaChars}[], estimatedTokens }` (`frozen`: the prompt stored at the session's first model call; `chars`: what a tool costs in every request, its description and schema; `schemaChars`: the schema alone) |
| `context.prompts` | netpi.context | `{ sessionId }` → `{ prompts: [{ version, afterSeq, createdAt, systemPrompt, tools: [{ name, description, parameters }] }] }`: every system prompt the session was sent, oldest first, with the tool definitions sent with it: the first (at the first model call, `afterSeq` = the last message then) and one after each `context.reset`. A session frozen before these were kept has version 1 only, with its tool names. The chat shows them as "System prompt" rows |
| `context.toolsets` | netpi.context | `{ sessionId }` → `{ sessionId, tools, baseline: { tools, sinceSeq }\|null, changes: [{ seq, time, added, removed, cause, plugins, text }], truncated, reloads: [{ ids, time, kind }] }`: the session's tools now, the baseline of its first model call (null before it), and every tool-set change it was told about, oldest first, with the cause worked out for the notice (`plugin-reload` + the plugin ids, `profile`, `user`, `settings`, `unknown`) and the notice text. The whole chat is walked back page by page, so a change older than the newest 500 messages is still there; `truncated` says the walk hit its 10 000-message backstop. `reloads` are the `plugins.reloaded` events of the last 10 minutes. `diag.toolsets` returns the same |
| `goal.get` | netpi.goal | `{ sessionId }` → `Goal\|null` (`meta.goal` unless cleared: `{ id, objective, status: active\|paused\|blocked\|complete, reason, tokenBudget, tokensUsed, continuations, noProgress, version, createdAt, updatedAt }`) |
| `goal.set` | netpi.goal | `{ sessionId, objective (≤ 4000), tokenBudget? }` → `Goal` (a new goal; starts a run with a "goal" notice when the agent is idle; names an untitled session) |
| `goal.edit` | netpi.goal | `{ sessionId, objective?, tokenBudget? }` → `Goal` (same goal; the model hears about it at its next call) |
| `goal.pause` / `goal.resume` / `goal.clear` | netpi.goal | `{ sessionId }` → `Goal` (`clear` → `null`). Pause lets the current run finish; resume resets the counters and starts a run when idle (also for an active goal whose agent is idle) |
| `ask.pending` | netpi.ask | `{ sessionId? }` → `{ id, sessionId, callId, agentId, agentName, questions: { question, options: { label, description? }[], multiple }[], askedAt }[]`: the questions waiting for the user (`ask_user`). `id` is the question's own (`ask_…`, like a guard `approvalId`); `callId` is the model's tool call, which two chats can share |
| `ask.answer` | netpi.ask | `{ id, answers?: string[][], text? }` → `true`: the options picked per question and/or the user's own words; `not_found` when nothing waits under that id, `bad_request` for an empty answer. A caller that only knows the tool call may send `callId` + `sessionId` instead |
| `plan.enter` | netpi.plan | `{ sessionId }` → `{ mode }`: puts a chat in plan mode (`meta.planMode.state: planning`); a subagent's chat is refused |
| `plan.exit` | netpi.plan | `{ sessionId }` → `bool` (false: not in plan mode): leaves it; the plan being decided is cancelled |
| `plan.get` | netpi.plan | `{ planId }` or `{ sessionId }` (its current, else latest plan) → `{ id, sessionId, projectId?, status: awaiting\|revising\|approved\|cancelled, revision, title, plan: { title, summary, steps: { text, detail? }[], files: { path, note? }[], risks, tests, openQuestions }, markdown, revisions: { n, at, plan, feedback? }[], callId?, ideaId?, filePath?, newSessionId?, createdAt, updatedAt }` or `null` (read-only) |
| `plan.list` | netpi.plan | `{ sessionId?, status? }` → plans as `plan.get` returns them without the revision bodies, plus `waiting` (a run waits for the decision); without a chat, the ones awaiting or being revised (read-only) |
| `plan.answer` | netpi.plan | `{ planId, decision: approve\|revise\|save\|file\|cancel, feedback?, newChat? }` → `{ planId, status, ideaId?, newSessionId?, path?, todos? }`. `feedback` is required to revise and a note on approve; `newChat` (approve) carries the plan out in a new chat that replaces this one. `bad_request` when the plan is not awaiting (or has no feedback); the idea that could not be saved is an error and the plan stays awaiting |
| `plan.offers` | netpi.plan | `{ sessionId? }` → `{ id, sessionId, callId, agentId, reason, askedAt }[]`: the offers of plan mode (`plan_enter`) that wait (read-only) |
| `plan.enterAnswer` | netpi.plan | `{ id, enter }` → `true`: enters plan mode (or not); `not_found` when nothing waits under that id |
| `plan.command` | netpi.plan | `{ sessionId, args }` → status text: the `/plan` command (`/plan`, `/plan <task>` starts a run on the task, `off`, `show`) |
| `guard.pending` | netpi.guardrails | `{ sessionId? }` → `{ approvalId, sessionId, callId, agentId, tool, kind: 'command'\|'path'\|'sleep', subject, rule, askedAt, opinion? }[]`: tool calls waiting for the user's OK (a guardrails `ask:` rule, or a wait over `guardrails.maxSleepSeconds`); `opinion` is the second opinion (`guardrails.secondOpinion`) when one was asked: `{ model, harmless, p: { destructive, stops_process, remote_change, read_only }, ms, error? }` |
| `guard.answer` | netpi.guardrails | `{ approvalId, allow, scope?: 'once'\|'session' }` → `true`: the call runs, or is blocked ("the user said no"); `not_found` when nothing waits under that id. `scope: 'session'` with `allow` also allows the rule that asked for the rest of that chat (session meta `guardrailsAllowed`, a list of rules) and the other calls of that chat waiting on the same rule; a refusal is never remembered |
| `files.search` | netpi.tools.files | `{ sessionId?, query, limit? }` → `{ path, rel, isDir }[]` (for `@` mentions) |
| `files.open` | netpi.tools.files | `{ path, sessionId?, cwd?, confirm? }` → `{ path, action: 'open'\|'edit'\|'reveal'\|'folder'\|'confirm' }` (opens a path with the operating system; `confirm` means the path is outside the session's workspace and nothing was opened — ask the user and call again with `confirm: true`; see `docs/TOOLS.md`) |
| `files.list` | netpi.tools.files | `{ sessionId?, dir? }` → `{ root, dir, entries: {name, rel, isDir, size?, mtime?}[] }` |
| `files.git` | netpi.tools.files | `{ sessionId?, cwd? }` → `{ repo, root, workspaceId?, branch, ahead, behind, files: { path, rel, status, added?, deleted? }[], added, deleted }\|null`: the uncommitted changes since the last commit, in the session's **workspace root** (its project folder when it is not bound to one; a bound workspace that cannot be used is an error, `workspace_unavailable`, never the project folder) |
| `files.scope` | netpi.tools.files | `{ sessionId?, cwd? }` → `{ sessionId, root, workspaceId?, branch?, isolated, identity?, version }`: what the Files tab is showing. `identity` is the session's workspace id and version (or its project when unbound) and is what a UI keys its refreshes on, so an answer computed for the previous root is recognizably stale |
| `files.commits` | netpi.tools.files | `{ sessionId?, cwd?, gitDir?, commonDir?, since?, until?, limit? (20, max 200) }` → `{ repo, gitDir, commonDir, reachable, commits: { hash, short, subject, author, at }[] }\|null`, newest first. `since`/`until` are hashes: only what came after / before it (both absent = the newest `limit`). `gitDir`/`commonDir` are the directories that hold this worktree's HEAD and the refs — a `.git` *file* (a worktree) is not a directory to watch; a caller that already has them from an earlier answer passes them back, and only the `git log` runs instead of re-resolving them. `reachable: false` means the range could not be resolved, i.e. `since` is not in this history any more (a rebase, a branch switch), so a caller that remembers a cursor can re-anchor instead of waiting forever. `null` only means "not a git repository". The idea check reads a project's history through this, because a plugin cannot run `git` itself |
| `decide.ask` | netpi.decide | `{ state: string\|object, questions /* the decide tool's shape or TypeSafe's */, model? }` → the model's `answers` (TypeSafe shape: `{ [id]: { type, choice?, score?, confidence?, probabilities?, noul? } }`); errors keep the server's code (`model_not_loaded`, `unreachable`, …) |
| `processes.list` | netpi.tools.shell | → `ProcessInfo[]` |
| `processes.output` | netpi.tools.shell | `{ id, tail? }` → `string` |
| `processes.kill` | netpi.tools.shell | `{ id }` → `bool` |
| `decide.decision` | netpi.decide | `{ messages, branches: [{ id?, content, labels }], model?, share_state? }` → NInfer's `/v1/decision` answer through the same server (`{ branches: [{ id, probabilities, mass, … }], usage, … }` plus `ms`); thinking off unless the caller sets it. The `messages` are the shared state NInfer caches across requests. Errors keep the server's code and request id |
| `ideas.list` `ideas.get` `ideas.review` `ideas.add` `ideas.update` `ideas.delete` `ideas.reorder` `ideas.toPrompt` `ideas.quickAdd` `ideas.recall` `ideas.attach` `ideas.closed` `ideas.suggestions` `ideas.resolve` `ideas.export` `ideas.import` | netpi.ideas | the backlog lives in the store's own plugin collections; every idea carries a `revision`, and `ideas.list` describes where: see `docs/PLUGIN-IDEAS.md` |
| `work.snapshot` | netpi.work | → `{ agents, runs, processes, usage, time, errors? }` (each part `null` when unavailable; see `docs/PLUGIN-WORK.md`) |
| `workspaces.resolve` | netpi.workspaces | `{ sessionId }` → `{ workspaceId, root, branch, baseCommit, kind, isolated, managed, ownerSessionId, ownerAgentId, version, projectId, projectPath, identity, available, error? }` — what a session's workspace resolves to: `root` is the directory its relative paths resolve against (the project's path when unbound), `identity` keys a UI's refresh, and a binding whose workspace is gone gives `available: false` with `error` (rebind it, or unbind) instead of a fall back |
| `workspaces.git` | netpi.workspaces | `{ sessionId?, cwd? }` → `{ root, topLevel?, commonDir?, branch?, head?, changes, isRepository }` — the git evidence for the root a session works in (an explicit `cwd`, else its workspace root, else the default workspace); `commonDir` is `--git-common-dir`, which a worktree reports as its main checkout's, so a worktree and the primary checkout read as the same repository |
| `workspaces.listForProject` | netpi.workspaces | `{ projectId }` → `WorkspaceInfo[]` — the workspaces of a project, with their branches and owners |
| `workspaces.create` | netpi.workspaces | `{ projectId, name, ownerSessionId?, ownerAgentId?, isolated?, base? }` → `WorkspaceInfo` (else `workspace_failed`) — a git project gets a worktree and a branch (from `base`, a branch or commit; default the project's HEAD, so the parent checkout's uncommitted changes are deliberately not swept in), a non-git project a plain folder; `isolated: false` records the project's own checkout as a shared, unmanaged workspace |
| `workspaces.attach` | netpi.workspaces | `{ path, projectId?, name?, ownerSessionId? }` → `WorkspaceInfo` — an existing checkout, recorded without creating anything; a checkout of another repository than the project's is refused |
| `workspaces.delete` | netpi.workspaces | `{ id }` → `{ id, removed }` — retires a workspace only when NetPI created it, its work is merged or pushed, nothing is bound to it and nothing is running in it; an attached, dirty or unmerged checkout is refused (`workspace_busy`), never deleted |
| `workspaces.integrate` | netpi.workspaces | `{ id, into? }` → `{ id, branch, merged, verified }` — merges the worktree's actual branch into the project's branch (default: the branch the project's checkout is on), serialized per repository (a second integrator waits and re-reads the branch); a merge that does not go cleanly is aborted and left to resolve by hand. `branch` is resolved from git at integrate time and checked against the record — a worktree that has switched branches since the record was written is refused with both branches named (`integration_failed`), so a no-op merge can never be reported as merged and verified. `merged`/`verified` come from an ancestry check of that branch after the merge, not from git's exit code |
| `workspaces.canRetire` | netpi.workspaces | `{ id }` → `{ id, canRetire, reason? }` — whether a workspace's checkout may be removed, and why not (attached, still bound, a running process in it, uncommitted changes, unmerged commits) |
| `diag.overview` | netpi.diagnostics | → `{ time, app, process, storage, plugins, models, agents, runs, calls: { running, last15m }, tools, processes, problems, more }`: start here (see `docs/DEBUGGING.md`) |
| `diag.problems` | netpi.diagnostics | → `{ severity: 'error'|'warn'|'info', area, message, hint? }[]`, worst first |
| `diag.calls` | netpi.diagnostics | `{ limit?, sessionId?, runId?, agent?, errors?, running?, detail? }` → `{ id, startedAt, state: running|ok|error|cancelled|incomplete, model, purpose, agent, sessionId, runId, firstTokenMs, durationMs, attempts, inputTokens, cacheReadTokens, outputTokens, stopReason, error }[]` (newest first) |
| `diag.call` | netpi.diagnostics | `{ id }` → the `diag.calls` fields plus `reasoningEffort, request: { messages, tools, systemPromptChars, inputChars, lastUser }, response: { textChars, thinkingChars, toolCalls, cacheWriteTokens, reasoningTokens, costUsd }, resets, notices, errorDetail?: { type, status, transient, contextOverflow }` |
| `diag.tools` | netpi.diagnostics | `{ limit?, sessionId?, name?, errors?, running? }` → `{ callId, name, sessionId, runId, startedAt, state, durationMs, arguments, result? /* preview of the text the model got */ }[]` |
| `diag.tool` | netpi.diagnostics | `{ callId, sessionId? }` → `{ callId, name, sessionId, state, durationMs, arguments /* parsed */, isError, result /* the full text the model got */, images, details }` (from the chat's messages; `not_found` when neither the tool log nor the last 2000 messages have it) |
| `diag.journal` | netpi.diagnostics | `{ limit?, type?, sessionId?, sinceSeq? }` → `{ seq, time, type, sessionId, source, data }[]` oldest first, no per-token events, `data` summed up |
| `diag.run` | netpi.diagnostics | `{ sessionId | agentId }` → `{ run, statusSince, session, slot, queue, children, calls, tools, journal, messages }` |
| `diag.settings` | netpi.diagnostics | → `{ file, settings }` without secrets |
| `diag.logs` | netpi.diagnostics | `{ limit?, level?, category?, contains?, sinceMinutes? }` → log entries, oldest first |
| `diag.failures` | netpi.diagnostics | `{ limit? }` → the saved failed requests: `{ name, time, bytes, provider, model, sessionId, transport, requestId, responseId, error }[]` |
| `diag.failure` | netpi.diagnostics | `{ name, offset?, limit? /* or maxChars */, summary? }` → `{ name, bytes, offset, returned, truncated, nextOffset?, content, shape? }`. Without `offset` it is the first `maxChars` characters, as before; with it a slice, so no body is out of reach (`nextOffset` pages on). `summary: true` returns `shape` instead of the text: the ordered `input[]`/`messages[]` kinds by index with a count per kind, no payload — enough to answer a gateway's "item 176 …" from one small result. It fills `content` only when a slice was asked for |
| `diag.toolsets` | netpi.diagnostics | `{ sessionId }` → the context plugin's `context.toolsets` (needs it: it is what keeps the record); error `unavailable` without it |
| `diag.snapshot` | netpi.diagnostics | `{ events? }` → `{ plugins, tools, rpc, events, logs, runtime, time }` (see `docs/PLUGIN-DIAGNOSTICS.md`) |
| `diag.event` | netpi.diagnostics | `{ seq }` → `{ seq, type, sessionId?, time, source?, ui, data }` |
| `diag.reload` | netpi.diagnostics | `{ args?: pluginId }` → `string` (`/reload`; no id = all plugins) |

```ts
interface ProcessInfo { id; pid; shell: 'bash'|'pwsh'; command; cwd; sessionId?; agentId?; background: boolean;
  status: 'running'|'exited'|'killed'|'timeout'; exitCode?; startedAt; endedAt?; outputBytes }
```

## Events

| type | scoped | data |
|---|---|---|
| `session.created` / `session.updated` | no | `{ session: SessionInfo }` – `session.created` goes out when a session gets its **first message** (materialization: `session.created` → `message.added` → `session.updated`), not for `sessions.create` of an empty chat |
| `session.deleted` | no | `{ id }` |
| `session.forked` | no | `{ sessionId /* the fork */, fromSessionId, upToSeq }` – after `sessions.fork`: the context plugin gives the fork the system prompt the original was sent at that point (so its next call starts with the prefix the backend saw), the todo plugin the checklist of its last `todo_write` |
| `session.project` | no | `{ sessionId, projectId, cwd }` – attached to another project or detached |
| `session.changed` | no | `{ sessionId, keys: string[] }` – the session's **meta** changed: the keys whose value is not what it was (added, changed, removed, sorted). A rewrite with the same value, and a field outside `meta` (title, model, project…), fire nothing. The key names are contract, not the plugin that writes them: `profile` (`SessionProfile.MetaKey`, the profiles plugin), `toolsOff` (`SessionTools.MetaKey`, `agent.setTools`), `identity` (`SessionIdentity.MetaKey`, a profile's prompt), `workspaceId` and `cwd` (`SessionWorkspace.MetaKey`, `SessionCwd.MetaKey`, the Workspaces plugin). It is the real signal for "the user switched this chat's profile", so a plugin can react to the change instead of looking for a notice kind or for message order (idea-m7vmue) |
| `session.workspace` | no | `{ sessionId, workspaceId, cwd, binding }` – a session was bound to a workspace, or unbound (`workspaceId` and `binding` null), and its `meta.workspaceId`/`meta.cwd` were written with it. Published by the Workspaces plugin. `binding` is the resolved `WorkspaceBinding`, so a consumer does not resolve it again and cannot end up with a different root than the switch announced |
| `project.created` / `project.updated` | no | `{ project }` |
| `project.deleted` | no | `{ id }` |
| `message.added` / `message.updated` | yes | `{ sessionId, message: ChatMessage }` |
| `messages.compacted` | yes | `{ sessionId, upToSeq }` |
| `stream.start` | yes | `{ sessionId, agentId, model }` – an assistant message starts streaming |
| `stream.delta` | yes | `{ sessionId, kind: 'text'\|'thinking', text }` – append (coalesced ~30ms) |
| `stream.tool` | yes | `{ sessionId, callId, name }` – model started emitting a tool call |
| `stream.reset` | yes | `{ sessionId, reason }` – discard the in-progress stream (retry) |
| `stream.end` | yes | `{ sessionId }` – followed by `message.added` with the final assistant message |
| `tool.start` | yes | `{ sessionId, agentId, callId, name, label, arguments }` |
| `tool.output` | yes | `{ sessionId, callId, chunk }` – live output (shell) |
| `tool.end` | yes | `{ sessionId, callId, name, isError, durationMs }` – result arrives via `message.added` (role tool) |
| `agent.status` | no | `{ agent: AgentInfo }` |
| `ask.asked` | no | `{ id, sessionId, callId, agentId, agentName, questions, askedAt }` – a question waits for the user (`ask_user`); unscoped, so every window hears of it |
| `plan.changed` | no | `{ sessionId, mode?: planning\|awaiting\|approved\|off, planId?, callId?, status?, revision?, title?, submitted }` – a chat's plan mode or plan changed (`submitted`: a `plan_submit` just stored it, the user is wanted); unscoped |
| `plan.enter.asked` | no | `{ id, sessionId, callId, agentId, reason, askedAt }` – the agent offers plan mode (`plan_enter`); unscoped |
| `plan.enter.closed` | no | `{ id, sessionId, callId, status: entered\|declined\|steered\|withdrawn\|cancelled }` – the offer stopped waiting |
| `guard.asked` | no | `{ approvalId, sessionId, callId, agentId, tool, kind, subject, rule, askedAt, opinion? }` – a tool call waits for the user's OK (guardrails); unscoped |
| `guard.cleared` | no | `{ sessionId, callId, agentId, tool, kind, subject, rule, by: 'opinion'\|'session', opinion? }` – an `ask:` rule matched and the call runs without asking: the second opinion found the command confidently read-only (`by: 'opinion'`, with `opinion`), or the user allowed that rule for this chat (`by: 'session'`); unscoped |
| `guard.closed` | no | `{ approvalId, sessionId, callId, status: 'allowed'\|'denied'\|'steered'\|'cancelled', scope?: 'session' }` – it stopped waiting (`scope` when it was allowed for the rest of the chat) |
| `ask.closed` | no | `{ id, sessionId, callId, status: 'answered'\|'steered'\|'withdrawn'\|'cancelled', answers: string[][]\|null, text }` – it stopped waiting |
| `agent.queue` | yes | `{ sessionId, items: QueuedInput[] }` |
| `agent.notice` | yes | `{ sessionId, level: 'info'\|'warn'\|'error', text, kind?, phase?, mode? }` – transient (retry countdown etc.); a compaction's carry `kind: 'compaction'`, `phase: 'start'\|'done'\|'failed'` and `mode: 'auto'\|'overflow'\|'manual'` |
| `context.prompt` | yes | `{ sessionId, version, afterSeq }` – the session was sent a new system prompt (its first model call, or after `context.reset`); `context.prompts` has it |
| `session.context` | no | `{ sessionId, used, window }` |
| `agents.changed` | no | `{ agents: AgentSlots[], resources, unassigned: SlotHolder[] }` – what `agents.list` and `agents.unassigned` return, whenever a run takes or frees an instance, joins or leaves the unassigned queue, or an agent's state changes |
| `models.changed`, `plugins.changed`, `ui.changed` | no | `{}` |
| `settings.changed` | no | `{ path?: string, source?: "file" }`; a whole-file reload/replacement has no path |
| `plugins.reloaded` | no | `{ ids: string[], kind: 'reload'\|'reload-failed'\|'deferred'\|'enabled'\|'disabled'\|'removed' }` – which plugins the host reloaded, enabled, disabled or found gone (every `/reload`, hot reload from the build, a `plugins.disabled` change, and a rescan that found a folder or assembly missing). A reload is a **swap**: the new version starts while the old one still serves, so its tools are never absent and a chat gets no "tools changed" notice; `reload-failed` means the new version did not start and the running one was kept; `deferred` means `plugins.quiet` is on and nothing was applied; `removed` means the plugin's folder or assembly is gone, so its tools are gone with it. What changed, not just that something did: the context plugin names it in the next "tools" notice, the diagnostics plugin lists it in `diag.overview`/`diag.problems`, and `IPluginManager.Deferred()` (and `diag.overview`'s `deferred`) says what is waiting |
| `usage.recorded` | no | `{ provider, model, usage }` (agent turns) |
| `usage.changed` | no | `BudgetStatus`, after model calls were recorded (debounced), or immediately after a `budget` / `budget.*` setting change or whole-file settings reload/replacement |
| `process.started` / `process.exited` | no | `{ process: ProcessInfo }` |
| `ideas.changed` | no | `{ backend: 'sqlite', database, scope, schemaVersion, file, reason? }` – after every write that committed (never one that rolled back), so every window re-reads canonical state; `file` is the legacy name, `reason` what wrote it. A notification, not exactly-once delivery |
| `ideas.suggested` | no | `{ suggestion }` – a card a closed chat (or a commit sweep) left waiting for the user |
| `ideas.resolved` | no | `{ id, action?, card? }` – a card was answered, discarded, or finished. A window that still shows the card drops it; both the composer and the Ideas tab re-read the cards with `ideas.suggestions` on reconnect and when they become visible |

## Plugin UI tabs

A plugin registers a tab (`context.Ui.AddTab(...)`) whose `module` is an ES module in the plugin's `wwwroot`
(served at `/plugins/{pluginId}/{module}?v={version}`). The module exports:

```js
export function mount(el, ctx) {
  // render into el …
  return { unmount() {}, onShow() {}, onHide() {} };   // all optional
}
```

`ctx`:

```ts
{
  pluginId: string, tabId: string,
  rpc(method: string, params?: object): Promise<any>,
  on(pattern: string, handler: (data, evt) => void): () => void,   // bus events; 'agent.*' and '*' patterns
  app: {
    readonly activeSessionId: string | null,
    readonly activeSession: SessionInfo | null,
    readonly activeProject: ProjectInfo | null,
    onChange(cb: () => void): () => void,        // active session/project changed
    openSession(id: string): void,
    newSession(opts?: { projectId?: string }): Promise<void>,
    insertText(text: string): void,              // into the composer
    openTab(tabKey: string): void,               // "pluginId/tabId"
    openSettings(page?: string, target?: string): void, // the settings dialog, on a page ("agents", "profiles", …);
    // a target names an item on the page: "agents" + an agent id opens that agent's dialog
    toast(text: string, level?: 'info'|'warn'|'error'): void,
  }
}
```

Plugin UIs render inside the host DOM and should style themselves with the host CSS variables
(`--bg`, `--bg-1`, `--bg-2`, `--bg-3`, `--border`, `--fg`, `--fg-muted`, `--fg-dim`, `--accent`, `--ok`, `--warn`, `--err`,
`--font-ui`, `--font-mono`, `--radius`) and the `np-*` utility classes documented in `web/src/styles/kit.css`.
The mount element (`.plugin-root`) is a flex column at least as tall as the panel, and the panel scrolls it. To fill the
height (for a footer at the bottom), give the tab's root `flex: 1 0 auto`; `min-height: 100%` does not resolve there.

A bundle built by `build-plugins` shares the host UI's Svelte and its `@netpi/kit` (both are shims over
`globalThis.__netpiHost.svelte` / `.kit`, set in the host's `main.js`), so it carries no runtime of its own and its
components are the host's. Consequences: a bundle is pinned to the svelte it was built against and refuses to load
against a host running another one, and a svelte bump means rebuilding the UI and every plugin tab. Both failures
throw a message naming the cause, which the tab shows with a Retry button. A tab that does not use Svelte at all, or
one that bundles its own runtime, is unaffected.

## Backups (netpi.backup)

| RPC | Parameters | Result |
|---|---|---|
| `backup.list` | `{}` | Snapshot manifests with `id`, `path`, `version`, `createdAt`, `automatic`, `files` (SHA-256 per file) and `ideaImages` (file of the snapshot → `idea-images/<name>` in the home, when the snapshot carries images). Listing does not rehash every snapshot. |
| `backup.create` | `{}` | Creates and verifies a manual snapshot; returns its manifest and path. Allow a long RPC timeout for large databases. |
| `backup.verify` | `{ id }` | Checks the manifest version and both file hashes; returns the manifest or an error. |

Restore is offline through
`scripts/restore-backup.mjs`, into a new home only. See [BACKUPS.md](BACKUPS.md).

Approval compatibility: `guard.answer` now requires the unique `approvalId` from `guard.pending` or `guard.asked`.
A provider's `callId` remains display/correlation data, never an authorization identifier. Old clients must refresh
and fetch pending approvals; callId-only answers fail validation. `guard.closed` includes the approvalId it closes.

Budget amounts include persistent reservations. `reservedOrUnsettledUsd` identifies the active/crash-left portion;
`interruptedEstimateUsd` / `interruptedEstimateCalls` identify calls that were interrupted before the provider reported
the final bill (settled from the partial usage, or estimated from what was sent and streamed) — never from the full
reservation; `unknownCostCalls` counts unpriced calls. A failure or stop before the first byte settles at $0
(`cost_source 'rejected'`), so a storm of 503/529s cannot lock a budget. Every retry attempt gets its own ledger row,
and each generation of the ledger meters a call exactly once (a hot swap keeps two generations in the pipeline
briefly; the second sees the first's mark and passes through). Amounts are attributed to the day the attempt started.

Reservations are a gate, not a bill: the input is counted in tokens (not serialised bytes) at the cache-read rate —
in an agent loop the context is re-read, and a call that misses the cache is corrected by the settlement — and the
output is the effective maximum the provider accepts. The ledger, the caps and the reservations live in the Agents
plugin; the core's model catalog refuses no model at all, so while that plugin is absent or failing paid calls are
neither metered nor limited (a storage failure refuses them instead: the ledger has no in-memory fallback).

## MCP plugin

Read-only `mcp.list {}` returns `{servers:[{id,status,error,generation,toolCount,resourceCount,version,config}]}`; credentials remain environment references. `mcp.tools {serverId?}` returns `{tools:[{id,serverId,name,description,revision,deferred,readOnly,schema}]}`; `readOnly` is the server's own `annotations.readOnlyHint: true` or its `readOnly` list in the configuration. `mcp.tool {id}` returns one tool by its registered id: `{id,serverId,name,readOnly,annotations}` (plan mode matches `plan.mcpAllow` against the real name). `mcp.resources {serverId?}` returns `{resources:[{kind,uri,server,name,description,mimeType,available}]}` — what `mcp_search` can surface, including entries hidden from the model by a server's `resources` allow-list.

Mutations: `mcp.save {id,config}`, `mcp.remove {id}`, `mcp.setEnabled {id,enabled}`, `mcp.reconnect {id}`, `mcp.refresh {id}`. Each returns the status snapshot. Invalid configuration is rejected before saving.

`mcp.toolsChanged` publishes `{serverId,reason,added,removed,updated}` (stable registered ids). Disclosed schema changes produce targeted tools notices with `updated`, `revisions` and cause `remote-server`; `context.toolsets` retains them. `agent.tools` adds a `deferred` flag per concrete entry. `tool.start` and `tool.end` for indirect calls add `resolvedTool` and `serverId`, preserving the original call id and gateway name.

`mcp.serverChanged` publishes `{serverId,status,error,generation}`. Status is connecting, reconnecting, connected, failed or disabled. A fresh start connects in the background — servers read `connecting` until their catalog arrives (their `mcp.toolsChanged` announces it) — while a reload of the MCP plugin still requires the previously healthy servers to connect before the swap takes over. `mcp.list` also returns `rejected:[{name,error}]` per server; `mcp.tools` includes the complete valid UI inventory with an `exposed` flag, including entries excluded from model selection.

Physical admission lifecycle: `agents.resources` owner descriptors expose `leaseId`, `executorGeneration`, `correlationId`, `purpose`, `retiring`, `cancellationRequestedAt`, and `providerReturnedAt`. `diag.overview.physicalOwners` works without the scheduler or executor. `diag.call { correlationId }` accepts the stable call identifier as an alternative to numeric `id`; a Diagnostics reload may discard its local call record, while the host still retains ownership. `resources.changed { leaseId }` announces lifecycle updates; `resources.released` remains the release signal. Cancellation requests never imply capacity release.
