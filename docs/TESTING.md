# Testing NetPI

Three layers, all without NuGet packages (console runners, no test framework):

| layer | where | what it needs |
|---|---|---|
| unit suites | `tests/NetPI.{Host,Providers,Tools,Agent,Aux}.Tests` | the built projects |
| mock model server | `tests/MockLlm` | nothing (ASP.NET shared framework) |
| end-to-end suite | `tests/NetPI.E2E` | the built app (`artifacts/dev/app`, which `scripts/e2e.ps1` builds as needed), the mock, and for the UI tests Node 22 + Playwright (`playwright-core` devDependency; falls back to an installed Edge/Chrome) |

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

Plugins build into `artifacts/dev/app/plugins/<Name>/`, the server into `artifacts/dev/app/`. The web UI and the
plugin UI bundles come from `npm run build` (`web/dist`, `plugins/*/wwwroot/ui.js`) and the .NET build copies them
into the build output.

`artifacts/app` is the **installed** build — the one a running NetPI loads its plugins from — and only
`build.ps1 -Publish` writes there. That is the point: a build (of a plugin, or of a test project that references one)
lands in `artifacts/dev/app` and the running app sees nothing, so no chat loses a tool because someone ran the tests.
Publishing while NetPI runs hot-reloads the changed plugins, which every chat holding one of their tools hears about
(`-Publish -NextStart` defers all of it to the next start). The E2E suite runs from a private copy of the build output
(`artifacts/dev/app`), so it is not affected by (and does not affect) other instances.

## Unit suites

The loop that is fast: **one big run, then only what failed.** `scripts/test.ps1` builds the five suites, runs them
once, keeps the whole output in `artifacts/testlogs/<timestamp>.txt`, and finishes with the failing test names as a
ready-to-paste `-Only` command. Re-running that while fixing takes seconds instead of the ~90 s of the full set; the
full run belongs at the end, before a merge.

```powershell
.\scripts\test.ps1                                   # build + run all five, print a re-run command for the failures
.\scripts\test.ps1 -Only "settings:", "goal:"        # just tests whose name contains these (substring, OR-ed)
.\scripts\test.ps1 -Suite Aux -SkipBuild             # one suite, reusing the build (nothing changed)
.\scripts\test.ps1 -Serial                           # one suite at a time, when a run misbehaves
```

The suites are built **once**, as one generated solution holding just the requested projects (`artifacts/test-speed`),
so shared dependencies are compiled once instead of once per suite. Up to three suite *processes* then run at a
time (`-Parallel`, default 2; `-Serial` for one). Each suite gets its own temporary root (`NETPI_TEST_ROOT`, under
the system temp — not the repo, because the git tests create real repositories and a nested one behaves
differently), so two invocations of the same suite never delete each other's files.

Next to each suite's result the script prints the **process time** and the time the suite's own timers reported, and
calls out any suite that spent more than three seconds outside its own tests. That gap is not noise: it is the
runner waiting for output that never arrived, which is what a child process outliving its command looks like. A
suite that exits non-zero without reporting a failure is reported as a crash, and a filter that matched nothing is
reported as such — neither is ever green. `artifacts/testlogs/<timestamp>.json` holds the same numbers machine-readably.

Each suite can also be run directly, which is what the script does:

```bash
dotnet tests/NetPI.Providers.Tests/bin/Debug/NetPI.Providers.Tests.dll   # AiProxy (Responses/Chat), Anthropic, OpenRouter against a scripted HTTP mock
dotnet tests/NetPI.Tools.Tests/bin/Debug/NetPI.Tools.Tests.dll           # read/write/edit/grep/find/ls, bash/pwsh, processes, files.open, files.git
dotnet tests/NetPI.Agent.Tests/bin/Debug/NetPI.Agent.Tests.dll           # agent loop, steering/queue/abort, subagents, agents, persistence, context notices, goals, skills
dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll               # retry, nudge, tool repair, compaction, ideas, work, diagnostics, todo, web, media, ssh
dotnet tests/NetPI.Aux.Tests/bin/Debug/NetPI.Aux.Tests.dll ideas        # the ideas flow only: tools and RPC (IdeasTests), the storage and the
                                                                         # answer transaction (IdeasStorageTests, ReviewStorageTests), the cutover,
                                                                         # export and import (IdeasMigrationTests), the chat checks (IdeasCheckTests),
                                                                         # commit tracking (IdeasCommitTests). They run against a real temporary
                                                                         # SQLite database, not a fake; the load tests load the built plugins from
                                                                         # artifacts/dev/app (what a plain build makes), or from NETPI_APP_DIR
tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests                        # kernel: SQLite, settings, bus, registries, sessions, catalog, server, plugins
dotnet tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests.dll backup  # the snapshot: the WAL copy, retention, manifest verification, an offline restore
                                                    # into a new home, and the SQLite-backed ideas backlog travelling in it and coming
                                                    # back (BackupTests, ReviewBackupTests)
```

