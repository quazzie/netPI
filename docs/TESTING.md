# Testing NetPI

The Host plugin-independence gate scans all 31 plugin projects and current shared/imported build declarations for peer dependencies, then starts/stops each plugin alone. Agent regressions cover reload admission, shutdown timeout, waiter cancellation, prompt races, profiles without Context and reset forks after Context reload. Aux tests cover shared decision admission, alternate providers, verifier cancellation acknowledgement and optimistic verified updates. `web/mock/e2e.mjs --only 'plugin tab: Work|capability removal and recovery' --no-dev` exercises physical capacity/background outcomes and executor/Context UI recovery. The mock-only capability scenario creates its own session and does not run against an external server.

Three layers, all without NuGet packages (console runners, no test framework):

| layer | where | what it needs |
|---|---|---|
| unit suites | `tests/NetPI.{Host,Providers,Tools,Agent,Aux,Storage}.Tests` | the built projects |
| mock model server | `tests/MockLlm` | nothing (ASP.NET shared framework) |
| end-to-end suite | `tests/NetPI.E2E` | the built app (`artifacts/dev/app`, which `scripts/e2e.ps1` builds as needed), the mock, and for the UI tests Node 22 + Playwright (`playwright-core` devDependency; falls back to an installed Edge/Chrome) |

## Build

`NetPI.slnx` contains the Windows-only desktop shell, so outside Windows build the projects one by one in dependency order
(`BuildProjectReferences=false` keeps the build fast because every dependency was already built by the loop):

```bash
B="dotnet build -nologo -v q -clp:ErrorsOnly -p:BuildProjectReferences=false"
$B src/NetPI.Abstractions/NetPI.Abstractions.csproj
$B src/NetPI.Contracts/NetPI.Contracts.csproj
$B src/NetPI.Host/NetPI.Host.csproj
$B src/NetPI.Server/NetPI.Server.csproj
for p in plugins/*/*.csproj tests/*/*.csproj; do $B "$p" || break; done
```

```powershell
$b = 'build','-nologo','-v','q','-clp:ErrorsOnly','-p:BuildProjectReferences=false'
'src/NetPI.Abstractions','src/NetPI.Contracts','src/NetPI.Host','src/NetPI.Server' | % { dotnet @b (Get-ChildItem $_ -Filter *.csproj).FullName }
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

The loop that is fast: **one big run, then only what failed.** `scripts/test.ps1` builds the six suites, runs them
once, keeps the whole output in `artifacts/testlogs/<timestamp>.txt`, and finishes with the failing test names as a
ready-to-paste `-Only` command. Re-running that while fixing takes seconds instead of the ~90 s of the full set; the
full run belongs at the end, before a merge.

```powershell
.\scripts\test.ps1                                   # build + run all six, print a re-run command for the failures
.\scripts\test.ps1 -Only "settings:", "goal:"        # just tests whose name contains these (substring, OR-ed)
.\scripts\test.ps1 -Suite Aux -SkipBuild             # one suite, reusing the build (nothing changed)
.\scripts\test.ps1 -Suite Aux,Host -SkipBuild        # two: a [string[]] takes a comma, not a second -Suite
.\scripts\test.ps1 -Serial                           # one suite at a time, when a run misbehaves
```

A test that cannot run here (no git on PATH, no Edge/Chrome/Chromium, the plugins not built) calls `Check.Skip(reason)`
instead of returning quietly: it prints `SKIP  <test> (<reason>)`, the suite's summary counts it apart from the
passes (`12 passed, 0 failed, 3 skipped, 15 total`), and the script prints how many there were per suite. A skip is
not a failure — it says what was missing — but it is never counted as a pass either.

The suites are built **once**, as one generated solution holding just the requested projects (`artifacts/test-speed`),
so shared dependencies are compiled once instead of once per suite. Up to five suite *processes* then run at a
time (`-Parallel`, 1–5, default 2; `-Serial` for one). Each suite gets its own temporary root (`NETPI_TEST_ROOT`, under
the system temp — not the repo, because the git tests create real repositories and a nested one behaves
differently), so two invocations of the same suite never delete each other's files. A suite still running after
`-TimeoutMinutes` (default 30) is stopped and fails the run, instead of being waited for. The build test runs `build.ps1`
with `-NoClean`, so it builds over `artifacts/dev/app` instead of emptying it under the suites that load plugins from it.

Next to each suite's result the script prints the **process time** and the time the suite's own timers reported, and
calls out any suite that spent more than three seconds outside its own tests. That gap is not noise: it is the
runner waiting for output that never arrived, which is what a child process outliving its command looks like. A
suite that exits non-zero without reporting a failure is reported as a crash, and a filter that matched nothing at
all is reported as such — neither is ever green. A filter that matches nothing in *one* suite while another suite ran
it is normal (`-Suite Host,Storage -Only "settings:"`), so the suite is marked in the table instead of failing the
run, and the re-run command names the suites that failed. `artifacts/testlogs/<timestamp>.json` holds the same
numbers machine-readably.

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
tests/NetPI.Storage.Tests/bin/Debug/NetPI.Storage.Tests.dll            # the storage port: one set of scenarios run against every
                                                                      # provider (memory and sqlite; a provider joins with one line in
                                                                      # Providers.All). It is the port's contract in executable form —
                                                                      # a storage provider that does not pass it is not usable
tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests                        # kernel: storage, settings, bus, registries, sessions, catalog, server, plugins
dotnet tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests.dll backup  # the snapshot: the WAL copy, retention, manifest verification, an offline restore
                                                    # into a new home, and the SQLite-backed ideas backlog travelling in it and coming
                                                    # back (BackupTests, ReviewBackupTests)
```

