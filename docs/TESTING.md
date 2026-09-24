# Testing NetPI

Three layers, all without NuGet packages (console runners, no test framework):

| layer | where | what it needs |
|---|---|---|
| unit suites | `tests/NetPI.{Host,Providers,Tools,Agent,Aux}.Tests` | the built projects |
| mock model server | `tests/MockLlm` | nothing (ASP.NET shared framework) |
| end-to-end suite | `tests/NetPI.E2E` | the built app (`artifacts/app`), the mock, and for the UI test Node 22 + Playwright + Chromium |

## Build

`NetPI.slnx` contains the Windows-only desktop shell, so outside Windows build the projects one by one in dependency order
(`BuildProjectReferences=false` keeps the build fast because every dependency was already built by the loop):

```bash
B="dotnet build -nologo -v q -clp:ErrorsOnly -p:BuildProjectReferences=false"
$B src/NetPI.Abstractions/NetPI.Abstractions.csproj
$B src/NetPI.Host/NetPI.Host.csproj
$B src/NetPI.Server/NetPI.Server.csproj
for p in plugins/*/*.csproj tests/*/*.csproj; do $B "$p" || break; done
```

```powershell
$b = 'build','-nologo','-v','q','-clp:ErrorsOnly','-p:BuildProjectReferences=false'
'src/NetPI.Abstractions','src/NetPI.Host','src/NetPI.Server' | % { dotnet @b (Get-ChildItem $_ -Filter *.csproj).FullName }
Get-ChildItem plugins/*/*.csproj, tests/*/*.csproj | % { dotnet @b $_.FullName }
# on Windows `dotnet build NetPI.slnx` also works (it includes src/NetPI.Desktop)
```

Plugins build into `artifacts/app/plugins/<Name>/`, the server into `artifacts/app/`. The web UI and the plugin UI bundles
come from `npm run build` (`artifacts/app/wwwroot`, `plugins/*/wwwroot/ui.js`).

Note: a running NetPI instance started from `artifacts/app` hot-reloads the plugins you rebuild. The E2E suite runs
from a private copy of `artifacts/app`, so it is not affected by (and does not affect) other instances.

## Unit suites

```bash
dotnet tests/NetPI.Providers.Tests/bin/Debug/NetPI.Providers.Tests.dll   # AiProxy (Responses/Chat), Anthropic, OpenRouter against a scripted HTTP mock
dotnet tests/NetPI.Tools.Tests/bin/Debug/NetPI.Tools.Tests.dll           # read/write/edit/grep/find/ls, bash/pwsh, processes, files.open
dotnet tests/NetPI.Agent.Tests/bin/Debug/NetPI.Agent.Tests.dll           # agent loop, steering/queue/abort, subagents, lanes, persistence, context notices
dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll               # retry, nudge, tool repair, compaction, ideas, work, diagnostics, todo, web, media, ssh
tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests                        # kernel: SQLite, settings, bus, registries, sessions, catalog, server, plugins
```

Every runner takes optional name filters (`… NetPI.Agent.Tests.dll abort lanes`) and exits with 0 when all selected
tests pass. `NETPI_TEST_LOGS=1` shows host logs in the Host suite.

The web tool tests serve pages and fake SearXNG / Brave endpoints from a local Kestrel server and never read your pi
config or `BRAVE_API_KEY`. The `screenshot` test drives a real headless Edge/Chrome/Chromium; without one installed it
checks everything except the page screenshots and says so.

The SSH tool tests use a fake launcher (no ssh runs). With `NETPI_SSH_TEST_HOSTS=nuc,server` the `ssh live` test also
runs against those aliases of your `~/.ssh/config` (Linux hosts whose keys are set up): quoting, write/read/edit
(including a 5 MB file), `cwd`, scp both ways, and a timeout and an abort that must end the remote processes. It works
under `/tmp/netpi-ssh-test-*` and removes it.

## Mock model server (`tests/MockLlm`)

