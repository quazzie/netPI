# Settings reference

Agent rework additions (2026-10-01): `decide.bulkModel` (empty) selects an explicitly configured model for batches at `decide.bulkThreshold` (8); an explicit per-call model wins. `ideas.verify` (true) requires independent verification before automatic proposals appear; switching it off stops those proposals. `ideas.verifyModel` (empty) inherits `ideas.model`; `ideas.applyVerifiedUpdates` (true) applies verified completion only to an unchanged idea in an idle project. Verification respects paid-model policy and queue deadlines.

`loops.contextChecks` (false) enables provider-captured conversation checks. `loops.routingHints` and `loops.skillHints` (false) add advisory checks to that flow. `todo.checkCommits` (false) suggests completed checklist items after successful commits. These opt-in consumers do not automatically change models, tools, spending permission or goal status; cache performance remains endpoint-dependent.

All settings live in `~/.netpi/settings.json` (Windows: `%USERPROFILE%\.netpi\settings.json`; override the folder
with `NETPI_HOME`). The file is created on first start with the **core keys only** — `server.port`,
`server.devOrigins`, `plugins.disabled`, `plugins.dirs`, `plugins.quiet`, `tools.disabled` — and nothing about one
machine: no default model, no agents, no provider. Every other setting belongs to a plugin and appears with the
default that plugin declares (`settings.schema`), so a fresh install is what each owner says it is. The file accepts
`//` comments and trailing commas, and is watched: edits apply live. Keys are shown as dotted paths —
`providers.aiproxy.baseUrl` means `{ "providers": { "aiproxy": { "baseUrl": … } } }`.

If the file does not parse (a hand edit that dropped a comma), the app keeps serving the last valid document, no
`settings.set` or `settings.replace` is saved over the broken file (it would be replaced by defaults plus one change,
losing providers, API keys, agents and MCP servers), and `diag.problems` names the parse error until the file is fixed.
Writes succeed again as soon as it parses.