Every runner takes optional name filters (`… NetPI.Agent.Tests.dll abort scheduler`) and exits with 0 when all selected
tests pass. `NETPI_TEST_LOGS=1` shows host logs in the Host suite.

**The storage conformance suite** (`tests/NetPI.Storage.Tests`) is the storage port's contract in executable form:
one set of scenarios run against every store a factory builds — ids and seqs, the session list with every filter,
projects, the session tree, the context view and compaction, the copy a fork makes, `Atomic` (rollback and
re-entrancy), the key-value store, the collections (every `DataOp`, ordering and paging, `Count`/`Sum`/`DeleteWhere`,
a changed declaration, a transaction and the lock rules) and the snapshot. It holds no SQL: a provider joins with one
line in its `Providers.All` and passes the same scenarios as every other, which is what makes a second provider
trustworthy. Nothing about the storage port may be changed without running it (`-Suite Storage`).

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
test: the state of the server asked right after the failure (below), the message, the test's console output, the server log
since it started, the requests the mock model server got and the client events. No section of that file is ever empty: one that has
nothing says why, and the runner complains when it writes one that is. All failures of a run are listed together with a rerun command; `failing.json` remembers them across runs until
they pass (`-Failed`). The 20 newest run folders are kept. Exit codes: 0 passed, 1 a test failed or timed out, 2 bad selection,
3 a server would not start (that shard's tests are reported as *not run*), 4 the runner itself failed.

**A failed test is contained.** Its body may keep running in the background (a timeout), its server may be wedged, and whatever it
left running is still at work, so its server is stopped and the rest of its shard gets a fresh one: it can never mutate the next
test's fixture, and one hang cannot turn into a dozen failures that each wait out their own 30 s timeout (that is what happened
on 2026-10-01: one wedged server, six "failures"). `--self-test` pins this (and was mutation-checked).

**What is wrong with the server.** Right after any failure the runner asks the server four small questions at once, each with a
2 s deadline: `/api/health`, `app.info` (touches nothing), `sessions.list` (reads the database) over HTTP, and `app.info` over the
test's own WebSocket, and reads the process (alive? exit code, threads, CPU over the window). The failure file's `server state`
section, the console and `report.json` (`server`) carry the verdict: *the process is gone*, *wedged* (nothing answers: the thread
pool is starved or the process is frozen, with idle or busy CPU), *RPC dispatch is stuck*, *the database path is blocked*, *this
test's WebSocket is dead*, or *responsive* (the server was fine: the failure is the test's own, an RPC that was merely slow). An RPC
that times out carries the same verdict in its message (`RPC projects.create timed out after 30000ms` / `server: wedged: …`).
The probes are made from the runner, so the runner is timed first: when its own thread pool needs a second to start a trivial
work item (4 browsers and a loaded machine do that), the verdict starts with `UNRELIABLE: the runner itself is overloaded` instead of
blaming a healthy server. For a wedge, the server's own log is the second half of the evidence: its stall watchdog names the handler
or the starved pool (`docs/DEBUGGING.md`, "The app stopped answering"). `inspect.triage` freezes a real server process and checks the
verdict.

The third half is the stacks: when the server is alive and does not answer, the runner reads every thread's managed stack from outside
with `dotnet-stack report -p <pid>` into `failures/<id>.stacks.txt`, which names what the process is blocked on. The tool is not a
dependency of the repository (`dotnet tool install -g dotnet-stack` once; `NETPI_E2E_STACK_TOOL` names another executable); without
it the failure file says how to get it.

