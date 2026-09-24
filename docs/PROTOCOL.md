# NetPI UI ⇄ Server protocol

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

## Data shapes (camelCase JSON)

```ts
type Role = 'user' | 'assistant' | 'tool' | 'notice' | 'summary';
interface ChatMessage {
  id: number; seq: number; sessionId: string; role: Role; parts: Part[]; createdAt: string;
  provider?: string; model?: string; stopReason?: 'stop'|'tool_use'|'length'|'aborted'|'error'|'content_filter';
  usage?: { inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number; reasoningTokens: number };
  durationMs?: number; compacted: boolean;
  meta?: { kind?: string; [k: string]: any };   // notice kinds: project | instructions | nudge | agent-message | agent-result | steer | retry | error | compaction
}
type Part =
  | { type: 'text'; text: string }
  | { type: 'thinking'; text: string; durationMs?: number; signature?: string; redacted?: string }
  | { type: 'tool_call'; id: string; name: string; arguments: string /* raw JSON */ }
  | { type: 'tool_result'; callId: string; name: string; content: string; isError: boolean; details?: any; durationMs?: number; images?: ImagePart[] }
  | { type: 'image'; mediaType: string; data: string /* base64 */ };

interface SessionInfo { id; title; projectId?; parentSessionId?; kind: 'chat'|'subagent'; model?; reasoning?; createdAt; updatedAt; archived; messageCount; contextTokens; meta? }
interface ProjectInfo { id; name; path; createdAt; updatedAt; lastUsedAt? }
interface ModelInfo { provider; id; ref /* "provider/id" */; displayName?; contextWindow?; maxOutputTokens?; concurrency?;
  inputModalities: string[]; reasoning?: { supported: boolean; efforts: string[]; default?: string }; status?; isLocal: boolean }
type AgentStatus = 'idle'|'queued'|'running'|'yielded'|'completed'|'failed'|'cancelled';
interface AgentInfo { id; sessionId; name; parentAgentId?; parentSessionId?; isSubagent; depth; status: AgentStatus; model?; pool?;
  activity?; createdAt; startedAt?; finishedAt?; runs; turns; toolCalls; inputTokens; outputTokens; queuedMessages; task?; result?; error?; children: string[] }
interface QueuedInput { id; text; mode: 'steer'|'queue'; source; createdAt }
interface LanePoolInfo { key; provider?; capacity; busy; queued; models: string[]; owners: LaneOwner[]; waiters: LaneOwner[]; source; status? }
interface LaneOwner { agentId; sessionId?; label?; since }
interface UiTabInfo { id; title; panel: 'left'|'right'; icon?; module; export?; order; pluginId; version }
interface SlashCommandInfo { name; description; rpc?; clientAction?; argsHint?; pluginId }
interface PluginInfo { id; name; description?; version?; directory; state; error?; loadedAt?; loadCount; loadMs; order; enabled }
```

## Core RPC methods (host)

| method | params | result |
|---|---|---|
| `app.info` | – | `{ version, os, osDescription, home, appDir, defaultWorkspace, settingsFile, desktop, pid, dotnet, sqlite, pathSeparator }` |
| `projects.list` | – | `ProjectInfo[]` |
| `projects.create` | `{ name, path, create? }` | `ProjectInfo` |
| `projects.update` | `{ id, name?, path? }` | `ProjectInfo` |
| `projects.delete` | `{ id }` | `true` (its sessions are detached) |
| `sessions.list` | `{ projectId?, search?, includeSubagents?, parentSessionId?, includeArchived?, limit?, offset? }` | `SessionInfo[]` (newest first) |
| `sessions.create` | `{ title?, projectId?, model?, reasoning? }` | `SessionInfo` |
| `sessions.get` | `{ id }` | `SessionInfo` |
| `sessions.update` | `{ id, title?, model?, reasoning?, archived?, meta? }` | `SessionInfo` (null clears model / reasoning) |
| `sessions.delete` | `{ id }` | `true` (its subagent sessions are deleted too) |
| `sessions.setProject` | `{ id, projectId: string\|null }` | `SessionInfo`; publishes `session.project` (the context plugin appends a `project` notice) |
| `sessions.messages` | `{ id, beforeSeq?, limit? (default 60) }` | `{ messages: ChatMessage[], hasMore: boolean }` ascending by seq |
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
| `settings.set` | `{ path, value }` | `true` (dotted path; a null value removes the key) |
| `settings.replace` | `{ settings: object }` | `true` |
| `fs.dirs` | `{ path? }` | `{ path, parent, dirs: {name,path}[], roots: string[] }` (folder picker) |
| `tools.list` | – | `{ name, label, description, category, readOnly, pluginId, active, disabled, priority }[]` |
| `rpc.list` | – | `{ method, description, pluginId }[]` |
| `services.list` | – | `{ type, implementation, priority, owner }[]` (registered services) |
| `events.recent` | `{ max? (200, up to 500) }` | `{ type, sid, d, seq, ts, source, ui }[]` recent bus events |
| `logs.recent` | `{ max? (200, up to 2000) }` | `{ time, level, category, message, exception? }[]` |

