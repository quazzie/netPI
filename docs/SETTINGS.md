# Settings reference

All settings live in `~/.netpi/settings.json` (Windows: `%USERPROFILE%\.netpi\settings.json`; override the folder
with `NETPI_HOME`). The file is created with sensible defaults on first start, accepts `//` comments and trailing
commas, and is watched: edits apply live (the ⚙ settings dialog in the app edits the same file). Keys are shown as
dotted paths — `providers.aiproxy.baseUrl` means `{ "providers": { "aiproxy": { "baseUrl": … } } }`.

## Core

| key | default | |
|---|---|---|
| `defaultModel` | `aiproxy/qwen3.8-27b` | model ref (`provider/model`) for sessions that have none |
| `workspace.default` | `~/.netpi/workspace` | working directory of sessions without a project |
| `server.port` | `7431` | a random free port is used when it is taken |
| `server.devOrigins` | `[]` | extra allowed origins (e.g. `http://localhost:5173` for `npm run dev`) |
| `plugins.disabled` | `[]` | plugin ids not to load |
| `plugins.enabled` | `[]` | turn on plugins whose `plugin.json` says `enabled: false` |
| `plugins.dirs` | `[]` | extra plugin folders (besides `<app>/plugins` and `~/.netpi/plugins`) |
| `tools.disabled` | `[]` | tool names hidden from agents (e.g. `["pwsh", "idea_remove"]`) |
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
| `enabled`, `local` | `true` | local providers take lane capacity from the catalog's `concurrency` |
| `models.<id>` | – | per model: `transport`, `replayReasoning`, `parseThinkTags`, `maxOutputTokens`, `contextWindow`, `concurrency`, `displayName`, `hidden` |

### `providers.openaiCompatible` (extra endpoints)

An array of `{ "id", "name", "baseUrl", "apiKey"?, "transport"?, "local"?, "headers"? }` plus any of the keys above.
Defaults differ: transport `chat`, `local` only for loopback URLs. Example: LM Studio, a remote llama.cpp, OpenRouter.

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
| `agent.maxToolResultChars` | `60000` | longer tool output is cut (head + tail kept) |
| `agent.parallelReadOnlyTools` | `true` | run several read-only calls of one turn concurrently |
| `agents.maxDepth` | `3` | subagent nesting depth (deeper agents get no orchestration tools) |

## Lanes

```jsonc
"lanes": {
  "localDefaultCapacity": 1,      // local models without a catalog concurrency
  "cloudDefaultCapacity": 4,      // one pool per cloud provider (e.g. "anthropic")
  "pools": {
    // explicit pools: glob-matched model refs share one set of lanes
    "gpu": { "capacity": 2, "models": ["aiproxy/qwen3.8-27b", "aiproxy/gemma-*"] },
    // an entry without "models" only overrides that pool's capacity
    "anthropic": { "capacity": 2 }
  },
  "budgets": { "anthropic": { "dailyTokens": 2000000 } }   // input + output + cache writes per day
}
```

## Context and AGENTS.md

| key | default | |
|---|---|---|
| `context.customPrompt` | – | replaces the identity section of the system prompt |
| `context.appendPrompt` | – | appended at the end |
| `context.toolDescriptions` | `false` | also describe every tool in the system prompt (they are always in the tool schemas) |
| `agentsMd.fileNames` | `["AGENTS.md", "CLAUDE.md"]` | first match per directory, from the file system root down to the working directory |
| `agentsMd.extraFiles` | `[]` | always included |

The global file is `~/.netpi/AGENTS.md`.

## Auto-compaction, nudge, retry, tool repair

| key | default | |
|---|---|---|
| `compaction.enabled` | `true` | |
| `compaction.thresholdPercent` | `0.8` | compact when the context passes this share of the window |
| `compaction.reserveTokens` | `16384` | …or when fewer tokens than this are left |
| `compaction.keepRecentTokens` | `20000` | recent messages kept verbatim |
| `compaction.model` | – | summarizer model ref (default: the session's model) |
| `compaction.maxSummaryTokens` | `8192` | |
| `compaction.defaultContextWindow` | `131072` | for models without a known window |
| `nudge.enabled` / `nudge.maxPerRun` | `true` / `3` | "continue" when a turn ends empty, cut off, or announces an action without doing it |
| `toolRepair.enabled` | `true` | execute tool calls a model wrote as text (`<tool_call>…`) |
| `retry.enabled` / `retry.maxAttempts` | `true` / `6` | retries lost connections and stalled streams |
| `retry.baseDelayMs` / `retry.maxDelayMs` | `1000` / `30000` | exponential backoff with jitter |
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
| `ideas.fileName` | `ideas.json` | in the project folder (sessions without project: `~/.netpi/ideas.json`) |