Every runner takes optional name filters (`… NetPI.Agent.Tests.dll abort scheduler`) and exits with 0 when all selected
tests pass. `NETPI_TEST_LOGS=1` shows host logs in the Host suite.

The web tool tests serve pages and fake SearXNG / Brave endpoints from a local Kestrel server and never read your pi
config or `BRAVE_API_KEY`. The `screenshot` test drives a real headless Edge/Chrome/Chromium; without one installed it
checks everything except the page screenshots and says so.

The ideas suites inject the failures the happy paths cannot show, and they need a **real database** to show them: the
aux harness opens an actual SQLite file in a temporary home (it references `NetPI.Host` for the real `Database`), so a
rejected patch leaving no trace, two plugin instances keeping each other's write, a rolled-back transaction, a revision
that survives a restart and one effect for two answers of a card are what is actually verified — an in-memory fake
proves none of them. On top of that: a malformed legacy file fails the whole cutover and leaves the originals untouched,
a cutover that already happened is refused, a restart after the commit does not import again, a check claim nobody owns
any more is retryable, a decision that does not answer, a burst of 45 commits, a rewritten history and a worktree. The
commit tests drive the check directly through `IdeaCommitCheck.SweepNowAsync` against a fake repository that behaves
like `git log since..until`, so each case is exact and needs no git.

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
markdown echo of the message. Every tag also takes `hold=<ms>`: the first answer of the scenario stays open for that long after its first chunk (a fixed time, not
scaled by `--speed`), so a test can observe the live state instead of racing it. Two rules apply to every scenario: when the last message is a `nudge` notice the model
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
| `[s:textcall]` | a Qwen-style textual `<tool_call><function=agent_choices>` as the whole message (tool-call repair) |
| `[s:doccall]` | the same markup as a documentation example: prose around a fenced call (never repaired, nudged) |
| `[s:cutoff]` | only thinking, then `incomplete` / `length` / `max_tokens` (nudge) |
| `[s:empty]` | an empty answer (nudge) |
| `[s:thinktags]` | `<think>…</think>` inline in the text, as Qwen without a reasoning parser |
| `[s:badargs file=]` | truncated JSON arguments + an unknown tool name, then a valid call |
| `[s:drop n=1]` | the first `n` attempts drop the connection mid-stream (retry, `stream.reset`) |
| `[s:stall n=1 ms=60000]` | the first `n` attempts go silent mid-stream (retry stall timeout) |
| `[s:midfail n=1]` | an error inside the stream: `response.failed` / error chunk / Anthropic `overloaded_error` |
| `[s:error status=503 n=1]` | the first `n` attempts fail with an HTTP error |
| `[s:fail]` | HTTP 400 (non-retryable) |
| `[s:spawn n=3 delay=1500 stagger=0 model= workerhold=0]` | `agent_spawn background` × n (workers run `[s:sub]` for `delay + (i-1)·stagger` ms, plus a fixed `workerhold` stream hold independent of mock speed) → `agent` `wait` → summary of the reports |
| `[s:spawnbg delay=]` | one background worker, the parent ends its turn; the report arrives later as an `agent-result` notice |
| `[s:nest delay=]` | orchestrator → `lead` (`[s:subspawn]`: `agent` `send` to the parent, `agent_spawn` of a `helper`, which waits for it) |
| `[s:sub i= delay=]` | subagent: thinks for `delay` ms, reports `Report from <name>: i squared is i²` (also the default for subagents) |
| `[s:ideas title=]` | `ideas` (action `add`) with tags, priority and a section |
| `[s:ask]` | `ask_user` with one question (`Which way?`: Fast, Thorough), then `Answer received: <the result>` |
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

