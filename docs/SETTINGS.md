# Settings reference

Agent rework additions (2026-10-01): `decide.bulkModel` (empty) selects an explicitly configured model for batches at `decide.bulkThreshold` (8); an explicit per-call model wins.

All settings live in `~/.netpi/settings.json` (Windows: `%USERPROFILE%\.netpi\settings.json`; override the folder
with `NETPI_HOME`). The file is created on first start with the **core keys only** — `server.port`,
`server.devOrigins`, `plugins.disabled`, `plugins.dirs`, `plugins.quiet`, `tools.disabled` — and nothing about one
machine: no default model, no agents, no provider. Every other setting belongs to a plugin and appears with the
default that plugin declares (`settings.schema`), so a fresh install is what each owner says it is. The file accepts
`//` comments and trailing commas, and is watched: edits apply live. Keys are shown as dotted paths —
`providers.instances.aiproxy.baseUrl` means `{ "providers": { "instances": { "aiproxy": { "baseUrl": … } } } }`.

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
| `plugins.quiet` | `false` | while on, a plugin reload (a `/reload`, a build that replaced a plugin's files) is recorded and **not applied**: the running version keeps serving, so nothing swaps under a running chat and no in-memory plugin state is lost. Switching it off applies everything that piled up, in plugin start order. `diag.overview` lists what is waiting in `deferred`, and `plugins.reloaded` says `kind: "deferred"`. The one thing a git worktree cannot do: the running app is one process that every session shares. A switch in **Settings → General** |
| `tools.deferred` | the default list | built-in tools kept out of requests until a chat uses them (called by name or found with `tool_search`); absent: `ToolExposure.DefaultDeferred` (all but the file and shell tools, `codemode`, `tool_search`, `ask_user`), `[]`: every built-in tool declared; `"*"` (every tool) and `"!name"` (an exception) work too. A profile can have its own (`profiles.<id>.toolsDeferred`). Only in this file (docs/TOOLS.md, "Deferred tools") |
| `tools.disabled` | `[]` | tool names hidden from every chat (e.g. `["pwsh"]`); only in this file: the dialog switches whole plugins, and each chat switches its own tools (the composer's tools button, `meta.toolsOff`) |
| `logging.level` | `Information` | host log level (`~/.netpi/logs/netpi-YYYYMMDD.log`) |

Two of the keys above are read at **startup only**, so a change to them takes effect on the next start:
`storage.provider` and `database.sqlitePath`.

## Storage

| key | default | |
|---|---|---|
| `storage.provider` | `sqlite` | the storage provider the store is opened with: `sqlite` (`<home>/netpi.db`) or `memory`. Selection fails closed — a provider that is missing or broken stops the startup with a message that says what to do. `memory` runs only when it is asked for, because running after a broken install would lose every chat; `netpi-server --ephemeral` is that switch for a throwaway run or a test. A provider of your own drops in as an implementation of `IStorageProvider` and has to pass `tests/NetPI.Storage.Tests` (docs/PLUGINS.md) |
| `database.sqlitePath` | – | explicit SQLite **library** to load (default: `winsqlite3.dll` on Windows, `libsqlite3` elsewhere; env `NETPI_SQLITE`); only the `sqlite` provider reads it, and only the `Sqlite3` P/Invoke layer. A path field in **Settings → General** |

The port is `src/NetPI.Abstractions/StoragePort.cs`: sessions/messages/projects, the core's small key-value store,
each plugin's own collections of JSON documents (`ctx.Data`), and a snapshot of the whole store. A provider reads its
own keys from the settings it is handed (`StorageOpenOptions.Settings`, namespaced under `storage.<id>.*`). There is
no SQL outside `src/NetPI.Host/Storage/Sqlite`, and nothing a plugin stores leaves its JSON document.

## Providers

`providers.instances` is an object keyed by **provider id** (the part before the slash in a model reference, `claude-cli/…`):
`{ "work": { "type": "claude-cli", "name": "Claude (work)", "configDir": "~/.claude-work" }, … }`. It starts empty: no provider
exists until one is added (Settings → Models → **Add provider**: choose a kind, configure it; add as many as you like, also of the same
kind). An entry holds its `type`, an optional `name` (shown in lists; default the kind's title), `enabled` (default `true`) and the keys of
its kind below. Entries apply live: an added one lists its models at once, a removed one is gone, an edited key applies on the next call.
An id is letters, digits, `-` and `_`. Kinds: `aiproxy`, `openai-compatible`, `openrouter`, `anthropic`, `claude-cli`.
`scripts/migrate-providers.mjs` moves the older layout (one block per provider under `providers.`) into this one.

### Kind `aiproxy` (AiProxy / nInfer: the Responses API, local models)

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
| `modelsCacheSeconds` | `10` | `/v1/models` cache; local endpoints are checked every 2 s in the provider background loop, respecting this TTL |
| `reasoningSummary` | – | Responses `reasoning.summary` (nInfer rejects it with HTTP 400; leave unset for AiProxy/nInfer) |
| `headers` | – | extra HTTP headers |
| `enabled`, `local` | `true` | a local model's `concurrency` is how many runs its agents share |
| `models.<id>` | – | per model: `transport`, `replayReasoning`, `parseThinkTags`, `maxOutputTokens`, `contextWindow`, `concurrency`, `displayName`, `hidden` |

### Kind `openai-compatible` (any OpenAI-style server)

LM Studio, a remote llama.cpp, vLLM, OpenAI itself. The keys of `aiproxy` below apply; the defaults differ: `transport` `chat`, `local`
only for a loopback URL, and `baseUrl` is required (an entry without one is ignored, with a log line).

### Kind `openrouter` (OpenRouter)

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
| `maxOutputTokens` | `32768` | cap for `max_tokens` (catalogs report up to 500k+; reasoning counts against it), further cut to what the model's `contextWindow` has left beside the request. The catalog lists each model's output limit under this cap (a per-model `maxOutputTokens` is used as it is), so the budget reserves what a call can actually produce |
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

### Kind `anthropic` (Claude, API key)

| key | default | |
|---|---|---|
| `apiKey` | env `ANTHROPIC_API_KEY` | required |
| `baseUrl` | `https://api.anthropic.com` | |
| `thinking` | `auto` | `auto`: by model (`budget_tokens` up to Claude 4.5, adaptive from 4.6, where `budget_tokens` is deprecated and from 4.7 on refused); `budget` (`budget_tokens` from the effort), `adaptive`, `off` |
| `thinkingDisplay` | `summarized` | adaptive thinking only: `summarized` returns readable thinking, `omitted` leaves the thinking blocks empty (the API's default from 4.7 on; the duration is still shown) |
| `thinkingBudgets` | low 2048, medium 8192, high 16384, max 32000 | tokens per effort. A budget is fitted inside `max_tokens` rather than raising it: when the caller named its own cap (a compaction summary, the budget ledger's reservation) that cap is what is sent |
| `adaptiveEffort` | `true` | send `output_config.effort` with adaptive thinking |
| `promptCaching` | `true` | cache_control on system prompt, tools and the rolling last message |
| `defaultMaxOutputTokens` | `32000` | also cut to what the model's `contextWindow` has left beside the request |
| `dumpFailedRequests` | `true` | save the request **and response** body of failed calls to `~/.netpi/logs/failed-requests/` (newest 30) and add the server's `request-id` to the error, for reproducing backend bugs. A rate limit or an overloaded backend is not saved |
| `betas` | `[]` | `anthropic-beta` header values |
| `fallbackModels` | built-in list | used when `/v1/models` can't be listed |
| `modelsCacheSeconds` | `600` | |

### Kind `claude-cli` (Claude through the Claude Code CLI)

Runs the installed `claude -p` as a subprocess, signed in with the owner's subscription (`claude auth login` once, in a
terminal; the CLI bundled with the Claude desktop app has its own login and does not share it). Give an agent a
`claude-cli/…` model and that agent **is** Claude Code for the run: it works in the session's workspace with the CLI's own
tools (read, search, edit, write, shell, web) and answers in text. NetPI's tools are not used; a notice names the folder the
CLI works in, and its tool use shows in the chat as ordinary tool-call blocks (named as the CLI names them, `Read`, `Bash`,
`Edit`…), already run: the agent loop records them and never executes them (see "Calls the provider ran itself" in
[PROTOCOL.md](PROTOCOL.md)). The CLI's tools
act on the disk directly: NetPI's workspace guard and permission rules do not apply to them. Images are not sent.
`ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` are removed from the child's environment, so a call is never billed to an API
key by accident; if the CLI still reports an API-key sign-in, the stream carries a warning. The cost is left unreported
(`total_cost_usd` is the API price, not what a subscription pays), so budgets do not count it. Subscription usage limits
apply, and a run that works through many files uses a lot of them.

| key | default | |
|---|---|---|
| `enabled` | `true` | models are listed only while enabled |
| `configDir` | – | the CLI's own config folder (`CLAUDE_CONFIG_DIR`, `~` allowed), where its login lives; empty = `~/.claude`. Another folder is another account: add one provider per account and sign each in with `CLAUDE_CONFIG_DIR=<folder> claude auth login` |
| `executable` | `claude` | name on PATH or a full path. A bare `claude` that is not on PATH is looked for in `~/.local/bin`, where the installer puts it |
| `models` | `claude-opus-5-5`, `claude-sonnet-5-5`, `claude-haiku-5-5` | ids passed to `--model` |
| `timeoutSeconds` | `3600` | the longest one call may run before it is killed (10–14400). A backstop: a CLI that makes progress is not stuck, and a dead one ends by itself |
| `maxConcurrent` | `2` | CLI processes at once (1–16): each is a full Claude Code, and the subscription's rate limits are shared; the other calls wait, and say so |
| `tools` | `default` | `default`: every built-in tool of the CLI; `none`: text only; or a list for `--tools`, e.g. `Read,Grep,Glob` for read-only. NetPI's own tools are not handed to the CLI; a turn that keeps its Bash is told how it reaches NetPI itself instead: `node "<app>/netpi.mjs" --home "<home>" <method>` (`netpi.mjs` ships beside the app), for the ideas, `memory.search` and the MCP servers NetPI holds (`mcp.read`, `mcp.call`), so a session in another project neither rebuilds an MCP client nor reads a server's URL |
| `permissionMode` | `bypassPermissions` | a print-mode call cannot answer a prompt, so the CLI's tools run unasked; `acceptEdits`, `plan` and `default` limit them (what is not allowed then fails) |
| `settingSources` | – | which settings files the CLI loads (`user`, `project`, `local`); none by default, because they bring hooks and permission rules |
| `connectors` | `false` | let the CLI load the account's MCP connectors |
| `usage` | `true` | show the Claude subscription usage (session and weekly windows) in the context ring's popout. It is the utilization the CLI prints after each call; hovering or pressing the ring also asks Anthropic for the live figure with the CLI's login token (`claudecli.usage.refresh`, at most once a minute) |
| `stallTimeoutSeconds` | `1800` | how long the CLI may stay silent before the retry plugin declares the call stalled and retries it. The CLI's own tools and subagents can run for minutes without a line of output (the global `retry.stallTimeoutSeconds` is 180 s, which would kill a running subagent and wipe the chat's in-flight answer); 0 never declares a stall — a dead CLI ends the stream on its own |

Each call is one process with the prompt on stdin (the system prompt through a temporary `--system-prompt-file` that also
tells the model which tools it really has). The working folder is the session's workspace (else its project folder, else
`<home>/claude-cli`, where the prompt files are written too: the home is the owner's alone, a shared temp folder is not). The
reported usage is the last turn of the CLI's own loop, so the context size is the size of the CLI's real context, shown by the context
ring against the model's real window: the CLI reports it with each answer (1M for the Claude 5 models, 200k for the earlier ones; read
from the result's entry for the model asked for, since the CLI lists every model it used, a Haiku for an Explore subagent among them),
and the catalog takes it up. The CLI compacts its own session, so NetPI's compaction leaves these chats alone. `claude auth status`
(the plan the usage popout names) runs only while the provider is enabled. A failed live usage ask (`claudecli.usage.refresh`) is the
answer again for the rest of its minute, not the old reading; on macOS the CLI keeps its login in the Keychain, which NetPI does not
read, so the reading there comes from the calls alone.

**A chat keeps one CLI session.** The first turn starts it (`--session-id`, with the conversation so far as the prompt); every
later turn resumes it (`--resume`) and sends only what is new to it: the messages after the point it had, without its own answers
and its own tools' results (it holds those), an answer another model gave in between (labelled), and no compaction summary (the CLI
has the real history, and compacts it itself). Nothing new (a retry) sends "Continue.". The pointer (NetPI chat → CLI session, and
the newest message the CLI has) is kept in the plugin's data across restarts and dropped after 60 days without use. A CLI session
that no longer exists, or a chat whose history was cut back, starts a new session with the whole conversation (and says so). A title
or compaction call through the model uses no session. The CLI keeps its sessions as files under `~/.claude/projects`, so a chat's
sessions also show in `claude --resume`; NetPI never deletes them.

Only an agent's own turns get the CLI's tools and its folder: a title, a compaction summary or any other side call through a
`claude-cli` model is text only and runs outside every project. The child process does not inherit `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, the cloud-provider switches, NetPI's own secrets, any variable a secret setting
reads (`env:NAME`) or `shell.hideEnv`, the same policy as NetPI's shell tool; the user's other variables stay. On Windows the
CLI and everything it starts are in a job object that dies with NetPI, so a killed or crashed NetPI leaves no CLI running. A
failure after the CLI ran tools is reported and never retried automatically, and a refusal for the subscription's limit says when
the window opens again.

**The CLI's tools follow what NetPI gives the run** (idea-hghbmg). Its calls never pass NetPI's before-call hooks, so the provider
holds them itself: a CLI tool whose NetPI counterpart exists but is not given to the run (bash → `Bash`, edit/write → `Edit`, `Write`,
read/grep/find → `Read`, `Grep`, `Glob`, web_fetch/web_search → `WebFetch`, `WebSearch`, agent_spawn → `Task`, todo_write →
`TodoWrite`) goes to `--disallowedTools` — a tool switched off for the chat, a profile without it, a subagent handed a shorter list.
A counterpart NetPI does not have at all leaves the CLI's tool alone. A run that may only read (plan mode, and a research subagent
of a chat in plan mode: the Plan plugin marks the call) gets `--tools Read,Grep,Glob,WebFetch,WebSearch,TodoWrite` (what of that the
run is given). The chat is told in a notice when something was held back. Goals need `goal_update`, which the CLI does not get, so a
goal cannot be set or resumed on a `claude-cli` chat, and a goal whose chat moves to one pauses after the run. The after-call hooks
see the CLI's calls under their NetPI names, so a goal set earlier still counts the CLI's work.

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
Chats NetPI starts by itself (idea work from the Ideas tab, the start screen or the idea dialog, a plan approved in a
new chat, idea refinement, schedule runs, `sessions.dispatch` without a profile) get `profiles.autoProfile` in place of
the global default; a project's own default still wins. Such starters call `profiles.applyAuto` ([PROTOCOL](PROTOCOL.md)).

```jsonc
"profiles": {
  "defaultProfile": "coder",
  "autoProfile": "coder",      // "none": no profile; unset: the same as defaultProfile
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
| `profiles.<id>.toolsDeferred` | the profile's own `tools.deferred` (written to the chat's `meta.toolsDeferred`): tool names, `"*"` for every tool, `"!name"` for an exception — `["*", "!codemode"]` sends codemode alone; unset: the setting. The dialog's "Deferred tools" field |
| `profiles.defaultProfile` | the profile of new chats whose project has none |
| `profiles.autoProfile` | the profile of chats NetPI starts by itself whose project has none: a profile id, `"none"`, or unset for `defaultProfile` |

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
  local OpenAI-compatible providers refresh in the background within their model-cache TTL plus 2 s; the agents follow `models.changed`), a cloud agent while its provider answers, and neither while
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
- **`cost`:** also accepts `cacheRead` and `cacheWrite`; the defaults are 10 % and 125 % of the input price.
- The older `budget.providers.<provider>.dailyTokens` (tokens per day per provider) is no longer read: the dollar budget
  below is the one limit, and an agent's `budget` caps its own dollars per day.

## Budget

| key | default | |
|---|---|---|
| `budget.monthlyUsd` | – | spend per month on paid models; unset = no limit |
| `budget.dailyUsd` | – | spend per day |
| `budget.resetDay` | `1` | the day of the month the budget period starts (1–28) |
| `budget.warnPercent` | `80` | from here `agent_choices` tells agents to use paid agents only when you asked |
| `budget.onLimit` | `"stop"` | when a budget is spent: `"stop"` paid calls, or `"ask"`: your chats stop with "let this chat go over" (`budget.allow`), subagents stop |
| `budget.keepCallsDays` | `90` | days the ledger keeps its row per model call (at least 62; `0` keeps every row). Older settled rows are deleted at start and every 5000 calls; the totals per period, day, agent and chat (`session_usage`) stay, so only `usage.chats` of a period older than that comes back empty (idea-sdevr6) |


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

## Schedules

| key | default | meaning |
|---|---|---|
| `schedules.enabled` | `true` | Start due runs (`netpi.schedules`, `schedules.*` in PROTOCOL.md). Off: nothing starts; due runs wait and the missed-run rule applies when it is back on |
| `schedules.missedGraceHours` | `24` | A run that fell due while NetPI was not running still starts if it is at most this late (0–720 h); a later one is recorded as `missed`. Never more than one catch-up run. The grace is never under one minute: the timer loop wakes up to a minute late, and that is not downtime |
| `schedules.minIntervalMinutes` | `5` | The smallest `minutes` an `every` cadence may have (1–1440) |
| `schedules.maxRunsPerDay` | `48` | Runs one schedule may start in any 24 hours (1–1440); past it, a run is skipped |

## Webhooks

| key | default | meaning |
|---|---|---|
| `webhooks.enabled` | `false` | Run the webhook receiver (`netpi.webhooks`). It is a separate small listener — NetPI's own API stays on loopback with its per-run token — that serves only `POST /hook/{id}` and `GET /health`. Off: nothing listens |
| `webhooks.bind` | `127.0.0.1` | The address the receiver listens on: `127.0.0.1` (this PC only), this PC's LAN address, or `0.0.0.0`. Anything but loopback needs a firewall rule for the port |
| `webhooks.port` | `7432` | The receiver's port (1–65535); a busy port is retried for 10 s (a hot reload), then reported by `webhooks.status` |
| `webhooks.allowFrom` | `["127.0.0.1/32","::1/128"]` | Addresses or CIDR ranges that may call the receiver; anything else is answered 403 before the token is looked at |
| `webhooks.maxBodyKb` | `256` | Largest payload (1–1024 KB); bigger is answered 413 |

A webhook's own settings (incident key and window, the safety fuse `maxChatsPerHour`) are per webhook, in the Webhooks tab. Twenty wrong tokens from one address in ten
minutes lock that address out (429) for the rest of the window.

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
at `compaction.effort` (low) from a transcript that includes the model's thinking (tool results cut to 2000
characters). An earlier summary is merged by rule (keep everything, move finished work to Done, update the next steps).
When the kept part starts inside one long turn, the start of that turn gets its own summary; what came before it is
summarized only if it holds conversation, not just notices (their plugins announce them again after a compaction, and
nothing but notices to summarize is nothing to compact). The two share one and a half summary budgets by their sizes
(each at least half a budget and at most a whole one), and a turn with nothing before it, the usual shape of a long
agentic run, gets the whole budget. The files read and modified are listed from the tool calls themselves
(`<read-files>`, `<modified-files>`) and carried from summary to summary. A summary cut off at its output limit is
asked again once with up to twice the room; cut off again, it is not used: the chat stays as it was, and auto-compaction
waits five model calls before it tries again (sooner when the context grows by 5 % of the window), since the same input
fails the same way. Beyond pi: a transcript too long for the summarizer is summarized in rolling parts, and
`compaction.model` can summarize with another model. A *previous* summary that the summarizer in use could not have
written — it was made by a bigger model, or the setting changed — is condensed in its own calls first; that costs extra
summarizer calls and shortens the result, so a smaller `compaction.model` shows up as more calls and a terser summary.

