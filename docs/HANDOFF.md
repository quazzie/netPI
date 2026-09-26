# NetPI: handoff to Claude Code (2026-09-25)

NetPI was built in a Linux cloud sandbox by several agents working in parallel, then brought up on Windows and run against the real model stack. This page covers where things are, the current state, the decisions that must hold, and what to do next. Test results and open issues are in `docs/STATUS.md`; completed plans are archived in `docs/archive/`.

## Where things are

| | |
|---|---|
| Repo | `C:\AI\NetPI` (this folder) |
| Build output | `artifacts\app\`: `NetPI.exe` (WinForms + WebView2 desktop shell, starts the server in-process), `netpi-server.exe` (headless), `plugins\<Name>\`, `wwwroot\` |
| User data | `%USERPROFILE%\.netpi\`: `settings.json`, `netpi.db` (SQLite), `logs\netpi-YYYYMMDD.log`, `logs\failed-requests\`, `window.json`, `webview\`, `workspace\` |
| Old NetPI v1 | source `C:\AI\Projects\NetPI`; its data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`. Reference only; don't run it against the new data folder |
| Model stack | AiProxy `http://127.0.0.1:8090` (AiSwitcher, `C:\AI\AiSwitcher`) → nInfer `:8080` (source `C:\AI\src\ninfer-windows`, AiSwitcher profile `quasar-v3`), serving `qwen3.8-27b` with concurrency 2 and 2 × 262k KV. See `docs/AIPROXY-AGENT-GUIDE.md` |
| Claude | Anthropic Messages API with an API key (`providers.anthropic.apiKey` or `ANTHROPIC_API_KEY`). Only tested against a mock so far (no API key available) |
| OpenRouter | `providers.openrouter.apiKey` or `OPENROUTER_API_KEY`; tested live with the free `stealth/space-bunny-alpha` |
| Plans | `docs/plans/`: active plans (open questions at the end); `docs/archive/`: one dated record per completed plan (what was done, results, commits) |

## Current state

- **Windows:** all five unit suites and the E2E suite (UI smoke included) pass; counts are in `docs/STATUS.md`.
- **Real models:** runs on nInfer (`qwen3.8-27b`) and on OpenRouter behave: queueing, subagents, steering,
  and cache reuse across turns, project switches and AGENTS.md edits (every turn reuses the previous prompt + output).
- **Not yet verified:** the Anthropic provider against the real API, and Linux/macOS since the Windows work.
- **Agent tools:** `web_fetch`, `web_search` (SearXNG / Brave), `screenshot` and `todo_write` work live with `qwen3.8-27b`;
  tools that appear or disappear mid-session are announced with a notice. The `ssh_*` tools work against the hosts in
  `~/.ssh/config` (tested live on `nuc` and `server`).
- **Goals** (`plugins/NetPI.Goal`, `/goal`): the agent is started again after every run until it marks the goal
  complete; tested with a scripted model, not yet with a real one.