The real `netpi-server` (a private copy of the dev build) against the mock model server, driven over HTTP/WebSocket like the UI
does. **Run it by what your change can reach, not as a whole, while you work**: one test is ~2 s, the whole suite ~35 s.

```powershell
.\scripts\e2e.ps1 -Changed                        # the tests your changed files can affect (tests\NetPI.E2E\areas.json)
.\scripts\e2e.ps1 -Only control.abort-stream, retry   # test ids, or substrings of an id (then of a name)
.\scripts\e2e.ps1 -Tag scheduling                 # an area or a tag (-List shows them)
.\scripts\e2e.ps1 -Smoke                          # one representative test per boundary, ~10 s
.\scripts\e2e.ps1 -Failed                         # what failed and has not passed since, across runs
.\scripts\e2e.ps1 -List [-Tag x]                  # ids, tags, last duration, name; starts nothing
.\scripts\e2e.ps1                                 # everything, sharded: the gate before a merge
.\scripts\e2e.ps1 -Only <id> -Repeat 20 -Fresh    # how often does it fail? every failure keeps its evidence
.\scripts\e2e.ps1 -Fresh                          # every test alone on its own server: finds hidden order dependencies
.\scripts\e2e.ps1 -SelfTest                       # the runner's own tests (selection, scheduling, containment, report); no server
```

The script builds only the projects whose sources changed since their last build here (per-project stamps in
`artifacts/e2elogs`; output in `artifacts/dev/app`, nothing installed, a running NetPI untouched). `-SkipBuild` trusts what is
there, `-Rebuild` builds the solution. It runs the same thing as the runner directly:

```bash
dotnet tests/NetPI.E2E/bin/Release/NetPI.E2E.dll [options] [test id | filter]...
  <filter>          an exact test id, else a substring of ids, else (only if no id matches) of names; several are OR-ed
  --tag a,b         add every test with one of these tags (an area such as `retry`, or smoke, lifecycle, scheduling, ui, needs-node)
  --smoke           = --tag smoke.   --skip-tag a,b / --no-ui leave tests out.   --failed: the tests that are failing now
  --list            the selected tests, and start nothing.   A filter or tag that matches nothing exits 2 before anything starts.
  --parallel N|auto shards running at once (default auto: about one per 12 s of estimated work, at most min(cores/2, 4))
  --repeat N        every selected test N times.   --fresh: a fresh server for every test run.   --timeout-scale X
  --out DIR         this run's folder (default artifacts/e2elogs/<run>)      --state-dir DIR / --no-state
  --speed X  --app DIR  --port N  --mock-port N  --keep  --verbose  --self-test
```

**Shards.** Each shard is a server and a mock model server of its own (a copy of the dev build, its own home and free ports),
so they cannot disturb one another; the selected tests are split over them by how long each took last time
(`artifacts/e2elogs/timings.json`), longest first, and each shard runs its tests in registration order. Two runs at once (two
agents) share nothing: ports, work folders, screenshots and result folders are per run. Measured on one 16-core machine
(2026-09-30): the whole suite took 126 s serially and ~35 s in 4 shards; the longest single test (`ui.smoke`, ~26 s) bounds it.