A scripted stand-in for AiProxy and the Anthropic API. It streams realistic SSE (reasoning/thinking deltas, text deltas,
function-call argument deltas, usage with cached tokens) and validates requests the way the real APIs do: every
`function_call_output` must answer a `function_call` (Responses), tool messages must follow their `tool_calls` (Chat),
Anthropic roles must alternate, `tool_result` must follow its `tool_use`, replayed thinking needs the signature the mock
issued, a continued tool turn must start with a thinking block when thinking is on, at most 4 `cache_control` blocks,
`max_tokens > budget_tokens`. Reasoning efforts outside the model's `efforts`, `max_tokens` above `max_output_tokens`
and prompts larger than `context_window` are rejected (the last one as a llama.cpp-style `exceed_context_size_error`).

```bash
dotnet tests/MockLlm/bin/Debug/MockLlm.dll [--port 7479] [--speed 1] [--tiny-ctx 12000] [--anthropic-key KEY] [--verbose] [--init-home DIR]
```

| endpoint | |
|---|---|
| `GET /health`, `GET /v1/models`, `GET /v1/models/{id}` | AiProxy catalog (`/v1/models` with an `x-api-key`/`anthropic-version` header answers in Anthropic format) |
| `POST /v1/responses`, `POST /v1/chat/completions` | AiProxy transports (streaming only) |
| `GET /anthropic/v1/models`, `POST /anthropic/v1/messages` | Anthropic Messages API (`x-api-key` required) |
| `GET /_stats` | per model: `inflight`, `maxInflight`, `total`, `errors`, `overCapacity` (requests beyond the catalog concurrency) |
| `GET /_log?since=N` | one entry per request: api, model, scenario, step, status/error, input/cached/output tokens, effort, notices seen, calls made, dropped/cancelled… |
| `GET /_request/{seq}` | the raw request body |
| `POST /_reset` | clear stats, log and retry counters |