| key | default | |
|---|---|---|
| `compaction.enabled` | `true` | a model that keeps its own context (`managesOwnContext`, the Claude CLI) is never auto-compacted: summarizing NetPI's copy of the messages would not shrink what its backend holds |
| `compaction.reserveTokens` | `16384` | compact when fewer tokens than this are left (at most half the window) |
| `compaction.thresholdPercent` | `1` | also compact at this share of the window (1 = only by the reserve) |
| `compaction.keepRecentTokens` | `20000` | recent messages kept verbatim |
| `compaction.model` | – | summarizer model ref (default: the session's model) |
| `compaction.effort` | `low` | the summarizer's reasoning effort (`none`, `low`, `medium`, `high`, or `chat` for the chat's own); an effort the model does not offer means its default. Its thinking counts against the summary's budget, which at a coding chat's effort a long turn used up |
| `compaction.maxSummaryTokens` | 80 % of the reserve (`13107`) | output budget of a summary, thinking included (at most a quarter of the window). A summary cut off at it is asked again once with up to twice the room (within the window and the model's output cap) before the compaction fails |
| `compaction.defaultContextWindow` | `131072` | for models without a known window |
| `nudge.maxPerRun` | `3` | "continue" when a turn ends empty, cut off, or announces an action without doing it. A cut-off that is only thinking gets "stop deliberating, take the smallest next step" instead of "continue where you left off" (which meant more thinking), and one cut off while writing a tool call as text gets "it did not run: make it smaller, write a large file in parts". `maxPerRun` counts *consecutive* nudges: any acceptable response (a tool call, a final answer, an aborted or errored turn) resets it, so the cap bounds one stall episode rather than the whole run |
| `loops.maxHintsPerRun` | `3` | a `loop` notice (a hint, nothing is stopped) before the next model call when the agent is about to repeat itself: the same call after `loops.repeats` − 1 identical results (the whole result text, durations and times aside, and its images: two screenshots or page snapshots that differ anywhere are progress), the same failing call retried, or two steps that undo each other (A, B, A, B); one hint per loop. Subagents too |
| `loops.repeats` | `3` | the call about to run counts (2–10) |
| `memory.enabled` | `true` | with an embedding model (`embed.model`) every chat is embedded in the background (a sweep every 5 minutes) and `memory_search` finds earlier chats by meaning; the text goes only to the embedding server |
| `memory.maxChunksPerChat` | `24` | pieces of ~1,200 characters a chat is embedded as (4–200); the start, the summaries and the end are kept first |
| (none: the plugin's own on/off switch) | | execute tool calls a model wrote as text (`<tool_call>…`), but only when the message is nothing but the call: an answer that also explains, documents or quotes the markup stays text, and the nudge asks for a real call |
| `retry.maxAttempts` | `6` | retries what the provider calls transient — lost connections, stalled streams, rate limits and overload (429/529) and 5xx/server-error responses — up to this many attempts, with a notice in the chat each time; after that the error surfaces unchanged, with its request id |
| `retry.baseDelayMs` / `retry.maxDelayMs` | `1000` / `30000` | exponential backoff with jitter; when the server says how long to wait (`Retry-After`, e.g. with a 429 or 529), at least that long |
| `retry.firstEventTimeoutSeconds` | `600` | silence before the first token (slow prefill) |
| `retry.stallTimeoutSeconds` | `180` | silence between tokens; a model can override it for its own calls with `ModelInfo.stallTimeoutSeconds` (the Claude CLI ships 1800 s, since its own tools and subagents can be silent for minutes) |
| `retry.maxTotalSeconds` | `300` | give up once this much of the waiting between attempts (the backoff and any Retry-After) has passed: the time an attempt ran or stalled does not count against it, so a full first-token stall still gets its retry whatever this is set to |

## Tools

| key | default | |
|---|---|---|
| `files.newFileEol` | `lf` | `lf`, `crlf` or `auto` (CRLF on Windows) for new files; existing files keep their style |
| `shell.bashPath` | auto | Git Bash on Windows (`C:\Program Files\Git\bin\bash.exe`, …; never WSL's bash) |
| `shell.pwshPath` | auto | `pwsh` (PowerShell 7), falls back to Windows PowerShell |
| `shell.pwshAlways` | `false` | offer `pwsh` even when no PowerShell was found |
| `shell.timeoutSeconds` | `120` | default per command (max 1800) |
| `shell.idleMinutes` | `5` | a running command that has printed nothing this long raises `process.idle` once per silence (0 = off) |
| `shell.trackChanges` | `true` | compare `git status` before and after a foreground command that may write, and report the files it changed in its repository (and a script edit that changed nothing); see TOOLS.md, `bash` / `pwsh` |
| `shell.hideEnv` | `[]` | environment variables a `bash`/`pwsh` command never sees, on top of what is always hidden: `NETPI_TOKEN`, `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`, `BRAVE_API_KEY`, every variable a secret setting reads (`env:NAME` under `providers`/`mcp`, an MCP server's `env`/`headerEnv` sources); see TOOLS.md, `bash` Environment |
| `ideas.tellAgentOnCommit` | `true` | after the agent itself runs a successful `git commit`/`git merge` in the session's project, one notice asks it to mark the idea that commit finished. Silent when the project has no open idea. With more than three of them the notice names none and asks for nothing (a backlog that size says nothing about this commit), unless embeddings are on (`ideas.semantic`): then it names the three open ideas closest in meaning to the commit message, as candidates. The cards above stay: they catch a commit made outside any chat |
| `ideas.commitNoticesPerRun` | `2` | how many of those notices one run may get (0–10), so a run that commits in a loop is asked a bounded number of times |
| `ideas.semantic` | `true` | with embeddings (`embed.model`): the backlog is embedded in the background, the commit notice names the open ideas nearest a commit, the `ideas` tool lists the closest ideas when no idea holds every word of a `query`, and adding an idea names the existing ones it resembles. Off, or without embeddings, all of it behaves as before |
| `ideas.noticeFloor` | `0.6` | the commit notice names an open idea only when it is at least this close to the commit message |
| `ideas.similarThreshold` | `0.80` | cosine (card to card) above which an existing idea is said to resemble a new one (0.5–0.99). 0.80 is about the top 7 % of each idea's nearest other idea in a 231-idea backlog with bge-base; the known duplicate pair scored 0.807-0.824 |
| `ideas.commitNoticeDebounceSec` | `30` | seconds a run waits after a commit notice before it may get the next one (0–3600), so a burst of commits costs one notice instead of one each; 0 turns the debounce off and leaves only the per-run cap |

## Workspaces

A workspace is the checkout a session works in — a root, a branch, a starting commit, an owner (`docs/PROTOCOL.md` for the RPCs and events). The `netpi.workspaces` plugin owns them: it registers `workspaces.*`, `sessions.setWorkspace` and the `workspace.*` / `session.workspace` events, keeps its own collection of workspaces, and writes the binding to the session as `meta.workspaceId` with `meta.cwd` (the folder the chat then runs in). Isolation is at the checkout level, deciding which of a repository's checkouts a worker's files land in: **it is not an OS sandbox** (a shell command is never parsed, and code a worker runs keeps the user's privileges). A project that is not a git repository gets a plain folder, and is otherwise unaffected by these settings.

| key | default | |
|---|---|---|
| `workspaces.isolateWriters` | `true` | a worker that can write gets its own git worktree and branch instead of sharing the project's checkout: it decides `isolated` for `agent_spawn` and `workspaces.create` when the caller does not say, and it follows the worker's **tools** (a subagent that can only read shares its caller's workspace, because a worktree of its own isolates nothing). `isolated: true` asks for one regardless, `isolated: false` asks to share |
| `workspaces.branchPrefix` | `"netpi/"` | the branch name of a worktree NetPI creates is this prefix plus the workspace's name (a taken name gets a `-2`, `-3`, … suffix, so nobody's existing branch is reused) |
| `workspaces.worktreeRoot` | `""` | where worktrees are created: **inside** the project, in its `.worktrees` folder (`<project>/.worktrees/<name>`, always the repository's main checkout even when the project path is a linked worktree) when empty, otherwise this folder (a relative path is taken from the project). A root inside the repository is kept out of `git status` through the clone's `.git/info/exclude` — local, so a project this plugin manages never gains a committed ignore line because a worker existed (a project that lists `/.worktrees/` in its own `.gitignore` needs nothing here) — and `workspaces.delete` removes the checkout with `git worktree remove` |

## Guardrails

`plugins/NetPI.Guardrails` checks every tool call before it runs: patterns and paths only (no model call, no change to
the prompt), so it costs microseconds. A matching rule blocks the call (the model reads why; nothing ran); a rule that
starts with `ask:` asks you first on the tool's row in the chat (No / Allow in this chat / Allow) while the run waits with its
instance given back ("Allow in this chat" stops that rule asking for the rest of the chat; other chats still ask); in a subagent an ask rule blocks, since nobody watches its chat. `block:` may start a rule, `#` a comment line. The
checks catch the plain cases (a command built at run time gets through): they are not a sandbox.

| key | default | |
|---|---|---|
| `guardrails.enabled` | `true` | check tool calls against the rules below |
| `guardrails.commands` | catastrophic commands (below) | regular expressions, tried on each part of a `bash`, `pwsh` or `ssh` `run` command, and of the program and arguments a `windows` `open` starts (split at new lines, `;`, `&&`, `\|\|`, `\|`, `&`, but not the `&` of a redirection such as `2>&1`; a backslash at the end of a line continues it), ignoring case. A part is tried as written and with what a shell ignores taken off (a trailing `# comment`, redirections, quotes, and wrappers: `sudo` and its options, `env`, `command`, `time`, `nohup`, `exec`, `if`, `then`, `do`), so `rm -rf / 2>/dev/null` and `sudo -n rm -rf "$HOME"` match like `rm -rf /`. A rule that takes longer than 250 ms on a part counts as matching it |
| `guardrails.paths` | `["ask: ~/.netpi", "~/.ssh"]` | files and folders the agent may not change: `write` and `edit` refuse them, so does an `ssh` download (`copy`, direction `download`) whose destination is under one, and so do `bash` and `pwsh` commands that name them, in any spelling a shell uses (`~/.netpi`, `$HOME/.netpi`, `%USERPROFILE%\.netpi`, `$env:USERPROFILE\.netpi`, `C:\Users\me\.netpi`, `/c/Users/me/.netpi`); the `read` tool still reads them. `~` is your home |
| `guardrails.maxSleepSeconds` | `30` | the longest a `bash`, `pwsh` or `ssh` `run` command may wait on purpose (0 switches it off). A pattern cannot say "longer than N seconds" (a rule for `^sleep\s` takes `sleep 1` with it), so this one is parsed and compared: `sleep 300` with its GNU suffixes (`1m30s`, `500ms`) and decimals, `Start-Sleep -Seconds 300` / `-Milliseconds` / `-Timeout 00:05:00`, `ping -n 300` and `ping -c 300`, `timeout 400 sleep 300` (the timeout bounds it, so `timeout 5 sleep 300` is not a long wait), and the script a shell runs with `bash -c "sleep 300"` or `pwsh -Command "Start-Sleep 300"`. Only a wait in command position counts: the `sleep 300` an `echo`, a `printf` or a `git commit -m` quotes is data, and so is the tool's own `timeout` (that is a wait, not a sleep) — a heredoc body is a line of its own once the command is split at its newlines, so it does read as a command |
| `guardrails.maxSleepAction` | `"block"` | `block`: a wait over the limit does not run and the model reads why (run the work itself with a matching `timeout`, or `background: true` and `process wait`). `ask`: you are asked on the tool's row in the chat, and "Allow in this chat" remembers it for that chat. A second opinion (`guardrails.secondOpinion`) never clears one |
| `guardrails.secondOpinion` | `false` | before an `ask:` rule asks you about a `bash`, `pwsh` or `ssh` `run` command, a decision model reads it (`decide.ask`, the Decide plugin through AiGateway); one it finds harmless runs without asking (event `guard.cleared`), the rest ask as before with the model's view on the card. Blocking rules, protected paths and a long wait are never relaxed; no answer (no Decide plugin, the model not loaded, 15 s) means you are asked. In a subagent a cleared call runs; one that is not cleared is blocked as before |
| `guardrails.secondOpinionModel` | `qwen3.8-27b` | the decision model; `qwen3.8-27b` (about 0.3 s) was measured on 852 real commands (decisions-lab's docs/DECISION-MODELS.md, "NInfer baselines" 0.4); `kev-9b` calls too many local commands remote |
| `guardrails.secondOpinionThreshold` | `0.2` | a command runs without asking only when p(destructive), p(stops a process) and p(changes a remote) are each below it (0.01–0.5); p(read-only) is shown on the card but not required (the model underrates builds and test runs). At 0.3 a hub quit was cleared in the hand-labelled set, at 0.2 no risky command |

The default commands: `rm -r` of `/`, `/*`, `~` or `$HOME`; deleting a drive root (`rm`, `rd`, `del`, `Remove-Item` of
`C:\`, `/c`); `mkfs`; `dd … of=/dev/…` (not `/dev/null`); `format C:`; a fork bomb. These are blocked. Asked first (`ask:`), since the user may well want them done: `shutdown`, `reboot`, `poweroff`, `halt`, `systemctl reboot|poweroff|halt`,
`Stop-Computer`, `Restart-Computer`, and `powershell` / `powershell.exe` (Windows PowerShell 5.1: use `pwsh`, PowerShell 7). Each is anchored at the start of a command part, so `grep shutdown` or
`echo 'rm -rf /'` pass. A deliberate long wait is a rule of its own (`guardrails.maxSleepSeconds`), because only a parsed
number tells a five-minute sleep from a one-second one.

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
| `decide.baseUrl` | – | server for `decide` (`POST /v1/systemone`); empty = the `baseUrl` of the first `aiproxy` provider (else `http://127.0.0.1:8090`) |
| `decide.model` | `qwen3.8-27b` | decision model `decide` asks: `qwen3.8-27b` on NInfer (through AiGateway's System One bridge; the best without training, no extra memory, shares the 5090 with the agents), or `kev-9b`/`kev-4b` on the nuc (load it from AiHub first; it takes the whole 4070). See decisions-lab's docs/DECISION-MODELS.md |
| `decide.maxItems` | `500` | most items per `decide` call (1–5000) |
| `decide.parallel` | `4` | `decide` requests at once (1–16) |
| `decide.groupSimilarity` | `0.985` | with embeddings, `decide` items at or above this cosine share one decision (a log line that differs only in a timestamp); identical items always do; 0 = only identical ones. Kept high on purpose: lines that differ in an error code must stay apart |
| `decide.lane` | `true` | a single short check on a local model (the guard's second opinion, the idea checks, loop and todo hints) does not take an agent slot: NInfer answers decisions on its own lane ahead of agent work (decisions first). Off: every decision waits for a slot like a chat. The `decide` tool's batches always take slots |
| `embed.baseUrl` | – | an OpenAI-compatible `POST /v1/embeddings` server for the Embeddings plugin; empty = the `baseUrl` of the first `aiproxy` provider (else `http://127.0.0.1:8090`). The owner's: `http://192.168.1.3:8012` (the nuc's `embed-tasks`) |
| `embed.model` | – | the embedding model; **empty switches embeddings off** (every consumer then behaves as without them). `bge-base-en-v1.5` was measured (decisions-lab `nuc-plan.md`) |
| `embed.queryPrefix` | – | put in front of search texts only, never stored documents (bge: `Represent this sentence for searching relevant passages: `) |
| `embed.timeoutMs` | `1500` | how long an interactive embedding call waits (100–60000); indexing in the background waits up to 30 s per batch. A timeout or failure backs off (5 s, doubling, at most 5 min), and calls fail at once meanwhile |
| `embed.batchSize` | `64` | texts per request (1–256) |
| `web.browserPath` | auto | Edge, Chrome or Chromium for `browser` (found in the usual install folders or on PATH) |
| `browser.target` | `own` | Where a chat's `browser` tab opens: `own`, the agents' browser (headless; the user sees it, and logs in, in the chat's Browser view), or `chrome`, the user's running Chrome — through the NetPI extension when it is connected, else through remote debugging allowed at `chrome://inspect/#remote-debugging`. A call can ask for the other with `browser` |
| `browser.chromeUserData` | Chrome's default | The folder holding the user's Chrome `DevToolsActivePort` (`%LOCALAPPDATA%\Google\Chrome\User Data`, `~/Library/Application Support/Google/Chrome`, `~/.config/google-chrome`) |
| `browser.headless` | `true` | `false`: the agents' browser opens a window of its own. The chat's Browser view shows its tab either way. Applies when the browser next starts |
| `browser.profile` | `default` | A name: logins and cookies are kept in `<home>/browser/<name>`. `temp`: a fresh profile each time the browser starts, deleted when it closes |
| `browser.idleMinutes` | `10` | The agents' browser closes after this many minutes without a `browser` call and with no Browser view open (1–1440); the chats' tabs close with it. The connection to the user's Chrome over its DevTools port is kept while a chat has a tab there (Chrome asks again for every new connection) |
| `browser.maxControls` | `200` | Controls listed per page, the ones nearest the visible part (50–5000); `find`, and `snapshot` with `all`, reach the rest |
| `windows.maxControls` | `300` | Controls listed per window by the `windows` tool, the ones nearest the visible part (50–5000); `find`, and `snapshot` with `all`, reach the rest |
| `windows.journal` | `true` | Every `windows` step (the action, its arguments, the result) as a JSON line in `<home>/windows/journal-yyyyMMdd.jsonl`: data to train a smaller control picker on |

## Goals

| key | default | |
|---|---|---|
| `goal.maxContinuations` | `100` | automatic runs before the goal pauses (resume allows as many again) |
| `goal.noProgressLimit` | `3` | automatic runs in a row without a successful tool call before the goal pauses |
| `goal.tokenBudget` | `0` | token budget for goals set without one (0 = none); input not read from the cache plus output |
| `goal.review` | `check` | `check`: before `goal_update complete` stands, one model call that did not do the work judges the goal against the run's evidence and refuses it with the gaps it finds (a second refusal blocks the goal for the user); also refused while the chat's own subagents still work. `off`: the model's word is enough. See TOOLS.md, Goal |
| `goal.whileSubagentsRun` | `continue` | `continue`: a goal run that ends while the chat's subagents work is continued at once, naming them (a continuation that only talks then waits for their reports). `wait`: their reports start the next run |

## Plan mode

`plugins/NetPI.Plan` (see `docs/TOOLS.md`, "Plan mode").

| key | default | |
|---|---|---|
| `plan.mcpAllow` | `[]` | MCP tools a plan-mode chat may call besides the ones their server declares read-only (`annotations.readOnlyHint`, or the server's own `readOnly` list). Names with `*` as wildcard, matched against the tool's own name or `server/name`, ignoring case: `ha_get_*`, `ha_search`, `ha_list_*`. A tool that changes things stays blocked |
| `plan.subagentsReadOnly` | `true` | the subagents a plan-mode chat starts get only tools that read, no checkout of their own, and are bound by plan mode too |
| `plan.seedTodos` | `true` | approving a plan in the same chat puts its steps in the chat's todo list (unless the list has open items) |

## SSH tools

| key | default | |
|---|---|---|
| `ssh.path` | auto | the ssh client: Windows OpenSSH (`System32\OpenSSH\ssh.exe`), else Git's, else `ssh` on PATH |
| `ssh.scpPath` | next to `ssh.path` | the scp client |
| `ssh.config` | `~/.ssh/config` | where the host aliases come from; any other file is also passed to ssh and scp (`-F`) |
| `ssh.connectTimeoutSeconds` | `10` | (2–120) |
| `ssh.timeoutSeconds` | `120` | default `ssh` `run` timeout (max 1800) |
| `ssh.reuseConnections` | `true` | where the client cannot multiplex (Windows), keep connections to a host open between calls (below) |
| `ssh.connectionsPerHost` | `3` | kept connections per host (1–8): calls running at the same time; one more opens its own ssh |
| `ssh.idleSeconds` | `300` | a kept connection unused this long is closed (10–3600) |

Where the client supports it, calls to one host ride a single master connection (OpenSSH `ControlMaster`, 5 minutes
after the last call): the first call of a run pays the TCP + transport + auth handshake, the rest reuse the socket. The
control sockets live in `~/.netpi/ssh/` (one per host and user, hashed), so they don't touch the user's own folder.

**The client is asked, not assumed** — an option it does not know is a hard error, and it may not multiplex at all:

- The idle timeout is only passed to a client that accepts it. OpenSSH_for_Windows (NetPI's own client on Windows) has
  no `ControlIdleTimeout`, and asking for it fails every call with `Bad configuration option`.
- A client that cannot multiplex gets no master connection: OpenSSH_for_Windows accepts `ControlMaster` and then fails
  every session with `getsockname failed: Not a socket`. This is the normal case on Windows, and there the plugin keeps
  connections itself instead (`ssh.reuseConnections`): one long-lived ssh per host runs a small `sh` loop that takes one
  call at a time (the command base64-encoded, its stdin as counted bytes saved to a temp file first, output framed by a
  per-call random start line and end marker with the exit code), up to `ssh.connectionsPerHost` side by side. A handshake costs ~300 ms to
  a LAN host and a call over a kept connection a few ms (measured 2026-10-03). A timeout or abort kills that connection,
  as killing a per-call ssh did; a host whose kept connection will not start (auth, host key, no POSIX `sh`, `base64`
  or count-exact `head -c`) runs its calls the old way for 10 minutes, so ssh's own errors read as before. A job a call
  leaves running still writes to the connection's output: what it prints outside a call (before the next call's start line,
  after a call's end) is dropped and the connection is given up, and a command with a lone `&` gives up its connection
  after the call, so a later call never carries another's output (idea-ic4fr9).

Both questions are answered once per client, offline (`ssh -o … -G localhost` and `ssh -O check`), and take a few
milliseconds; anything unexpected answers "not supported", so a call never fails because of the check itself.

## MCP servers

`mcp.servers` is an object keyed by stable server id. Each entry accepts `enabled`, `transport` (stdio/http), `command`, `args`, absolute `cwd`, `url`, `env`, `headerEnv`, `tools`, `resources`, `pinned`, `readOnly`, `connectTimeoutMs` (5000: connecting and the first handshake) and `callTimeoutMs` (60000: a tool call, and a catalog refresh — listing 77 tools and their resources can take longer than a connect). env/headerEnv values name source environment variables, resolved against the current user/machine registry environment on Windows first, then NetPI's own environment (a variable set or changed after start is picked up without a restart). tools/pinned/readOnly contain raw remote tool names; resources holds resource URIs (exact, or a prefix ending in `*`). Details and examples: [PLUGIN-MCP.md](PLUGIN-MCP.md).

| Setting | Default | Purpose |
| --- | ---: | --- |
| `mcp.maxMessageChars` | 4000000 | Protocol frame/SSE event limit |
| `mcp.maxCatalogTools` | 10000 | Tools per server |
| `mcp.maxCatalogChars` | 8000000 | Catalog data per server |
| `mcp.maxResourceEntries` | 2000 | Resources per server |
| `mcp.maxResourceCatalogChars` | 2000000 | Resource catalog data per server |
| `mcp.maxResourceChars` | 20000 | Characters per resource read; a larger one is written to a file |
| `mcp.maxBinaryBytes` | 2000000 | Decoded image limit |
| `mcp.refreshSeconds` | 60 | Polling fallback |

## Code mode

The `codemode` tool's scripts (docs/TOOLS.md).

| key | default | |
|---|---|---|
| `codemode.timeoutSeconds` | `600` | a script still running after this is stopped (a call can ask for its own `timeoutSeconds`, at most 3600) |
| `codemode.memoryMb` | `256` | what one script may allocate |

## Language servers

The `lsp` tool's servers (docs/TOOLS.md).

| key | default | |
|---|---|---|
| `lsp.maxServers` | `3` | language servers running at once (one per language and workspace root); starting one more stops the least recently used. Each holds a project in memory |
| `lsp.idleMinutes` | `15` | a server nobody asked for this long is stopped; the next request starts it again |