**Results.** Every run leaves `artifacts/e2elogs/<run>/`: `report.json` (selection, per-test outcome and time, per-shard
setup/test/teardown time, revision), `console.txt` (from the script), `screenshots/`, and `failures/<id>.txt` for each failed
test: the message, the test's console output, the server log since it started, the requests the mock model server got and the
client events. All failures of a run are listed together with a rerun command; `failing.json` remembers them across runs until
they pass (`-Failed`). The 20 newest run folders are kept. Exit codes: 0 passed, 1 a test failed or timed out, 2 bad selection,
3 a server would not start (that shard's tests are reported as *not run*), 4 the runner itself failed.

**A timed-out test is contained.** Its body keeps running in the background, so its server is stopped and the rest of its shard
gets a fresh one: it can never mutate the next test's fixture. `--self-test` pins this (and was mutation-checked).

**Ids and tags.** An id is `area.name` (`retry.drop`); the area is also a tag. `tests/NetPI.E2E/Catalog.cs` holds the other tags.
`smoke` is one representative test per boundary: startup, chat and a real file tool, the Responses/Chat/Anthropic adapters, abort
and slot scheduling, persistence across a restart and a reload while active (the `lifecycle` tag is the wider group of those). The
browser tests (`ui`, `needs-node`) are not in smoke yet. `areas.json` maps source paths to tags/ids for `-Changed`; `--self-test`
fails when it names something that does not exist or a plugin has no rule.

**Each test works alone.** Checked by running every test on its own fresh server (`-Fresh`); a test that depends on another
having run first (or that asserts a server-wide total) is a bug. Two were found that way: the usage summary total and
`subagents.wait-steer`. The same tool measures a flaky test (`-Repeat 20 -Fresh`). Never run it again "to see": read
`failures/<id>.txt`, find the state the test raced, and either wait for that state or give the mock a knob that holds it
(`hold=<ms>` in a scenario tag keeps the first answer's stream open after its first chunk).

Adding a test: see the end of this section. It needs an id in `r.Add("<id>", "<name>", …)` and, if it belongs to a group, an entry
in `Catalog.cs` and (for a new plugin or file) `areas.json`.

What it does: starts MockLlm in-process, copies the dev build (`artifacts/dev/app`; `--app DIR` names another) to
`<temp>/netpi-e2e/<run>/app`, writes a settings file into a fresh home (`providers.*` → the mock, fast retry backoff), starts
`dotnet app/netpi-server.dll --home … --token e2e-token`, connects over `/ws` like the UI (subscribed to all sessions, every
event recorded) and runs the tests against that server. Each test creates its own sessions/projects. The work folder is deleted
at the end unless `--keep` is given (then `server.log`, the home with its SQLite database and the project folders stay for
inspection).

Coverage (run `--list` for the names):

- **startup / catalog**: the expected bundled plugins `running`, UI tabs, slash commands, tools, plugin UI bundles served; mock models with
  context/concurrency/efforts/status; default model = first loaded local model; no agents set up (the suite's settings have none: a slot per model).
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
- **hooks / middleware**: textual tool-call repair (a standalone envelope runs, a documented example does not), nudge after `length` and after an empty answer, retry after a dropped
  connection, HTTP 503, a stalled stream and in-stream errors on all three APIs (`agent.notice` + `stream.reset`), giving
  up after `retry.maxAttempts`, non-retryable errors, unknown model, budgets, a stopped backend; auto-compaction on
  `tiny-ctx` (summary, `compacted` flags, `messages.compacted`, context meter drops), overflow recovery when the backend's
  window is smaller than advertised, `/compact`; disabling/enabling a plugin; AGENTS.md and the working directory as
  notices (an edited AGENTS.md is appended, the prefix stays byte-identical); `context.prompts` has the system prompt
  and tools exactly as the backend got them.
- **profiles**: a new chat gets its project's default profile, else the global default, in a `session.updated` newer
  than the `sessions.create` result.
- **agents**: agents set up with `settings.set` are listed with their state (instances from the catalog, the one on the
  stopped backend inactive); `agents.use` runs a chat on one; switched off (`agents.setEnabled`, `agents.changed`) the chat
  stops at once with a notice; an agent whose model isn't loaded is refused without a request to the backend; a model
  no agent runs.
- **slots / subagents** (no agents: a slot per model): 3 workers on `qwen3.8-27b` (2 slots) → at most 2 requests in flight at the backend, one worker
  queued, the parent yields and resumes with the reports, `agents.list` / `work.snapshot` mid-flight, no duplicate
  `agent-result` notices; a background worker's report wakes the idle parent; steering interrupts `agent` `wait` and the late
  report arrives as a notice; aborting the orchestrator cancels its workers; aborting one worker; nested
  orchestrator → lead → helper (the lead's spawn waits) without deadlock, `agent` `send`; three top-level chats on one model.
- **skills**: a project's `.agents/skills` skill in `skills.list`, the `skills` catalog notice and `/skill:name` with the
  skill's instructions sent to the model (not in the system prompt), the notice tied to its message.
- **ideas**: the `ideas` tool (add) writes the backlog in the app's own database, stamped with the session's project —
  `ideas.list` says so (`storage.backend` is `sqlite`, `storage.scope` is `netpi.ideas`) — and no `ideas.json` is written
  to the home; `ideas.changed` carries the same payload, `ideas.add` appends.
- **hot reload**: overwriting `NetPI.Nudge.dll` → `plugins.changed`, reload, `plugins.unloaded { collected: true }`, still
  works; `plugins.reload` of providers/tools/hooks/agents/context; reloading the agent runtime mid-run; reloading the provider
  while a stream is open.
- **server**: session-scoped events only reach subscribed clients, HTTP RPC fallback and auth, external `settings.json`
  edits applied live, automatic session titles, projects CRUD, SIGTERM during a run → exit 0 → restart keeps sessions,
  messages and projects.
- **UI** (`ui/smoke.mjs`, Playwright): open a prepared session, send `[s:tools]` (streaming block, tool rows, diff, final
  answer), Work/Ideas/Diagnostics/Files tabs against the real server, subagents live in the Work tab, steering with
  Enter, Esc abort, a retried stream; fails on console errors or failed requests. Screenshots:
  `tests/NetPI.E2E/screenshots/ui-*.png`.

The UI test needs `node` and Playwright: `playwright-core` is a devDependency (installed with the other
dependencies), and both the E2E smoke and the mock walkthrough resolve it (see `web/mock/pw.mjs`). If Playwright's own
Chromium build is not downloaded (`npx playwright-core install chromium`) the tests fall back to an installed Edge or
Chrome and print `browser: msedge`. Use `--no-ui` without Playwright. It can also run on its own against any server:
`node tests/NetPI.E2E/ui/smoke.mjs --url http://127.0.0.1:7431 --token <token> --session "<title>"`.

On Windows bash scenarios run in Git Bash (which shows directories under `%TEMP%`, where the work dir lives, as
`/tmp/…`); the shutdown test kills the process instead of sending SIGTERM and only checks persistence.

### Adding a test

Add a scenario to `tests/MockLlm/Scenarios.cs` (a `Plan` per step: thinking, text, tool calls, stop reason, drop/stall/error
behaviour) and a test to one of the `tests/NetPI.E2E/*Tests.cs` files. `Env.Run(sessionId, text)` sends a message and waits
until the session's agent is idle, returning the run's events and the transcript; `Env.MockLog(since)` shows what the
backend received.

## Review-fix validation (2026-09-28)

The solution and web/plugin bundles were built using `AppOutDir=artifacts/review/app/` and `NETPI_NO_COPY=1`
for npm, keeping validation separate from the running app. Existing Ideas-tab Svelte warnings about initial state
capture remain; the .NET solution build has no warnings or errors.

Regression coverage added:

- Agent: two sessions with the same provider tool-call ID have separate approvals; stale approval IDs cannot
  answer another request. Host: a fork discards `guardrailsAllowed` alongside the existing run state.
- Agent: concurrent reservations cannot each spend the same remaining cap; reservations survive a new ledger
  instance; concurrent first-use recording retains every charge; changing accounting days/periods refreshes totals;
  unknown prices under caps, reported partial usage, rejected attempts, and cancellation are accounted for.
- Host: snapshot of committed WAL data, automatic-only retention, damaged manifest handling, checksum failure,
  path validation, actual offline restore followed by normal host startup, and refusal to overwrite an existing
  home. This recovery test requires Node.js 22+ on PATH.
- Real-server UI smoke: Settings → Data & backups creates and verifies a snapshot.

Fresh suite results are recorded in STATUS.md. Live paid billing and Linux/macOS were not exercised.

## MCP fixtures

MCP tests run in the existing Aux, Agent, Host and E2E suites. The shared `McpFixture.cs` mode launches a protocol-only stdio child via `--mcp-fixture`. HTTP fixtures bind local ephemeral ports. Run the scoped cases with filters `mcp`, `deferred:` and `mcp host:`; the E2E filter `mcp` includes actual Responses request captures and the narrow-panel UI check. The standalone UI check is `node tests/NetPI.E2E/ui/mcp-smoke.mjs`. All fixtures use isolated homes/processes and never install into the running app.

Browser launches disable Edge's `msWindowTabManagerPublic` feature, including the browser and screenshot tools exercised by the Aux suite and the Playwright UI smoke tests. Otherwise headless Edge can leave Explorer-owned tab proxies in Alt-Tab after the browser exits. This flag prevents new entries; restarting Windows Explorer clears old entries (and briefly restarts the taskbar).