One NetPI runs per home (`<home>/netpi.lock`): a second one on the same home refuses to start. A running NetPI also
writes `<home>/server.json` (its URL and this run's token) for tools that inspect it (`docs/DEBUGGING.md`).

The ⚙ settings dialog edits the same file. Most settings are real controls there: the host and each plugin declare
theirs (`settings.schema`, see `docs/PLUGINS.md`), and a control saves one key (`settings.set`; **Reset** removes the key
so the default applies again). The agents and the budget have their own page; the `settings.json` page edits the whole file.

## Core

| key | default | |
|---|---|---|
| `defaultModel` | – | model ref (`provider/model`) for sessions that have neither a model nor an agent; unset: the first loaded local model, else the first one listed |
| `models.refreshSeconds` | `10` | list the models again this often (the providers answer from their own caches), so a model loaded or unloaded in AiSwitcher reaches the agents within seconds; 0 = only on changes |
| `workspace.default` | `~/.netpi/workspace` | working directory of sessions without a project |
| `server.port` | `7431` | a random free port is used when it is taken |
| `server.devOrigins` | `[]` | extra allowed origins (e.g. `http://localhost:5173` for `npm run dev`) |
| `plugins.disabled` | `[]` | plugin ids not to load |
| `plugins.enabled` | `[]` | turn on plugins whose `plugin.json` says `enabled: false` |
| `plugins.dirs` | `[]` | extra plugin folders (besides `<app>/plugins` and `~/.netpi/plugins`) |
| `plugins.quiet` | `false` | while on, a plugin reload (a `/reload`, a build that replaced a plugin's files) is recorded and **not applied**: the running version keeps serving, so nothing swaps under a running chat and no in-memory plugin state is lost. Switching it off applies everything that piled up, in plugin start order. `diag.overview` lists what is waiting in `deferred`, and `plugins.reloaded` says `kind: "deferred"`. The one thing a git worktree cannot do: the running app is one process that every session shares |
| `tools.disabled` | `[]` | tool names hidden from every chat (e.g. `["pwsh"]`); only in this file: the dialog switches whole plugins, and each chat switches its own tools (the composer's tools button, `meta.toolsOff`) |
| `logging.level` | `Information` | host log level (`~/.netpi/logs/netpi-YYYYMMDD.log`) |

Two of the keys above are read at **startup only**, so a change to them takes effect on the next start:
`storage.provider` and `database.sqlitePath`.

## Storage

| key | default | |
|---|---|---|
| `storage.provider` | `sqlite` | the storage provider the store is opened with: `sqlite` (`<home>/netpi.db`) or `memory`. Selection fails closed — a provider that is missing or broken stops the startup with a message that says what to do. `memory` runs only when it is asked for, because running after a broken install would lose every chat; `netpi-server --ephemeral` is that switch for a throwaway run or a test. A provider of your own drops in as an implementation of `IStorageProvider` and has to pass `tests/NetPI.Storage.Tests` (docs/PLUGINS.md) |
| `database.sqlitePath` | – | explicit SQLite **library** to load (default: `winsqlite3.dll` on Windows, `libsqlite3` elsewhere; env `NETPI_SQLITE`); only the `sqlite` provider reads it, and only the `Sqlite3` P/Invoke layer |

The port is `src/NetPI.Abstractions/StoragePort.cs`: sessions/messages/projects, the core's small key-value store,
each plugin's own collections of JSON documents (`ctx.Data`), and a snapshot of the whole store. A provider reads its
own keys from the settings it is handed (`StorageOpenOptions.Settings`, namespaced under `storage.<id>.*`). There is
no SQL outside `src/NetPI.Host/Storage/Sqlite`, and nothing a plugin stores leaves its JSON document.

## Providers

### `providers.aiproxy` (OpenAI-compatible, default transport: Responses API)

| key | default | |
|---|---|---|
| `baseUrl` | `http://127.0.0.1:8090` | a trailing `/v1` is fine |
| `transport` | `responses` | `responses` (`/v1/responses`) or `chat` (`/v1/chat/completions`) |
| `apiKey` | – | sent as Bearer; `"env:NAME"` / `"$NAME"` reads an environment variable |
| `replayReasoning` | Responses: `true`, Chat: `false` | send previous reasoning back. Standard stateless Responses usage appends every output item of a response (reasoning → message → function calls) to the next input |
| `includeEncryptedReasoning` | `false` | also request `include: ["reasoning.encrypted_content"]` (OpenAI-hosted reasoning models; nInfer rejects non-empty `include` with HTTP 400) |
| `dumpFailedRequests` | `true` | save the request **and response** body of failed calls to `~/.netpi/logs/failed-requests/` (newest 30, response capped at 16k chars) and add the server's `x-request-id` / response id to the error, for reproducing backend bugs. A rate limit or an overloaded backend is not saved: it repeats on every attempt and its dumps rotate the useful ones out |
| `parseThinkTags` | `true` | split a leading inline `<think>…</think>` into thinking blocks; a tag after visible text stays text (it is content the model wrote) |
| `defaultMaxOutputTokens` | `16384` | when the catalog says `max_output_tokens: null`. Every provider also cuts the value down to what the model's `contextWindow` has left beside the request, so it never sends more output than the window can hold |
| `modelsCacheSeconds` | `10` | `/v1/models` cache |
| `reasoningSummary` | – | Responses `reasoning.summary` (nInfer rejects it with HTTP 400; leave unset for AiProxy/nInfer) |
| `headers` | – | extra HTTP headers |
| `enabled`, `local` | `true` | a local model's `concurrency` is how many runs its agents share |
| `models.<id>` | – | per model: `transport`, `replayReasoning`, `parseThinkTags`, `maxOutputTokens`, `contextWindow`, `concurrency`, `displayName`, `hidden` |

### `providers.openaiCompatible` (extra endpoints)

An array of `{ "id", "name", "baseUrl", "apiKey"?, "transport"?, "local"?, "headers"? }` plus any of the keys above.
Defaults differ: transport `chat`, `local` only for loopback URLs. Example: LM Studio, a remote llama.cpp, OpenRouter.

### `providers.openrouter` (OpenRouter)

Hundreds of hosted models through one API key. Without a key the provider offers no models. Calls use Chat
Completions with OpenRouter's unified `reasoning` object; each answer's `reasoning_details` are replayed unmodified to the
model that produced them (required for tool calls with Claude, Gemini or OpenAI reasoning). Requests carry the session id
(`session_id`), so a session's requests stay on one upstream provider and its prompt cache stays warm.

| key | default | |
|---|---|---|
| `apiKey` | env `OPENROUTER_API_KEY` | `"env:NAME"` / `"$NAME"` read another environment variable |
| `baseUrl` | `https://openrouter.ai/api` | a trailing `/v1` is fine |
| `include` | all models with tool calls | model ids or globs to offer, e.g. `["stealth/*", "anthropic/claude-*", "*:free"]`; an exact id is offered even without tool support |
| `models.<id>` | – | per model: `displayName`, `contextWindow`, `maxOutputTokens`, `hidden` |
| `maxOutputTokens` | `32768` | cap for `max_tokens` (catalogs report up to 500k+; reasoning counts against it), further cut to what the model's `contextWindow` has left beside the request |
| `provider` | – | routing preferences sent as the request's `provider` object, e.g. `{ "sort": "throughput" }` |
| `promptCaching` | `true` | top-level `cache_control` for `anthropic/*` models (other providers cache automatically) |
| `replayReasoning` | `true` | send `reasoning_details` back |
| `parseThinkTags` | `true` | split a leading inline `<think>…</think>` into thinking blocks; a tag after visible text stays text (it is content the model wrote) |
| `headers` | – | extra HTTP headers, e.g. `HTTP-Referer` + `X-OpenRouter-Title` for OpenRouter's app attribution (creates a public app page) |
| `dumpFailedRequests` | `true` | save the request **and response** body of failed calls to `~/.netpi/logs/failed-requests/` (newest 30, response capped at 16k chars); errors carry the generation id (`responseId` in the dump) |
| `modelsCacheSeconds` | `600` | |
| `enabled` | `true` | |

Effort levels come from the catalog: the picker lists the model's `supported_efforts`, plus `none` where reasoning can be
turned off; models without effort levels only get on/off. Free models are limited to 20 requests per minute and 50 or
1000 per day depending on credits bought; every agent turn is one request. Set up an agent per model you use; its `instances` (default 1) caps the requests at once.

### `providers.anthropic` (Claude)

| key | default | |
|---|---|---|
| `apiKey` | env `ANTHROPIC_API_KEY` | required |
| `baseUrl` | `https://api.anthropic.com` | |
| `thinking` | `budget` | `budget` (`budget_tokens` from the effort), `adaptive`, `off` |
| `thinkingBudgets` | low 2048, medium 8192, high 16384, max 32000 | tokens per effort. A budget is fitted inside `max_tokens` rather than raising it: when the caller named its own cap (a compaction summary, the budget ledger's reservation) that cap is what is sent |
| `adaptiveEffort` | `true` | send `output_config.effort` with adaptive thinking |
| `promptCaching` | `true` | cache_control on system prompt, tools and the rolling last message |
| `defaultMaxOutputTokens` | `32000` | also cut to what the model's `contextWindow` has left beside the request |
| `dumpFailedRequests` | `true` | save the request **and response** body of failed calls to `~/.netpi/logs/failed-requests/` (newest 30) and add the server's `request-id` to the error, for reproducing backend bugs. A rate limit or an overloaded backend is not saved |
| `betas` | `[]` | `anthropic-beta` header values |
| `fallbackModels` | built-in list | used when `/v1/models` can't be listed |
| `modelsCacheSeconds` | `600` | |

## Agents

| key | default | |
|---|---|---|
| `agent.maxTurns` | `200` | model calls per run |
| `agent.defaultMaxOutputTokens` | `16384` | when a model has no limit (the run's request and the budget's reservation of its output) |
| `agent.maxToolResultChars` | `20000` | a longer tool result is saved to a file: the agent gets its start and end and the path, to read the rest in parts or grep it (`read` pages, the bash and `ssh` `run` tails stay under it) |
| `agent.parallelReadOnlyTools` | `true` | run several read-only calls of one turn concurrently |
| `agents.maxDepth` | `3` | subagent nesting depth (deeper agents get no orchestration tools) |
| `agents.queueMax` | `20` | the longest an agent's queue of waiting runs grows: beyond it a new run is refused with a clear error instead of waiting (0: no waiting at all) |
| `agents.queueTimeoutSeconds` | `0` | a run that has waited this long for a free slot fails with a clear error (0, the default: no time limit, a waiting run stays queued until a slot is free) |
| `models.localSlots` | `1` | concurrent runs of one local model without a catalog `concurrency` (Settings → Models) |
| `models.cloudSlots` | `4` | concurrent runs across one cloud provider's models (Settings → Models) |

## Profiles

A profile is the opening of a chat's system prompt and the tools it gets (Settings → Profiles). A new chat gets its
project's default profile (the Projects dialog; `project.meta.profile`, `"none"` for none), else `profiles.defaultProfile`;
each chat can pick another next to the model: free before the first message, later the chat is read again once.
Subagents don't use profiles: the agent that starts one chooses its tools.

```jsonc
"profiles": {
  "defaultProfile": "coder",
  "coder": { "name": "Coder" },
  "admin": { "name": "Admin", "prompt": "You are a system administrator for the hosts in ~/.ssh/config. …",
             "toolsOff": ["bash", "pwsh", "write", "edit"] }
}
```

| key | |
|---|---|
| `profiles.<id>.name` | shown in the pickers |
| `profiles.<id>.prompt` | replaces the opening of the system prompt (the built-in "You are a coding agent…", or `context.customPrompt`); empty keeps it |
| `profiles.<id>.toolsOff` | tools the chat starts without (checkboxes in the dialog); the chat's tools button can still change them |
| `profiles.defaultProfile` | the profile of new chats whose project has none |

## Agents

An agent is a named worker on one model with a number of **instances** (runs at once). Chats and subagents run on
agents: the composer's agent picker sets a chat's agent (`agents.use`), and an agent that delegates picks one for each
subagent (`agent_choices`, `agent_spawn { agent }`) by its note on when to use it. Profiles are separate: the agent is
where a chat runs, the profile who it is.

```jsonc
"agents": {
  "qwen":  { "model": "aiproxy/qwen3.8-27b",                        // instances: the model's slots (2)
             "use": "The local model: free, for everyday work." },
  "bunny": { "model": "openrouter/stealth/space-bunny-alpha", "instances": 2,
             "use": "Free. General coding, research, reading code." },
  "opus":  { "model": "openrouter/anthropic/claude-opus-4.1",       // instances: 1 on a cloud model
             "use": "Costs real money: only for hard problems the free agents could not solve.",
             "budget": { "limitUsd": 5 },                 // this agent, per day
             "cost": { "input": 15, "output": 75 },       // $ per million tokens (default: the catalog's price)
             "disabled": true },                          // switched off (Settings, or its switch in the Work tab)
  "maxDepth": 3                                           // a setting, not an agent (how deep subagents nest)
}
```

- **Active:** an agent takes work only while it can without disturbing anything: a local agent while AiProxy reports its
  model **loaded** (NetPI never loads a model: loading one could evict what another agent runs; switch in AiSwitcher and
  the agents follow within `models.refreshSeconds`), a cloud agent while its provider answers, and neither while
  switched off. An inactive agent stays listed with the reason; a chat on it stops at once with a notice, and waiting
  runs are told too. Runs already going finish.
- **Instances:** default the model's slots for a local model, 1 for a cloud one. A local model's shared capacity is its
  positive catalog `concurrency`, otherwise `models.localSlots`, clamped to at least 1. Every named agent and plain
  model call on that model shares this limit; each agent's instances are an additional limit. Increasing instances
  does not create more model capacity. Different model refs have separate limits.
  Settings/catalog changes refresh existing queues. Reducing capacity lets active calls finish before more start;
  changing an agent's model keeps its active calls counted against their original model until they finish.
- **A run picks no agent up front.** It is eligible for every agent on its chat's model that can take work, and the first
  one with a free instance takes it (a free one before a busy one, then the agent the chat last ran on, then the lighter
  load). The agent in the picker (`meta.agent`) is where the chat last ran, not a reservation; with no agent on its model
  a chat stops with a notice. New chats start on the agent chosen last.
- **Any available agent** (the picker's first entry, `agents.use { agent: "any" }`, `agent_spawn { agent: "any" }`): a run of
  that chat is eligible for every agent that can take work, whatever its model, and the chat's model follows the agent
  it gets. Each run decides again (a run keeps its agent while it lasts). `any` is not an agent id.
- **Waiting:** a run waits for a free instance; with more than one agent it can use it waits **unassigned**, in nobody's
  queue, and `agents.unassigned` / the Work tab list it. With one agent it waits in that agent's queue. Queues are capped
  (`agents.queueMax`) and can be timed (`agents.queueTimeoutSeconds`, off by default): a burst past the cap is refused at
  once, and a run waits as long as it takes unless a time limit is set, after which it fails with a clear error. Queued
  calls across agents and unassigned runs are admitted by priority, then arrival order; a pool at its own instance limit
  does not hold up another eligible pool. A parent resuming after a wait retains its higher priority. FIFO applies within
  a priority class; active calls are not preempted.
- **With no agents at all** (a settings file without any) every model call gets a slot per model:
  positive catalog concurrency, otherwise `models.localSlots` (1, clamped to at least 1), per local model;
  `models.cloudSlots` (4) per
  cloud provider. The same slots serve model calls without an agent (a `compaction.model` on another model).
- **Upgrade:** settings from before agents (lanes) move once: the lanes you set up (`lanes.<id>`, `capacity` →
  `instances`) become agents, plus one for `defaultModel` when none runs it; `lanes.localDefaultCapacity` →
  `models.localSlots`, `lanes.cloudDefaultCapacity` → `models.cloudSlots`, `lanes.budgets` → `budget.providers`; then
  `lanes` is removed.
- **`cost`:** also accepts `cacheRead` and `cacheWrite`; the defaults are 10 % and 125 % of the input price.
- `budget.providers.<provider>.dailyTokens` (older: tokens per day per provider) is still read.

## Budget

| key | default | |
|---|---|---|
| `budget.monthlyUsd` | – | spend per month on paid models; unset = no limit |
| `budget.dailyUsd` | – | spend per day |
| `budget.resetDay` | `1` | the day of the month the budget period starts (1–28) |
| `budget.warnPercent` | `80` | from here `agent_choices` tells agents to use paid agents only when you asked |
| `budget.onLimit` | `"stop"` | when a budget is spent: `"stop"` paid calls, or `"ask"`: your chats stop with "let this chat go over" (`budget.allow`), subagents stop |

Every provider attempt (including each retry) is recorded in the ledger the Agents plugin keeps in `ctx.Data`.
Before dispatch, paid calls reserve
their estimate at the configured/catalog price: the input counted in tokens at the cache-read rate (in an agent
loop the context is re-read, and a call that misses the cache is corrected by the settlement) and the output at
the effective maximum the provider accepts. Admission and reservation are one
transaction, so concurrent chats and plugin generations share the same remaining budget. A reservation
that would exceed a monthly, daily or per-agent limit stops the call; `budget.onLimit: "ask"` offers the existing
explicit per-chat override (`budgetAllowedFrom` in the session's meta). Subagents cannot override. Unknown cloud prices
are refused when a dollar cap applies:
configure **both** input and output prices in the agent, or explicitly allow the chat to go over.

Completed usage replaces the reservation with provider-reported cost, else a token-price estimate. Interrupted
calls settle from the usage actually used (reported, or estimated from what was sent and streamed) — never from
the full reservation. A failure or stop before the first byte settles at $0, so a storm of 503/529s cannot lock
the budget. Persistent reservations survive crashes and hot reload; a lost final bill stays marked
reserved/unsettled instead of silently disappearing. A hot swap briefly runs two ledger generations on one call;
the first reserves, the other passes through its mark. The ledger is **the Agents plugin's own**, and the model
catalog refuses no model: without that plugin (or while its ledger cannot be read or written) paid calls run neither
metered nor limited, which is the trade this design makes. The Budget page identifies
reserved/unsettled, interrupted estimates and unknown-price calls separately.

Reservations are estimates, not a provider billing guarantee: tokenization, image billing and provider price
changes can differ. Amounts are attributed to the attempt's start day. Without a dollar cap unpriced calls remain
allowed and are flagged unknown. Free/local models keep running. An unavailable ledger stops capped paid calls.

## Backups

| key | default | meaning |
|---|---|---|
| `backup.enabled` | `true` | Automatic database + settings + idea-image snapshots, checked ten seconds after plugin start, then every minute |
| `backup.intervalHours` | `24` | Time since the newest snapshot before another automatic one (1–720 hours) |
| `backup.keepCount` | `7` | Automatic snapshots retained after successful creation (1–365); manual snapshots are never pruned |

Settings → **Data & backups** provides **Back up now**, paths, and **Verify**. Snapshots live in `<home>/backups`.
They include settings secrets and the images attached to ideas, and exclude project files, skills, plugins and external
credentials. Copy snapshots to separate storage for disk-failure protection. See [BACKUPS.md](BACKUPS.md) for restore
instructions.

## Context and AGENTS.md

| key | default | |
|---|---|---|
| `context.customPrompt` | – | replaces the identity section of the system prompt |
| `context.appendPrompt` | – | appended at the end |
| `context.toolDescriptions` | `false` | also describe every tool in the system prompt (they are always in the tool schemas) |
| `agentsMd.fileNames` | `["AGENTS.md", "CLAUDE.md"]` | first match per directory, from the file system root down to the working directory |
| `agentsMd.extraFiles` | `[]` | always included |
| `skills.paths` | `[]` | more skill folders, or one skill's folder (`~` is your home; relative to the working directory). See [PLUGIN-SKILLS.md](PLUGIN-SKILLS.md) |
| `skills.claudeCode` | `false` | also Claude Code's skills: `.claude/skills` in the project and `~/.claude/skills` |
| `skills.disabled` | `[]` | skill names switched off: agents don't see them, the `skill` tool and `/skill:` refuse them |
| `agentsMd.guidance` | built in | the "# Instruction files" prompt section: how agents use and keep AGENTS.md (lean, pointers to deeper docs, durable learnings as one line). Replaces the text; `""` drops the section |

A session's system prompt is rendered once, at its first model call, and kept for the whole session (the cached prefix
of the conversation depends on it), so the `context.*` and `agentsMd.guidance` settings apply to sessions started
afterwards. The working directory, the project, the instruction files and the skills are not in the prompt: they reach the model as
notices when they first apply and whenever they change (an edited AGENTS.md is announced on the next model call). Tools
that appear or disappear during a session (a plugin loaded or disabled, `tools.disabled`) are announced the same way,
with their guidelines. The global file is `~/.netpi/AGENTS.md`.

## Auto-compaction, nudge, loops, retry, tool repair

Compaction works like pi's. When fewer than `compaction.reserveTokens` are left in the window, the older part of the
chat is summarized and the last `compaction.keepRecentTokens` are kept as they are (never a tool call without its
result). The summary is a structured checkpoint (Task, Constraints & Preferences, Progress: Done / In Progress /
Blocked, Key Decisions, Next Steps, Critical Context; pi's "Goal" is "Task" so it can't be taken for a /goal) written
at the chat's reasoning effort from a transcript that includes the model's thinking (tool results cut to 2000
characters). An earlier summary is merged by rule (keep everything, move finished work to Done, update the next steps).
When the kept part starts inside one long turn, the start of that turn gets its own summary; what came before it is
summarized only if it holds conversation, not just notices (their plugins announce them again after a compaction, and
nothing but notices to summarize is nothing to compact). The files read and modified are listed from the tool calls themselves
(`<read-files>`, `<modified-files>`) and carried from summary to summary. A summary cut off at its output limit is not
used: the chat stays as it was. Beyond pi: a transcript too long for the summarizer is summarized in rolling parts, and
`compaction.model` can summarize with another model. A *previous* summary that the summarizer in use could not have
written — it was made by a bigger model, or the setting changed — is condensed in its own calls first; that costs extra
summarizer calls and shortens the result, so a smaller `compaction.model` shows up as more calls and a terser summary.

| key | default | |
|---|---|---|
| `compaction.enabled` | `true` | |
| `compaction.reserveTokens` | `16384` | compact when fewer tokens than this are left (at most half the window) |
| `compaction.thresholdPercent` | `1` | also compact at this share of the window (1 = only by the reserve) |
| `compaction.keepRecentTokens` | `20000` | recent messages kept verbatim |
| `compaction.model` | – | summarizer model ref (default: the session's model); it summarizes at the chat's reasoning effort when it offers it |
| `compaction.maxSummaryTokens` | 80 % of the reserve (`13107`) | output budget of a summary, thinking included (at most a quarter of the window) |
| `compaction.defaultContextWindow` | `131072` | for models without a known window |
| `nudge.enabled` / `nudge.maxPerRun` | `true` / `3` | "continue" when a turn ends empty, cut off, or announces an action without doing it. `maxPerRun` counts *consecutive* nudges: any acceptable response (a tool call, a final answer, an aborted or errored turn) resets it, so the cap bounds one stall episode rather than the whole run |
| `loops.enabled` / `loops.maxHintsPerRun` | `true` / `3` | a `loop` notice (a hint, nothing is stopped) before the next model call when the agent is about to repeat itself: the same call after `loops.repeats` − 1 identical results (the whole result text, durations and times aside, and its images: two screenshots or page snapshots that differ anywhere are progress), the same failing call retried, or two steps that undo each other (A, B, A, B); one hint per loop. Subagents too |
| `loops.repeats` | `3` | the call about to run counts (2–10) |
| `loops.model` | – | a decision model (`qwen3.8-27b`, `kev-9b`) that also reads the goal and the last 10 steps when 6 of the last 8 use one tool and 3 of them failed, and hints at p(stuck) ≥ 0.8 (`decide.ask`, needs the Decide plugin; at most 5 checks per run, 10 s each) |
| `toolRepair.enabled` | `true` | execute tool calls a model wrote as text (`<tool_call>…`), but only when the message is nothing but the call: an answer that also explains, documents or quotes the markup stays text, and the nudge asks for a real call |
| `retry.enabled` / `retry.maxAttempts` | `true` / `6` | retries lost connections and stalled streams |
| `retry.baseDelayMs` / `retry.maxDelayMs` | `1000` / `30000` | exponential backoff with jitter; when the server says how long to wait (`Retry-After`, e.g. with a 429 or 529), at least that long |
| `retry.firstEventTimeoutSeconds` | `600` | silence before the first token (slow prefill) |
| `retry.stallTimeoutSeconds` | `180` | silence between tokens |
| `retry.maxTotalSeconds` | `300` | give up once this much of the waiting between attempts (the backoff and any Retry-After) has passed: the time an attempt ran or stalled does not count against it, so a full first-token stall still gets its retry whatever this is set to |

## Tools

| key | default | |
|---|---|---|
| `files.newFileEol` | `lf` | `lf`, `crlf` or `auto` (CRLF on Windows) for new files; existing files keep their style |
| `shell.bashPath` | auto | Git Bash on Windows (`C:\Program Files\Git\bin\bash.exe`, …; never WSL's bash) |
| `shell.pwshPath` | auto | `pwsh` (PowerShell 7), falls back to Windows PowerShell |
| `shell.pwshAlways` | `false` | offer `pwsh` even when no PowerShell was found |
| `shell.timeoutSeconds` | `120` | default per command (max 1800) |
| `ideas.fileName` | `ideas.json` | the name the ideas file had before the backlog moved into the store. Nothing reads or writes it any more; an export is written under it when a path is given, and `ideas.list` reports it so an older UI still has a hint (docs/PLUGIN-IDEAS.md) |
| `ideas.recall` | `true` | while the first message of a chat is typed, a decision looks for the open idea it continues and the composer offers to add it (needs the Decide plugin) |
| `ideas.recallThreshold` | `0.8` | the probability an idea needs before it is offered (0.3–0.99); 0.8 gave no false offer on 56 unrelated messages (docs/DECISION-MODELS.md, "Ideas recall") |
| `ideas.saveCheck` | `true` | when a chat tab is closed, the model says whether it leaves a plan nobody built or wrote down; a new plan gets a card to save or discard, work on an open idea is attached to that idea instead |
| `ideas.attachThreshold` | `0.8` | the probability a closed chat has to be about an open idea before it is attached to it (0.3–0.99); 0.8 was right on 5 of 6 |
| `ideas.model` | `qwen3.8-27b` | the decision model of the idea checks (asked through the Decide plugin's server) and the model that drafts the save check |
| `ideas.allowPaidModel` | `false` | let the automatic checks (save check, the commit and recall questions) run on a paid model. Off: a cloud model is skipped and the log says why — an invoice for a background check is never what you meant |
| `ideas.checkWaitSeconds` | `30` | how long a background check (the save check, the commit sweep) waits for a slot on the model before it is **dropped** instead of run without one (1–300). A drop is never silent: the save check's mark stays retryable with the reason on it, the commit sweep tries that commit again, and the log says what was dropped. The recall keeps its own short 2 s wait — it answers a keystroke |
| `ideas.commitRetrySeconds` | `120` | how long a repository whose commit check failed waits before the check is tried again (0–86400). The wait doubles with every failed attempt and caps at an hour, so a broken check costs one attempt per backoff, not one per sweep trigger; after five failed attempts in a row the commit is recorded unread (`ideas.unread`) and the cursor moves past it, so later commits are read |
| `ideas.verifyRetrySeconds` | `30` | how long a proposal the verifier could not judge (the model was full, or three calls yielded their slot to queued chats) waits before it is tried again (1–3600). It doubles with every attempt and caps at ten minutes; the proposal is tried at most 3 times and then stays retryable on the next close of that chat. The wait ends as soon as higher-priority work on the model settles, and no attempt is made while any is queued — so a busy model costs waiting, not cancelled inference (`ideas.work` says why a proposal waits, how often it has been tried and when it is looked at again) |
| `ideas.closeOnCommit` | `true` | watch every project's repository: a commit is recorded on the open idea it works on, and when it may have finished one you get a card to mark it done (needs the Files plugin to read the commits and Decide for the questions) |
| `ideas.linkThreshold` | `0.7` | the probability a commit has to be about an open idea before it is recorded on it (0.3–0.99); 0.7 linked no wrong idea in 187 commits |
| `ideas.doneThreshold` | `0.8` | the probability an idea has to be finished before you are offered (0.3–0.99); 0.8 offered 4 of 5 finished ideas and nothing that was only advanced |
| `ideas.tellAgentOnCommit` | `true` | after the agent itself runs a successful `git commit`/`git merge` in the session's project, one notice asks it to mark the idea that commit finished. Silent when the project has no open idea, and when there are more than three of them the notice names none and asks for nothing (a backlog that size says nothing about this commit). The cards above stay: they catch a commit made outside any chat |
| `ideas.commitNoticesPerRun` | `2` | how many of those notices one run may get (0–10), so a run that commits in a loop is asked a bounded number of times |
| `ideas.commitNoticeDebounceSec` | `30` | seconds a run waits after a commit notice before it may get the next one (0–3600), so a burst of commits costs one notice instead of one each; 0 turns the debounce off and leaves only the per-run cap |

## Workspaces

A workspace is the checkout a session works in — a root, a branch, a starting commit, an owner (`docs/PROTOCOL.md` for the RPCs and events). The `netpi.workspaces` plugin owns them: it registers `workspaces.*`, `sessions.setWorkspace` and the `workspace.*` / `session.workspace` events, keeps its own collection of workspaces, and writes the binding to the session as `meta.workspaceId` with `meta.cwd` (the folder the chat then runs in). Isolation is at the checkout level, deciding which of a repository's checkouts a worker's files land in: **it is not an OS sandbox** (a shell command is never parsed, and code a worker runs keeps the user's privileges). A project that is not a git repository gets a plain folder, and is otherwise unaffected by these settings.

| key | default | |
|---|---|---|
| `workspaces.isolateWriters` | `true` | a worker that can write gets its own git worktree and branch instead of sharing the project's checkout: it decides `isolated` for `agent_spawn` and `workspaces.create` when the caller does not say, and it follows the worker's **tools** (a subagent that can only read shares its caller's workspace, because a worktree of its own isolates nothing). `isolated: true` asks for one regardless, `isolated: false` asks to share |
| `workspaces.branchPrefix` | `"netpi/"` | the branch name of a worktree NetPI creates is this prefix plus the workspace's name (a taken name gets a `-2`, `-3`, … suffix, so nobody's existing branch is reused) |
| `workspaces.worktreeRoot` | `""` | where worktrees are created: **inside** the project, in its `.worktrees` folder (`<project>/.worktrees/<name>`) when empty, otherwise this folder (a relative path is taken from the project). A root inside the repository is kept out of `git status` through the clone's `.git/info/exclude` — local, so a project this plugin manages never gains a committed ignore line because a worker existed (a project that lists `/.worktrees/` in its own `.gitignore` needs nothing here) — and `workspaces.delete` removes the checkout with `git worktree remove` |

## Guardrails

`plugins/NetPI.Guardrails` checks every tool call before it runs: patterns and paths only (no model call, no change to
the prompt), so it costs microseconds. A matching rule blocks the call (the model reads why; nothing ran); a rule that
starts with `ask:` asks you first on the tool's row in the chat (No / Allow in this chat / Allow) while the run waits with its
instance given back ("Allow in this chat" stops that rule asking for the rest of the chat; other chats still ask); in a subagent an ask rule blocks, since nobody watches its chat. `block:` may start a rule, `#` a comment line. The
checks catch the plain cases (a command built at run time gets through): they are not a sandbox.

| key | default | |
|---|---|---|
| `guardrails.enabled` | `true` | check tool calls against the rules below |
| `guardrails.commands` | catastrophic commands (below) | regular expressions, tried on each part of a `bash`, `pwsh` or `ssh` `run` command (split at new lines, `;`, `&&`, `\|\|`, `\|`, `&`, but not the `&` of a redirection such as `2>&1`; a backslash at the end of a line continues it), ignoring case. A part is tried as written and with what a shell ignores taken off (a trailing `# comment`, redirections, quotes, and wrappers: `sudo` and its options, `env`, `command`, `time`, `nohup`, `exec`, `if`, `then`, `do`), so `rm -rf / 2>/dev/null` and `sudo -n rm -rf "$HOME"` match like `rm -rf /`. A rule that takes longer than 250 ms on a part counts as matching it |
| `guardrails.paths` | `["ask: ~/.netpi", "~/.ssh"]` | files and folders the agent may not change: `write` and `edit` refuse them, so does an `ssh` download (`copy`, direction `download`) whose destination is under one, and so do `bash` and `pwsh` commands that name them, in any spelling a shell uses (`~/.netpi`, `$HOME/.netpi`, `%USERPROFILE%\.netpi`, `$env:USERPROFILE\.netpi`, `C:\Users\me\.netpi`, `/c/Users/me/.netpi`); the `read` tool still reads them. `~` is your home |
| `guardrails.secondOpinion` | `false` | before an `ask:` rule asks you about a `bash`, `pwsh` or `ssh` `run` command, a decision model reads it (`decide.ask`, the Decide plugin through AiGateway); one it finds harmless runs without asking (event `guard.cleared`), the rest ask as before with the model's view on the card. Blocking rules and `write`/`edit` are never relaxed; no answer (no Decide plugin, the model not loaded, 15 s) means you are asked. In a subagent a cleared call runs; one that is not cleared is blocked as before |
| `guardrails.secondOpinionModel` | `qwen3.8-27b` | the decision model; `qwen3.8-27b` (about 0.3 s) was measured on 852 real commands (docs/DECISION-MODELS.md, "NInfer baselines" 0.4); `kev-9b` calls too many local commands remote |
| `guardrails.secondOpinionThreshold` | `0.2` | a command runs without asking only when p(destructive), p(stops a process) and p(changes a remote) are each below it (0.01–0.5); p(read-only) is shown on the card but not required (the model underrates builds and test runs). At 0.3 a hub quit was cleared in the hand-labelled set, at 0.2 no risky command |

The default commands: `rm -r` of `/`, `/*`, `~` or `$HOME`; deleting a drive root (`rm`, `rd`, `del`, `Remove-Item` of
`C:\`, `/c`); `mkfs`; `dd … of=/dev/…` (not `/dev/null`); `format C:`; `shutdown`, `reboot`, `poweroff`, `halt`,
`Stop-Computer`, `Restart-Computer`; a fork bomb. Each is anchored at the start of a command part, so `grep shutdown` or
`echo 'rm -rf /'` pass.

The guard judges the call that runs, not another one: tool names match ignoring case (`Bash` runs `bash`), and arguments are read as the
tools read them: names ignoring case, `_`, `-` and spaces; the first name the tool tries wins (`bash`: `command`, `cmd`, `script`, `code`,
`commands`, `input`; `ssh` run: `script`, `command`, `cmd`, `code`; `write`/`edit`: `path`, `file_path`, `file`, `filename`, `target`);
an array is its lines; a string-encoded arguments object is unwrapped. The workspace guard reads its paths the same way, and also
refuses an `ssh` download into another checkout of the repository.

## Web tools

| key | default | |
|---|---|---|
| `web.fetch.maxChars` | `20000` | characters per `web_fetch` part (1000–200000); longer pages continue with `offset` |
| `web.fetch.timeoutSeconds` | `30` | per fetch |
| `web.fetch.maxBytes` | `5000000` | download cap |
| `web.userAgent` | a Chrome-like UA ending in `NetPI/0.1` | |
| `web.search.provider` | `auto` | `auto` (SearXNG when a URL is set, falling back to Brave), `searxng` or `brave` |
| `web.search.searxngUrl` | – | e.g. `http://192.168.1.3:8888` (the instance must allow `format=json`); a SearXNG that queries Brave itself makes a Brave key in NetPI a fallback only |
| `web.search.braveApiKey` | env `BRAVE_API_KEY` | Brave Search API key; `"env:NAME"` / `"$NAME"` read an environment variable |
| `web.search.braveUrl` | `https://api.search.brave.com/res/v1/web/search` | |
| `web.search.count` | `8` | results per search (max 20) |
| `media.maxBytes` | `10000000` | largest image `show_image` shows |
| `decide.baseUrl` | – | server for `decide` (`POST /v1/systemone`); empty = `providers.aiproxy.baseUrl` |
| `decide.model` | `qwen3.8-27b` | decision model `decide` asks: `qwen3.8-27b` on NInfer (through AiGateway's System One bridge; the best without training, no extra memory, shares the 5090 with the agents), `laya-logs` for the four log questions on the nuc, or `kev-9b`/`kev-4b` on the nuc (load it from AiHub first; it takes the whole 4070). See docs/DECISION-MODELS.md |
| `decide.maxItems` | `500` | most items per `decide` call (1–5000) |
| `decide.parallel` | `4` | `decide` requests at once (1–16) |
| `web.browserPath` | auto | Edge, Chrome or Chromium for `screenshot` and `browser` (found in the usual install folders or on PATH) |
| `browser.target` | `chrome` | Where a chat's `browser` tab opens: `chrome`, the user's running Chrome (remote debugging allowed at `chrome://inspect/#remote-debugging`), or `own`, the agents' hidden browser. A call can ask for the other with `browser` |
| `browser.chromeUserData` | Chrome's default | The folder holding the user's Chrome `DevToolsActivePort` (`%LOCALAPPDATA%\Google\Chrome\User Data`, `~/Library/Application Support/Google/Chrome`, `~/.config/google-chrome`) |
| `browser.headless` | `true` | `false`: the agents' browser opens a window (to log in to a site by hand, or to watch). Applies when the browser next starts |
| `browser.profile` | `default` | A name: logins and cookies are kept in `<home>/browser/<name>`. `temp`: a fresh profile each time the browser starts, deleted when it closes |
| `browser.idleMinutes` | `10` | The browser closes after this many minutes without a `browser` call (1–1440); the chats' tabs close with it |
| `browser.maxControls` | `200` | Controls listed per page, the ones nearest the visible part (50–1000); `find` reaches the rest |

## Goals

| key | default | |
|---|---|---|
| `goal.maxContinuations` | `100` | automatic runs before the goal pauses (resume allows as many again) |
| `goal.noProgressLimit` | `3` | automatic runs in a row without a successful tool call before the goal pauses |
| `goal.tokenBudget` | `0` | token budget for goals set without one (0 = none); input not read from the cache plus output |

## SSH tools

| key | default | |
|---|---|---|
| `ssh.path` | auto | the ssh client: Windows OpenSSH (`System32\OpenSSH\ssh.exe`), else Git's, else `ssh` on PATH |
| `ssh.scpPath` | next to `ssh.path` | the scp client |
| `ssh.config` | `~/.ssh/config` | where the host aliases come from; any other file is also passed to ssh and scp (`-F`) |
| `ssh.connectTimeoutSeconds` | `10` | (2–120) |
| `ssh.timeoutSeconds` | `120` | default `ssh` `run` timeout (max 1800) |

Where the client supports it, calls to one host ride a single master connection (OpenSSH `ControlMaster`, 5 minutes
after the last call): the first call of a run pays the TCP + transport + auth handshake, the rest reuse the socket. The
control sockets live in `~/.netpi/ssh/` (one per host and user, hashed), so they don't touch the user's own folder.

**The client is asked, not assumed** — an option it does not know is a hard error, and it may not multiplex at all:

- The idle timeout is only passed to a client that accepts it. OpenSSH_for_Windows (NetPI's own client on Windows) has
  no `ControlIdleTimeout`, and asking for it fails every call with `Bad configuration option`.
- A client that cannot multiplex gets no master connection: OpenSSH_for_Windows accepts `ControlMaster` and then fails
  every session with `getsockname failed: Not a socket`, so those calls each do their own handshake, as they did before
  connection reuse. This is the normal case on Windows.

Both questions are answered once per client, offline (`ssh -o … -G localhost` and `ssh -O check`), and take a few
milliseconds; anything unexpected answers "not supported", so a call never fails because of the check itself.

## MCP servers

`mcp.servers` is an object keyed by stable server id. Each entry accepts `enabled`, `transport` (stdio/http), `command`, `args`, absolute `cwd`, `url`, `env`, `headerEnv`, `tools`, `resources`, `pinned`, `readOnly`, `synonyms`, `connectTimeoutMs` (5000) and `callTimeoutMs` (60000). env/headerEnv values name source environment variables. tools/pinned/readOnly contain raw remote tool names; resources holds resource URIs (exact, or a prefix ending in `*`). Details and examples: [PLUGIN-MCP.md](PLUGIN-MCP.md).

| Setting | Default | Purpose |
| --- | ---: | --- |
| mcp.discoveryChars | 4000 | Bounded search/schema result; complete large schemas saved as files |
| mcp.maxMessageChars | 4000000 | Protocol frame/SSE event limit |
| mcp.maxCatalogTools | 10000 | Tools per server |
| mcp.maxCatalogChars | 8000000 | Catalog data per server |
| mcp.maxResourceEntries | 2000 | Resources per server |
| mcp.maxResourceCatalogChars | 2000000 | Resource catalog data per server |
| mcp.maxResourceChars | 20000 | Characters per resource read; a larger one is written to a file |
| mcp.maxBinaryBytes | 2000000 | Decoded image limit |
| mcp.refreshSeconds | 60 | Polling fallback |
