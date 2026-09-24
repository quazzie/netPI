# NetPI: handoff to Claude Code (2026-09-24)

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
- **Real models:** runs on nInfer (`qwen3.8-27b`) and on OpenRouter behave: lanes and queueing, subagents, steering,
  and cache reuse across turns, project switches and AGENTS.md edits (every turn reuses the previous prompt + output).
- **Not yet verified:** the Anthropic provider against the real API, and Linux/macOS since the Windows work.
- **Agent tools:** `web_fetch`, `web_search` (SearXNG / Brave), `screenshot` and `todo_write` work live with `qwen3.8-27b`;
  tools that appear or disappear mid-session are announced with a notice. The `ssh_*` tools work against the hosts in
  `~/.ssh/config` (tested live on `nuc` and `server`).
- **Goals** (`plugins/NetPI.Goal`, `/goal`): the agent is started again after every run until it marks the goal
  complete; tested with a scripted model, not yet with a real one.
- **Chat UI:** the streamed answer is laid out as the finished one will be, so nothing jumps between steps; a Steps
  preference (expanded / fold when done / folded), chat width, zoom and spellcheck are in Settings.
- **History:** the Windows bring-up, the first smoke tests and the prompt work are recorded in
  `docs/archive/2026-09-24-windows-bringup.md`; the agent tools and projects UI in `docs/archive/2026-09-24-agent-tools.md`;
  the SSH tools in `docs/archive/2026-09-24-ssh-tools.md`; goals and the steady chat in
  `docs/archive/2026-09-24-goals.md`.

## Decisions and preferences to keep

- **Everything is a runtime-reloadable plugin.** The host kernel stays small. Contracts in `src/NetPI.Abstractions` change additively only. Plugins never reference each other; they use services, RPC and events. See `docs/PLUGINS.md`.
- **Never rewrite what was sent; only append.** A session's system prompt is frozen at its first model call and contains nothing session-dependent. State changes (project, working directory, AGENTS.md) are appended as notices by the plugin that owns them; the host stores data and publishes events but writes no model-facing text. Tools are sent sorted by name. The only exceptions are compaction and tool-call repair. See `docs/PLUGINS.md`, "Never rewrite what was sent".
- **Tools stay live in running sessions.** A tool added or removed mid-session (plugin enabled, disabled or rebuilt) is sent from the next model call and announced with a "tools" notice; the backend re-reads the conversation once. The user prefers that to freezing the tool list per session (decided 2026-09-24).
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

0. **Lanes you set up per model, and settings controls** (`docs/plans/lanes-and-settings.md`): a lane is a model
   with a capacity, a note on when to use it and a budget (cost inferred); agents pick one by id from `lanes_list`;
   real controls in the settings dialog. Simplified with the user; waiting for "go".
1. **Goals with a real model.** Run a goal on `qwen3.8-27b` (a throwaway server or the user's NetPI): does it keep
   working, call `goal_update` complete only when done, stay within the no-progress rule? Then consider an
   independent check of "complete" (a verifier subagent) if it declares done too early.
2. **Claude provider live test.** Needs an Anthropic API key (none available yet): thinking, tools, prompt caching, and the
   adaptive-thinking settings (`docs/SETTINGS.md`). Claude through OpenRouter (paid) would exercise OpenRouter's side of
   it (signed `reasoning_details`, `cache_control`), not the native provider.
3. **Per-turn cache reuse and TTFT** in the chat or the Work tab. Cached/prompt tokens are in each assistant message's
   `usage`; TTFT is not recorded yet (the agent runner could store the time to the first delta in the message meta).
4. **OpenRouter follow-ups:** let the retry plugin honor `Retry-After` (an additive `ModelException` field), show the cost
   stored in `meta.openrouter.cost`.
5. **Open items** in `docs/STATUS.md`, under "Known limitations / ideas".

## Working rules

- **Build.** While NetPI runs, `.\build.ps1` builds everything except the host (the running app locks `NetPI.Host.dll` and the contracts) and the plugins hot-reload; it says when the host changed and needs NetPI closed. `dotnet build plugins\<Name>` hot-reloads one plugin, `npm run build:plugins` the plugin tabs.
- **Leave the user's live setup alone.** Don't modify `%USERPROFILE%\.netpi` data, and don't stop NetPI, AiSwitcher or nInfer without asking. The E2E suite and `netpi-server --home <temp>` use their own homes.
- **Tests and docs with every change.** Every behaviour change gets a test in the owning suite (the console runners in `tests/`). Also update `docs/PROTOCOL.md`, `docs/SETTINGS.md`, `docs/TOOLS.md` or `docs/UI.md` as relevant.
- **Commits.** Make small commits with descriptive messages.
- **Plans.** Write a plan for larger work to `docs/plans/<topic>.md`. When a plan is done, record it in `docs/archive/YYYY-MM-DD-<topic>.md` (what was done, results, commits)
  and keep this page and `docs/STATUS.md` about the current state only.
