# Settings reference

All settings live in `~/.netpi/settings.json` (Windows: `%USERPROFILE%\.netpi\settings.json`; override the folder
with `NETPI_HOME`). The file is created with sensible defaults on first start, accepts `//` comments and trailing
commas, and is watched: edits apply live. Keys are shown as dotted paths — `providers.aiproxy.baseUrl` means
`{ "providers": { "aiproxy": { "baseUrl": … } } }`.

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
| `tools.disabled` | `[]` | tool names hidden from every chat (e.g. `["pwsh"]`); only in this file: the dialog switches whole plugins, and each chat switches its own tools (the composer's tools button, `meta.toolsOff`) |
| `logging.level` | `Information` | host log level (`~/.netpi/logs/netpi-YYYYMMDD.log`) |
| `database.sqlitePath` | – | explicit SQLite library (default: `winsqlite3.dll` on Windows, `libsqlite3` elsewhere; env `NETPI_SQLITE`) |

## Providers

### `providers.aiproxy` (OpenAI-compatible, default transport: Responses API)

| key | default | |
|---|---|---|
| `baseUrl` | `http://127.0.0.1:8090` | a trailing `/v1` is fine |
| `transport` | `responses` | `responses` (`/v1/responses`) or `chat` (`/v1/chat/completions`) |
| `apiKey` | – | sent as Bearer; `"env:NAME"` / `"$NAME"` reads an environment variable |
| `replayReasoning` | Responses: `true`, Chat: `false` | send previous reasoning back. Standard stateless Responses usage appends every output item of a response (reasoning → message → function calls) to the next input |
| `includeEncryptedReasoning` | `false` | also request `include: ["reasoning.encrypted_content"]` (OpenAI-hosted reasoning models; nInfer rejects non-empty `include` with HTTP 400) |
| `dumpFailedRequests` | `true` | save the request body of failed calls to `~/.netpi/logs/failed-requests/` (newest 30) and add the server's `x-request-id` / response id to the error, for reproducing backend bugs |
| `parseThinkTags` | `true` | split inline `<think>…</think>` into thinking blocks |
| `defaultMaxOutputTokens` | `16384` | when the catalog says `max_output_tokens: null` |
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
| `maxOutputTokens` | `32768` | cap for `max_tokens` (catalogs report up to 500k+; reasoning counts against it) |
| `provider` | – | routing preferences sent as the request's `provider` object, e.g. `{ "sort": "throughput" }` |
| `promptCaching` | `true` | top-level `cache_control` for `anthropic/*` models (other providers cache automatically) |
| `replayReasoning` | `true` | send `reasoning_details` back |
| `parseThinkTags` | `true` | split inline `<think>…</think>` into thinking blocks |
| `headers` | – | extra HTTP headers, e.g. `HTTP-Referer` + `X-OpenRouter-Title` for OpenRouter's app attribution (creates a public app page) |
| `dumpFailedRequests` | `true` | save failed request bodies to `~/.netpi/logs/failed-requests/`; errors carry the generation id |
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
| `thinkingBudgets` | low 2048, medium 8192, high 16384, max 32000 | tokens per effort |
| `adaptiveEffort` | `true` | send `output_config.effort` with adaptive thinking |
| `promptCaching` | `true` | cache_control on system prompt, tools and the rolling last message |
| `defaultMaxOutputTokens` | `32000` | |
| `betas` | `[]` | `anthropic-beta` header values |
| `fallbackModels` | built-in list | used when `/v1/models` can't be listed |
| `modelsCacheSeconds` | `600` | |

## Agents

| key | default | |
|---|---|---|
| `agent.maxTurns` | `200` | model calls per run |
| `agent.defaultMaxOutputTokens` | `16384` | when a model has no limit |
| `agent.maxToolResultChars` | `20000` | a longer tool result is saved to a file: the agent gets its start and end and the path, to read the rest in parts or grep it (`read` pages, the bash and `ssh_run` tails stay under it) |
| `agent.parallelReadOnlyTools` | `true` | run several read-only calls of one turn concurrently |
| `agents.maxDepth` | `3` | subagent nesting depth (deeper agents get no orchestration tools) |

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
- **Instances:** default the model's slots for a local model (its `concurrency`), 1 for a cloud one. Agents on one local
  model share its slots: NetPI never runs more on the model than it serves (the agent dialog warns when their
  instances add up to more).
- **A chat without an agent** takes an agent on its model (a free one first) and keeps it; with no agent on its model it
  stops with a notice. New chats start on the agent chosen last.
- **With no agents at all** (a settings file without any) every model call gets a slot per model:
  `models.localSlots` (1, when the catalog doesn't say) per local model, `models.cloudSlots` (4) per
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

Every model call is recorded with its tokens and cost (`usage_calls`: agents, compaction, anything that asks a model).
The cost is what the provider reported (OpenRouter returns it for every call), else tokens × the price (the agent's
`cost`, else the catalog's pricing), and $0 for local models. A cloud model without a known price counts $0 but is
treated as paid when a budget is spent. The budget and the agents' daily caps (`agents.<id>.budget.limitUsd`) only
stop paid models; free and local ones always run.

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

## Auto-compaction, nudge, retry, tool repair

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
`compaction.model` can summarize with another model.

| key | default | |
|---|---|---|
| `compaction.enabled` | `true` | |
| `compaction.reserveTokens` | `16384` | compact when fewer tokens than this are left (at most half the window) |
| `compaction.thresholdPercent` | `1` | also compact at this share of the window (1 = only by the reserve) |
| `compaction.keepRecentTokens` | `20000` | recent messages kept verbatim |
| `compaction.model` | – | summarizer model ref (default: the session's model); it summarizes at the chat's reasoning effort when it offers it |
| `compaction.maxSummaryTokens` | 80 % of the reserve (`13107`) | output budget of a summary, thinking included (at most a quarter of the window) |
| `compaction.defaultContextWindow` | `131072` | for models without a known window |
| `nudge.enabled` / `nudge.maxPerRun` | `true` / `3` | "continue" when a turn ends empty, cut off, or announces an action without doing it |
| `toolRepair.enabled` | `true` | execute tool calls a model wrote as text (`<tool_call>…`) |
| `retry.enabled` / `retry.maxAttempts` | `true` / `6` | retries lost connections and stalled streams |
| `retry.baseDelayMs` / `retry.maxDelayMs` | `1000` / `30000` | exponential backoff with jitter; when the server says how long to wait (`Retry-After`, e.g. with a 429 or 529), at least that long |
| `retry.firstEventTimeoutSeconds` | `600` | silence before the first token (slow prefill) |
| `retry.stallTimeoutSeconds` | `180` | silence between tokens |
| `retry.maxTotalSeconds` | `300` | give up after this long (AiProxy may hold each attempt 2 min) |

## Tools

| key | default | |
|---|---|---|
| `files.newFileEol` | `lf` | `lf`, `crlf` or `auto` (CRLF on Windows) for new files; existing files keep their style |
| `shell.bashPath` | auto | Git Bash on Windows (`C:\Program Files\Git\bin\bash.exe`, …; never WSL's bash) |
| `shell.pwshPath` | auto | `pwsh` (PowerShell 7), falls back to Windows PowerShell |
| `shell.pwshAlways` | `false` | offer `pwsh` even when no PowerShell was found |
| `shell.timeoutSeconds` | `120` | default per command (max 1800) |
| `ideas.fileName` | `ideas.json` | in the project's `.netpi` folder (sessions without project: `~/.netpi/ideas.json`) |

## Guardrails

`plugins/NetPI.Guardrails` checks every tool call before it runs: patterns and paths only (no model call, no change to
the prompt), so it costs microseconds. A matching rule blocks the call (the model reads why; nothing ran); a rule that
starts with `ask:` asks you first on the tool's row in the chat (Allow / No) while the run waits with its instance given
back; in a subagent an ask rule blocks, since nobody watches its chat. `block:` may start a rule, `#` a comment line. The
checks catch the plain cases (a command built at run time gets through): they are not a sandbox.

| key | default | |
|---|---|---|
| `guardrails.enabled` | `true` | check tool calls against the rules below |
| `guardrails.commands` | catastrophic commands (below) | regular expressions, tried on each part of a `bash`, `pwsh` or `ssh_run` command (split at new lines, `;`, `&&`, `\|\|`, `\|`, `&`), ignoring case |
| `guardrails.paths` | `["ask: ~/.netpi", "~/.ssh"]` | files and folders the agent may not change: `write` and `edit` refuse them, and so do `bash` and `pwsh` commands that name them, in any spelling a shell uses (`~/.netpi`, `$HOME/.netpi`, `%USERPROFILE%\.netpi`, `$env:USERPROFILE\.netpi`, `C:\Users\me\.netpi`, `/c/Users/me/.netpi`); the `read` tool still reads them. `~` is your home |

The default commands: `rm -r` of `/`, `/*`, `~` or `$HOME`; deleting a drive root (`rm`, `rd`, `del`, `Remove-Item` of
`C:\`, `/c`); `mkfs`; `dd … of=/dev/…` (not `/dev/null`); `format C:`; `shutdown`, `reboot`, `poweroff`, `halt`,
`Stop-Computer`, `Restart-Computer`; a fork bomb. Each is anchored at the start of a command part, so `grep shutdown` or
`echo 'rm -rf /'` pass.

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
| `web.browserPath` | auto | Edge, Chrome or Chromium for `screenshot` (found in the usual install folders or on PATH) |

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
| `ssh.timeoutSeconds` | `120` | default `ssh_run` timeout (max 1800) |