Catalog (AiProxy): the models of `docs/AIPROXY-AGENT-GUIDE.md` — `gemma-4` (unloaded, efforts none/max), `ornith15-9b-mtp-128k`
(vision), `qwen38-27b-iq3s` (stopped: answers 503 `backend_unavailable`), `qwen3.8-27b` (loaded, concurrency 2, efforts
none/low/medium/xhigh, 262k context, 16k max output) — plus `tiny-ctx` (loaded, concurrency 1, `--tiny-ctx` window, default
12000: NetPI's system prompt and tool definitions alone are ~6k tokens). Anthropic: `claude-sonnet-4-5`, `claude-haiku-4-5`.

### Scenarios

The answer is scripted by the conversation: the `[s:name key=value …]` tag in the latest user message that is not a
harness notice picks the scenario, and the step is the number of assistant messages after that message. No tag = a
markdown echo of the message. Two rules apply to every scenario: when the last message is a `nudge` notice the model
finishes (`NUDGE-RESUMED`), and when it is an `agent-result` notice it acknowledges the report (`AGENT-RESULT-RECEIVED`).
Each final answer contains an upper-case marker (`TOOLS-DONE`, `SLOW-DONE`, …) that tests look for.

| tag | behaviour |
|---|---|
| *(none)*, `[s:echo]` | thinking + markdown echo of the message (table, code block), `ECHO-DONE`; reports the number of images received |
| `[s:tools file= old= new= create=]` | `ls` → `read file` → `edit file old→new` + `write create` → summary |
| `[s:parallel file= pattern=]` | four read-only calls in one turn (`read`, `grep`, `find`, `ls`) |
| `[s:bash]` | a `bash` command with multi-line output streamed over ~1 s |
| `[s:where file=]` | `bash pwd` + `write file`, answers `PWD=<output>` |
| `[s:slow ms=6000]` | thinking + text streamed slowly over `ms` (steering, queue, abort) |
| `[s:slowtools]` | three sequential `bash` calls, the first sleeps 2 s (steering mid-batch, abort during a tool) |
| `[s:textcall]` | a Qwen-style textual `<tool_call><function=lanes_list>` in the text (tool-call repair) |
| `[s:cutoff]` | only thinking, then `incomplete` / `length` / `max_tokens` (nudge) |
| `[s:empty]` | an empty answer (nudge) |
| `[s:thinktags]` | `<think>…</think>` inline in the text, as Qwen without a reasoning parser |
| `[s:badargs file=]` | truncated JSON arguments + an unknown tool name, then a valid call |
| `[s:drop n=1]` | the first `n` attempts drop the connection mid-stream (retry, `stream.reset`) |
| `[s:stall n=1 ms=60000]` | the first `n` attempts go silent mid-stream (retry stall timeout) |
| `[s:midfail n=1]` | an error inside the stream: `response.failed` / error chunk / Anthropic `overloaded_error` |
| `[s:error status=503 n=1]` | the first `n` attempts fail with an HTTP error |
| `[s:fail]` | HTTP 400 (non-retryable) |
| `[s:spawn n=3 delay=1500 stagger=0 model=]` | `agent_spawn` × n (workers run `[s:sub]` for `delay + (i-1)·stagger` ms) → `agent_wait` → summary of the reports |
| `[s:spawnbg delay=]` | one background worker, the parent ends its turn; the report arrives later as an `agent-result` notice |
| `[s:nest delay=]` | orchestrator → `lead` (`[s:subspawn]`: `agent_send` to the parent, `agent_spawn wait=true` of a `helper`) |
| `[s:sub i= delay=]` | subagent: thinks for `delay` ms, reports `Report from <name>: i squared is i²` (also the default for subagents) |
| `[s:ideas title=]` | `idea_add` with tags, priority and a section |
| `[s:long n=8 lines=60]` | `n` turns of `bash` output (`LONGSTEP k/n`) to fill the context (compaction on `tiny-ctx`) |
| compaction summarizer | recognized by its system prompt: returns a summary that carries the scenario tag and the last `LONGSTEP` |

### Pointing a dev instance at the mock

```bash
dotnet tests/MockLlm/bin/Debug/MockLlm.dll --port 7479 --init-home ~/.netpi-mock   # writes the provider settings, keeps running
dotnet artifacts/app/netpi-server.dll --home ~/.netpi-mock --port 7431 --open
```

`--init-home` merges this into `<home>/settings.json` (keeps everything else):

```json
{
  "providers": {
    "aiproxy":   { "baseUrl": "http://127.0.0.1:7479" },
    "anthropic": { "baseUrl": "http://127.0.0.1:7479/anthropic", "apiKey": "mock-key" }
  }
}
```

Then type e.g. `Please edit notes.txt [s:tools]` or `[s:spawn n=3 delay=3000]` in the UI. `--speed 0.3` slows every
stream down (UI work), `--verbose` prints one line per request. For UI development, `npm run dev` proxies to that
instance (`NETPI_URL`, default `http://127.0.0.1:7431`).

## End-to-end suite (`tests/NetPI.E2E`)

```bash
dotnet tests/NetPI.E2E/bin/Debug/NetPI.E2E.dll [options] [name filter…]
  --port N        netpi-server port (default 7470)
  --mock-port N   MockLlm port (default 7471)
  --app DIR       run this app folder instead of a fresh copy of artifacts/app
  --speed X       mock stream speed factor
  --no-ui         skip the Playwright UI test
  --keep          keep the work folder (server.log, home, projects, app copy)
  --verbose       echo server log lines and mock requests
  --list          list the tests
```

55 tests, about 90 s. Build first (it runs whatever is in `artifacts/app`, including the web UI from `npm run build`).

What it does: starts MockLlm in-process, copies `artifacts/app` to `<temp>/netpi-e2e/<run>/app`, writes a settings file
into a fresh home (`providers.*` → the mock, fast retry backoff), starts `dotnet app/netpi-server.dll --home … --token
e2e-token`, connects over `/ws` like the UI (subscribed to all sessions, every event recorded) and runs the tests against
one server instance (the last test stops it with SIGTERM and restarts it on the same home). Each test creates its own
sessions/projects. The work folder is deleted at the end unless `--keep` is given (then `server.log`, the home with its
SQLite database and the project folders stay for inspection).

Coverage (run `--list` for the names):

- **startup / catalog**: all 17 plugins `running`, UI tabs, slash commands, tools, plugin UI bundles served; mock models with
  context/concurrency/efforts/status; default model = first loaded local model; lane pools from the catalog.
- **chat loop**: `stream.start` → thinking/text `stream.delta` → `stream.end` → `message.added`, `agent.status`
  running → idle, `session.context`, `usage.recorded`; streamed text = persisted text; usage and thinking duration persisted.
- **tools**: `ls`/`read`/`edit`/`write` really run in a project folder, a CRLF file keeps CRLF, results persisted in call order,
  `tool.start`/`tool.end`/`stream.tool` ids consistent, `sessions.messages` paging with `beforeSeq`; parallel read-only batch;
  `bash` with live `tool.output`, exit code, `processes.list`; invalid JSON arguments and unknown tools are fed back.
- **providers**: Responses (default) and Chat Completions (per-model `transport`), reasoning efforts; Anthropic thinking with
  signatures replayed across `tool_use` turns; switching providers mid-session (Responses → Anthropic → Chat → Responses);
  images for vision models, omitted for text-only ones; cached-token usage per API, context meter, agent totals;
  `<think>` tags split on both OpenAI transports.
- **control**: steering mid tool batch (remaining calls skipped, message delivered next turn), follow-up queue and
  `agent.dequeue`, abort while streaming (partial answer `aborted`, request cancelled at the backend) and during a tool
  (process tree killed, the rest of the batch never starts), project switch (notice, new cwd, the model sees the notice,
  request prefix byte-identical),
  deleting a session (or an orchestrator with running subagents) during a run.
- **hooks / middleware**: textual tool-call repair, nudge after `length` and after an empty answer, retry after a dropped
  connection, HTTP 503, a stalled stream and in-stream errors on all three APIs (`agent.notice` + `stream.reset`), giving
  up after `retry.maxAttempts`, non-retryable errors, unknown model, budgets, a stopped backend; auto-compaction on
  `tiny-ctx` (summary, `compacted` flags, `messages.compacted`, context meter drops), overflow recovery when the backend's
  window is smaller than advertised, `/compact`; disabling/enabling a plugin; AGENTS.md and the working directory as
  notices (an edited AGENTS.md is appended, the prefix stays byte-identical).
- **lanes / subagents**: 3 workers on `qwen3.8-27b` (2 lanes) → at most 2 requests in flight at the backend, one worker
  queued, the parent yields and resumes with the reports, `lanes.list` / `work.snapshot` mid-flight, no duplicate
  `agent-result` notices; a background worker's report wakes the idle parent; steering interrupts `agent_wait` and the late
  report arrives as a notice; aborting the orchestrator cancels its workers; aborting one worker; nested
  orchestrator → lead → helper (`wait=true`) without deadlock, `agent_send`; three top-level chats on one pool.
- **ideas**: `idea_add` writes `<project>/ideas.json`, `ideas.list`, `ideas.changed`, `ideas.add`.
- **hot reload**: overwriting `NetPI.Nudge.dll` → `plugins.changed`, reload, `plugins.unloaded { collected: true }`, still
  works; `plugins.reload` of providers/tools/hooks/lanes/context; reloading the agent runtime mid-run; reloading the provider
  while a stream is open.
- **server**: session-scoped events only reach subscribed clients, HTTP RPC fallback and auth, external `settings.json`
  edits applied live, automatic session titles, projects CRUD, SIGTERM during a run → exit 0 → restart keeps sessions,
  messages and projects.
- **UI** (`ui/smoke.mjs`, Playwright): open a prepared session, send `[s:tools]` (streaming block, tool rows, diff, final
  answer), Work/Ideas/Diagnostics/Files tabs against the real server, subagents live in the Work tab, steering with
  Enter, Esc abort, a retried stream; fails on console errors or failed requests. Screenshots:
  `tests/NetPI.E2E/screenshots/ui-*.png`.

The UI test needs `node` and Playwright with Chromium (`npm i -g playwright && npx playwright install chromium`, or
`PLAYWRIGHT_BROWSERS_PATH` pointing at installed browsers). When Playwright's own Chromium build is not downloaded it
falls back to an installed Edge or Chrome and prints `browser: msedge`. Use `--no-ui` without Playwright. It can also run
on its own against any server: `node tests/NetPI.E2E/ui/smoke.mjs --url http://127.0.0.1:7431 --token <token> --session "<title>"`.

On Windows bash scenarios run in Git Bash (which shows directories under `%TEMP%`, where the work dir lives, as
`/tmp/…`); the shutdown test kills the process instead of sending SIGTERM and only checks persistence.

### Adding a test

Add a scenario to `tests/MockLlm/Scenarios.cs` (a `Plan` per step: thinking, text, tool calls, stop reason, drop/stall/error
behaviour) and a test to one of the `tests/NetPI.E2E/*Tests.cs` files. `Env.Run(sessionId, text)` sends a message and waits
until the session's agent is idle, returning the run's events and the transcript; `Env.MockLog(since)` shows what the
backend received.