- **Agents, budget, settings** (built 2026-09-24/25, not yet deployed to the user's NetPI): an agent is a model with
  instances and a note on when to use it; chats run on agents (the composer's agent picker) and subagents pick one
  (`agent_choices`, `agent_spawn { agent }`). An agent is active only while its model is loaded (NetPI never loads a
  model) and can be switched off in Settings or the Work tab. The user's lanes become agents on the first start. Every
  model call is recorded with its cost against one monthly budget (paid calls stop or ask when it is spent). The
  settings dialog renders the host's and every plugin's settings as controls; each chat switches its own tools.
- **Compaction works like pi's** (built 2026-09-25, not yet deployed): a structured checkpoint at the chat's reasoning
  effort, the files read and modified, split turns; long tool results go to a file the agent reads from.
- **Skills** (`plugins/NetPI.Skills`, built 2026-09-25, not yet deployed): the Agent Skills standard. SKILL.md folders
  in the project (`.agents/skills`, `.netpi/skills`, up to the git root) and globally (`~/.agents/skills`,
  `~/.netpi/skills`); the catalog reaches agents as a `skills` notice, the `skill` tool loads one, `/skill:name` loads one
  for the user's message. Tested with a scripted model, not yet with a real one. See `docs/PLUGIN-SKILLS.md`.
- **Profiles** (built 2026-09-24, not yet deployed): the opening of a chat's system prompt and its tools; a default per
  project; switching is free before the first message and one full re-read after it.
- **Decisions** (`plugins/NetPI.Decide`, built and hot-loaded into the user's NetPI 2026-09-26): the `decide` tool and
  `decide.ask` RPC ask a decision model (Kev, TypeSafe's `/v1/systemone`) typed yes/no, pick-one and score questions
  about one text or every line of a file, through AiGateway. `kev-9b`/`kev-4b` are nuc router models (the router runs
  a build of the Kev llama.cpp fork); switch to one in AiHub (it also unloads yue2). `qwen3.8-27b` works too, with no
  switch: AiGateway answers System One for NInfer models through NInfer's `/v1/decision` (fork, `local/main`, ~35 ms
  per question; it shares NInfer's two slots with the agents). The AiProxy provider leaves models
  with an `api` out of the chat model list. Which model for which task (the results matrix):
  `docs/DECISION-MODELS.md`; every experiment behind it: `docs/archive/2026-09-26-decisions.md`; what comes next
  (baselines, multi-prefill in NInfer, decisions inside NetPI): `docs/DECISION-ROADMAP.md`.
- **Chat UI:** the streamed answer is laid out as the finished one will be, so nothing jumps between steps; a Steps
  preference (expanded / fold when done / folded), chat width, zoom and spellcheck are in Settings.
- **History:** the Windows bring-up, the first smoke tests and the prompt work are recorded in
  `docs/archive/2026-09-24-windows-bringup.md`; the agent tools and projects UI in `docs/archive/2026-09-24-agent-tools.md`;
  the SSH tools in `docs/archive/2026-09-24-ssh-tools.md`; goals and the steady chat in
  `docs/archive/2026-09-24-goals.md`; lanes, the budget and the settings dialog in
  `docs/archive/2026-09-24-lanes-budget-settings.md`; profiles in `docs/archive/2026-09-24-profiles.md`; agents instead of
  lanes in `docs/archive/2026-09-25-agents.md`.

## Decisions and preferences to keep

- **Everything is a runtime-reloadable plugin.** The host kernel stays small. Contracts in `src/NetPI.Abstractions` change additively only. Plugins never reference each other; they use services, RPC and events. See `docs/PLUGINS.md`.
- **Never rewrite what was sent; only append.** A session's system prompt is frozen at its first model call and contains nothing session-dependent. State changes (project, working directory, AGENTS.md) are appended as notices by the plugin that owns them; the host stores data and publishes events but writes no model-facing text. Tools are sent sorted by name. The only exceptions are compaction and tool-call repair. See `docs/PLUGINS.md`, "Never rewrite what was sent".
- **Tools stay live in running sessions.** A tool added or removed mid-session (plugin enabled, disabled or rebuilt) is sent from the next model call and announced with a "tools" notice; the backend re-reads the conversation once. The user prefers that to freezing the tool list per session (decided 2026-09-24).
- **Tools are switched per chat; globally, whole plugins are.** The composer's tools button (`meta.toolsOff`); a change
  in a started chat applies from the next model call with a notice, at the price of one re-read; subagents inherit it
  (decided 2026-09-24).
- **Profiles hold instructions and tools, not the model; subagents get no profile.** The agent that starts a subagent
  chooses its tools, including ones it lacks itself (a limited orchestrator dispatches agents that can do more); by
  default its own (decided 2026-09-24).
- **Chats and subagents run on agents the user sets up, and one global budget guards the money.** An agent is a model
  with instances; to use a model you set up an agent on it (no temporary agents). An agent is active only while its
  model is loaded: NetPI never makes AiProxy load a model, since that would unload what the other agents run on.
  Profiles stay separate (the role, not where it runs). Paid calls are recorded and stop (or ask) when the monthly
  budget is spent (decided 2026-09-24/25).
- **The Responses transport is standard and stateless.** It sends `store:false` and the full input every call, and replays reasoning items (`reasoning` → `message` → `function_call`). No `previous_response_id` chaining.
- **No workarounds that hide backend problems.** No retries or self-healing for backend failures.
  - Errors show up unchanged, with the server's `x-request-id` and response id.
  - Failed request bodies are saved to `logs\failed-requests\`.
  - The retry plugin only covers lost connections and stalled streams.
- **Narrow side panels.** The user keeps them at about 230–320px. Every tab is designed narrow first: no horizontal scroll, single-line rows with truncation. Screenshots the user shares are weak references only; the design is our own.
- **Minimal dependencies.** The only NuGet package is WebView2. SQLite is a P/Invoke wrapper over the OS library. The UI uses Svelte 5 + Vite with marked, DOMPurify and highlight.js. Ask before adding dependencies.
- **Committed UI builds.** The built bundles (`web/dist`, `plugins/*/wwwroot/ui.js`) are committed, so building needs Node only when the UI changes.
- **Line endings.** The file tools are CRLF/LF agnostic and preserve each file's line endings and BOM.

## nInfer findings that matter for NetPI

Source: `C:\AI\src\ninfer-windows\.local\stateless-agents-20260924\report.md`.

- **Crash fixed.** `capture owner has no planning ID` is fixed. `response.failed` now carries `error.code: "server_error"`.
- **Stateless requests keep their KV-cache context.** nInfer now tracks each stateless `store:false` conversation by its prompt prefix and protects its latest cached state. In a real test, two concurrent agents ran 17 tool turns each up to ~253k tokens with side requests: no cold re-prefills and no fallbacks.
- **Keep reasoning replay on** (`providers.aiproxy.replayReasoning`, the Responses default). nInfer runs with `--preserve-thinking`; together there is no re-prefill of the whole run after a mid-run user message or notice.
- **Two settings must stay unset for nInfer:** `reasoningSummary` and `includeEncryptedReasoning`. nInfer rejects `reasoning.summary` and non-empty `include` with HTTP 400.
- **Cache reporting is accurate.** `usage.input_tokens_details.cached_tokens` equals the reused prefix, so NetPI's usage and cached figures can be trusted.

## Suggested next steps

0. **Deploy the renamed plugins.** The lanes plugin is now `netpi.agents` (`plugins/NetPI.Agents`) and the runtime
   `netpi.runtime` (`plugins/NetPI.Runtime`); `agents.list` lists the agents, `runs.list` the runs. Close NetPI and
   `build` (cmd): with NetPI closed the build removes the old `artifacts\app\plugins\NetPI.Lanes` and `NetPI.Agent`
   output (else both would load), and the first start moves `lanes.localDefaultCapacity`/`cloudDefaultCapacity` to
   `models.localSlots`/`cloudSlots`. Then: switch the model in AiSwitcher and watch the Work tab's agents follow within
   ~10 s; watch a local agent pick agents (and a paid OpenRouter call's cost reach the ledger).
1. **Goals with a real model.** Run a goal on `qwen3.8-27b` (a throwaway server or the user's NetPI): does it keep
   working, call `goal_update` complete only when done, stay within the no-progress rule? Then consider an
   independent check of "complete" (a verifier subagent) if it declares done too early.
2. **Claude provider live test.** Needs an Anthropic API key (none available yet): thinking, tools, prompt caching, and the
   adaptive-thinking settings (`docs/SETTINGS.md`). Claude through OpenRouter (paid) would exercise OpenRouter's side of
   it (signed `reasoning_details`, `cache_control`), not the native provider.
3. **Try the harness gaps with a real model** (all built, `docs/archive/2026-09-26-harness-gaps.md`): does `qwen3.8-27b`
   use `ask_user` for real decisions and not for routine steps, close ideas when done, and keep the cache when a chat
   is forked (the per-turn line shows it)? The guardrails' defaults block only the catastrophic; see whether they need more.
4. **Open items** in `docs/STATUS.md`, under "Known limitations / ideas".

## Working rules

- **Build.** The user runs builds from cmd: `build` (`build.cmd` runs `build.ps1` with the same options), and starts NetPI from `artifacts\app\NetPI.exe` (pinned to the taskbar). Build with `.\build.ps1` even while NetPI runs: it builds into `artifacts\build\stage` (a failed build changes nothing), plugins hot-reload, and the host files it replaces are moved into `artifacts\app\.old` (a running exe or DLL can be renamed, not overwritten), so the next start of NetPI runs the new host; after a contract change the plugins wait in `artifacts\app\.pending`, and that start installs them. The user works in their NetPI: build with `.\build.ps1 -NextStart` unless they want a change live now, so nothing changes under a running chat (plugins and the web UI wait in `.pending` too). When it prints "Ready for the next start", tell the user to restart NetPI. `dotnet build plugins\<Name>` hot-reloads one plugin, `npm run build:plugins` the plugin tabs.
- **Leave the user's live setup alone.** Don't modify `%USERPROFILE%\.netpi` data, and don't stop NetPI, AiSwitcher or nInfer without asking. The E2E suite and `netpi-server --home <temp>` use their own homes.
- **Tests and docs with every change.** Every behaviour change gets a test in the owning suite (the console runners in `tests/`). Also update `docs/PROTOCOL.md`, `docs/SETTINGS.md`, `docs/TOOLS.md` or `docs/UI.md` as relevant.
- **Commits.** Make small commits with descriptive messages.
- **Plans.** Write a plan for larger work to `docs/plans/<topic>.md`. When a plan is done, record it in `docs/archive/YYYY-MM-DD-<topic>.md` (what was done, results, commits)
  and keep this page and `docs/STATUS.md` about the current state only.
