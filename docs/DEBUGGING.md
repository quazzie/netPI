# Inspecting the running app

This is **the** way to look inside a running NetPI, for a person or a debugging agent: what runs, what waits and why,
every model call with its timing and errors, every tool call, the events that led somewhere, the logs, the settings
without secrets, and the requests a backend refused. Everything below only reads; nothing changes the app.

## Reaching the app

A running NetPI writes **`<home>/server.json`** (home: `%USERPROFILE%\.netpi`, `~/.netpi`, or `NETPI_HOME`) when it is
ready and removes it when it stops:

```json
{ "url": "http://127.0.0.1:7431", "token": "…", "pid": 26084, "version": "0.1.0", "startedAt": "…", "home": "…", "logs": "…", "desktop": true }
```

One NetPI per home, so one server.json per instance: the app holds `<home>/netpi.lock` while it runs, and a second one on
the same home refuses to start ("NetPI is already running with the home …"). Another setup (tests, a second server)
runs with its own home (`netpi-server --home <dir>` or `NETPI_HOME`); the CLI's `--home` picks which one to inspect.

The port can change (a busy one falls back to a random port) and the token is new every run, so read them here. Every
RPC method is an HTTP endpoint: `POST {url}/api/rpc/<method>` with the header `X-NetPI-Token: <token>` and the params
as a JSON body. The token guards the app like the API keys in settings.json next to it: only the same user reads either.

**The CLI** does that for you (Node, no packages):

```bash
node scripts/netpi.mjs                               # diag.overview: start here
node scripts/netpi.mjs diag.problems
node scripts/netpi.mjs diag.calls limit=20 errors=true
node scripts/netpi.mjs diag.run sessionId=ses_abc
node scripts/netpi.mjs diag.journal '{"sessionId":"ses_abc","limit":300}'
node scripts/netpi.mjs methods diag.                  # every method with what it does (W = changes something)
```

Options: `--home <dir>`, `--compact`, and `--write` for methods that change something (they are refused without it, so
looking around never changes the app). Exit codes: 0 ok, 1 the call failed, 2 NetPI isn't running there.

Without Node: `curl -s -X POST -H "X-NetPI-Token: $TOKEN" -d '{}' $URL/api/rpc/diag.overview`.

## What to call

| method | what it answers |
|---|---|
| `diag.overview` | Start here. App and process (memory, threads, the thread pool), plugins (failed ones with their error), models per provider (loaded ones), agents with their holders and waiters, active runs (status, activity, since when), model calls running and the last 15 minutes (count, errors, median first token and duration), running tools and processes, the problems, the other `diag.*` methods |
| `diag.problems` | What looks wrong now, worst first: failed plugins, providers that can't be reached, runs waiting on an agent that can't take work or waiting long, runs silent for minutes, model calls without a first token, failed calls, tools running long, errors in the log, a thread pool falling behind, a spent budget, saved failed requests. Each with a hint where to look next |
| `diag.calls` | Model calls, newest first (running ones too): model, purpose, agent, chat, run, time to first token, duration, attempts (retries), tokens, stop reason, error. Filters: `sessionId`, `runId`, `agent`, `errors`, `running`, `limit`; `detail: true` adds what `diag.call` has |
| `diag.call` | One call in detail: the request's size (messages, tools, system prompt, input chars, the person's last message), the response (text/thinking chars, tool calls), each retry and notice, the error's type, HTTP status, transient, context overflow |
| `diag.tools` | Tool calls, newest first: name, chat, run, state, duration, arguments and the result the model got (both cut short). Filters: `sessionId`, `name`, `errors`, `running` |
| `diag.tool` | One tool call in full (`callId`, and `sessionId` once it has left the tool log): the parsed arguments, the whole result text the model got, the error flag, images and `details` |
| `diag.journal` | The events that matter as a timeline, oldest first: statuses, messages (role, kind, a preview), tool starts and ends, stream starts and resets, agents' states, settings and plugin changes. Without the per-token events, which push everything else out of the host's `events.recent` within seconds. Filters: `sessionId`, `type` (prefix), `sinceSeq` |
| `diag.run` | One run in depth (`sessionId` or `agentId`): its info and since when it has its status, the chat (model, agent, profile, context size), the slot it holds or waits for (and who holds the others), its queued inputs, its subagents, its last calls, tool calls, journal and messages (tool calls with their arguments and call ids, tool results with a preview of their text) |
| `diag.logs` | Log entries, filtered: `level` (at least), `category`, `contains`, `sinceMinutes`, `limit` |
| `diag.settings` | settings.json without secrets (API keys, tokens and passwords shown as their length; `env:NAME` references kept) |
| `diag.failures`, `diag.failure` | The requests a backend refused, saved in `logs/failed-requests` (the newest 30): time, model, the server's request and response ids, the error; one with its body |

Also useful: `runs.list` (every run), `agents.list` (the agents and their slots), `sessions.messages`, `agent.queue`,
`context.preview` (a chat's system prompt and tools), `models.list`, `plugins.list`, `usage.summary`, `budget.status`,
`processes.list`, `rpc.list`. The log files are in `<home>/logs/netpi-YYYYMMDD.log`.

## How it is kept

The diagnostics plugin (`plugins/NetPI.Diagnostics`) records in memory from its start: the last 300 model calls (a model
middleware around all others, so a call's retries belong to it), the last 500 tool calls, and a journal of the last
3000 events. A restart or a reload of the plugin starts them empty. The Diagnostics tab shows the same: a problems strip,
and a Calls view with each call's detail.