## Plugin RPC methods

| method | plugin | params → result |
|---|---|---|
| `agent.send` | netpi.agent | `{ sessionId, text, images?: {mediaType,data}[], mode?: 'auto'\|'steer'\|'queue' }` → `AgentInfo` |
| `agent.abort` | netpi.agent | `{ sessionId }` → `bool` |
| `agent.queue` | netpi.agent | `{ sessionId }` → `QueuedInput[]` |
| `agent.dequeue` | netpi.agent | `{ sessionId, id }` → `bool` |
| `agents.list` | netpi.agent | `{ includeFinished? }` → `AgentInfo[]` |
| `agent.get` | netpi.agent | `{ id? , sessionId? }` → `AgentInfo\|null` |
| `lanes.list` | netpi.lanes | → `LanePoolInfo[]` |
| `usage.summary` | netpi.lanes | → `{ day, providers: { provider, inputTokens, outputTokens, cacheReadTokens, calls, budgetTokens? }[] }` |
| `agentsmd.list` | netpi.agentsmd | `{ sessionId }` → `{ path, bytes, scope }[]` (instruction files for the session's working directory; scope `global`, `project` or `extra`) |
| `compaction.run` | netpi.compaction | `{ sessionId, args? /* extra focus for the summary */ }` → `string` (error `busy` while the agent runs) |
| `context.preview` | netpi.context | `{ sessionId }` → `{ systemPrompt, frozen, tools: {name, description}[], estimatedTokens }` (`frozen`: the prompt stored at the session's first model call) |
| `files.search` | netpi.tools.files | `{ sessionId?, query, limit? }` → `{ path, rel, isDir }[]` (for `@` mentions) |
| `files.list` | netpi.tools.files | `{ sessionId?, dir? }` → `{ root, dir, entries: {name, rel, isDir, size?, mtime?}[] }` |
| `processes.list` | netpi.tools.shell | → `ProcessInfo[]` |
| `processes.output` | netpi.tools.shell | `{ id, tail? }` → `string` |
| `processes.kill` | netpi.tools.shell | `{ id }` → `bool` |
| `ideas.list` `ideas.get` `ideas.add` `ideas.update` `ideas.delete` `ideas.reorder` `ideas.toPrompt` `ideas.quickAdd` | netpi.ideas | see `docs/PLUGIN-IDEAS.md` |
| `work.snapshot` | netpi.work | → `{ lanes, agents, processes, usage, time, errors? }` (each part `null` when unavailable; see `docs/PLUGIN-WORK.md`) |
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
| `session.created` / `session.updated` | no | `{ session: SessionInfo }` |
| `session.deleted` | no | `{ id }` |
| `session.project` | no | `{ sessionId, projectId, cwd }` – attached to another project or detached |
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
| `agent.queue` | yes | `{ sessionId, items: QueuedInput[] }` |
| `agent.notice` | yes | `{ sessionId, level: 'info'\|'warn'\|'error', text }` – transient (retry countdown etc.) |
| `session.context` | no | `{ sessionId, used, window }` |
| `lanes.changed` | no | `{ pools: LanePoolInfo[] }` |
| `models.changed`, `plugins.changed`, `ui.changed`, `settings.changed` | no | `{}` |
| `usage.recorded` | no | `{ provider, model, usage }` |
| `process.started` / `process.exited` | no | `{ process: ProcessInfo }` |
| `ideas.changed` | no | `{ file }` |

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
    toast(text: string, level?: 'info'|'warn'|'error'): void,
  }
}
```

Plugin UIs render inside the host DOM and should style themselves with the host CSS variables
(`--bg`, `--bg-1`, `--bg-2`, `--bg-3`, `--border`, `--fg`, `--fg-muted`, `--fg-dim`, `--accent`, `--ok`, `--warn`, `--err`,
`--font-ui`, `--font-mono`, `--radius`) and the `np-*` utility classes documented in `web/src/styles/kit.css`.