**A test ends with nothing of its own running.** When a test passes, the runner waits for the agents it started to finish (up to 5 s,
stopping what will not) before the next test begins. Anything that was still running when the test returned is a **leak**: a `LEAK`
line under the test and in the summary, `leak` in `report.json`. It is a warning, not a failure, but a leak is a race waiting for the
next test: a worker still running makes its model requests inside the next test's window (`provider.anthropic-thinking` failed once
because `chat.fork` returned while a background worker's report was about to wake its parent). Wait for the final idle, as
`subagents.report-wakes-parent` does. A leak that will not stop also costs the shard its server. The settle step also asks `events.flush` first: an RPC answers before
its own events are delivered (`settings.set` returns before its `settings.changed`), and an event still on its way lands in the *next*
test's window and satisfies its first wait for "an event of that type" (`settings.live-edit` failed that way); the flush answers behind
every earlier event on the socket, so the event log is complete when the next test marks its start.

**The flake ledger.** `tests/NetPI.E2E/flakes.json` is the suite's health across runs, committed beside the test list so that
pruning `artifacts/e2elogs` cannot take it and a reader sees the rates without running anything. Every run folds its results in: a
test that has failed at least once gets a row with how many times it ran and failed, when it first and last failed, and the cause
the runner's own diagnosis gave (the server verdict first, the test's message beside it). A later pass marks the row `quiet` rather
than deleting it — "it flaked 2 in 20" is the part worth keeping — and a test that has never failed gets no row at all.

```powershell
.\scripts\e2e.ps1 -Flakes
```

`-Failed` answers "what is failing right now" and forgets a test the moment it passes; the ledger answers "how often", which is the
number that decides whether a test needs a fix or a re-run. When a flake is fixed, quote the before/after rate in the commit message
and the ledger keeps the history.

**Finding flakes: run it loaded.** An idle machine hides the races a busy one (another agent building, a browser test) shows, and a
suite that is green idle can still fail one run in ten. Start CPU burners and loop the whole suite, in a worktree of its own so that
building in yours cannot change the app copy the runs start from:

```powershell
$jobs = 1..12 | ForEach-Object { Start-Job { $end = [DateTime]::Now.AddMinutes(20); while ([DateTime]::Now -lt $end) { [math]::Sqrt(12345.678) | Out-Null } } }
1..10 | ForEach-Object { .\scripts\e2e.ps1 -SkipBuild }      # every failure of every run has its failure file
$jobs | Stop-Job; $jobs | Remove-Job -Force
```

(About 12 burners on a 16-core machine doubles a run's time; many more, with several browser tests at once, starve the runner itself, and
the verdict then says so.) Read each failure file before changing anything, then measure the fix the same way: `-Only <id> -Repeat 15
-Fresh` under the burners, before and after. This found a lost update in the settings store, three test races and a leak in one
afternoon (2026-10-01); a unit test that fails sometimes is run the same way (`dotnet <Suite>.dll "<filter>"` in a loop).

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

The browser is also what measures the chat's markdown: `node web/scripts/bench-stream.mjs` streams a long answer through
both render strategies in a headless browser, checks that splitting the answer at `web/src/lib/live.js` renders exactly what
the whole answer renders (exit 1 if it does not), and prints the per-tick cost of each.

`node web/scripts/check-tab-lifecycle.mjs` is the unit suite for the plugin tab lifecycle (`web/src/lib/kit/tab.js`,
`refresh.svelte.js`): it compiles both the way the build compiles them, drives them in a headless browser with a fake
`ctx`, and checks one load on mount, events coalesced, a poll that never stacks a second load, a hidden tab that loads
nothing, and nothing running after unmount.

`node web/scripts/check-host-shims.mjs` is the unit suite for what a plugin tab bundle is built against: `svelte` and
`@netpi/kit` resolve to generated shims (`web/scripts/host-shims.mjs`) over the host UI's own copies on
`globalThis.__netpiHost`. It checks that the shims cover every export of svelte's three entry points and of the kit
index, that a tab compiled against them mounts and renders the host's kit components, that a `$derived` in the tab
follows host state (what two runtimes cannot do), and that no host, another svelte, or a host missing an export each
fail with a message saying which.

`node web/scripts/check-projects.mjs` is the unit suite for `web/src/lib/projects.js` (what the projects panel and the
projects dialog share) and for the three views of the dialog: the counts, the filter and order, and the remove flow in
Node against stubbed state, then the views themselves in a headless browser — rows and counts, the filter narrowing
the list, both forms, and what a failing rpc leaves on screen instead of a spinner.

`node web/scripts/check-rpc-deadline.mjs` is the unit suite for the RPC client's deadline (`web/src/lib/rpc.svelte.js`):
the timeout is set when the call is made and is kept through the switch to the HTTP fallback (the fetch continues
with the time still left in it, as an AbortSignal) and through the reading of the response body; a call with no
timeout waits for the server, no default is invented for it. The real client source runs in a vm with a controllable
clock, fake timers and a fetch that never answers, so only a deadline can end a call.

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
