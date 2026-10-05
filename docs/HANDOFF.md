# NetPI: handoff to Claude Code (2026-09-25)

NetPI was built in a Linux cloud sandbox by several agents working in parallel, then brought up on Windows and run against the real model stack. This page covers where things are, the current state, the decisions that must hold, and what to do next. Test results and open issues are in `docs/STATUS.md`; completed plans are archived in `docs/archive/`.

## Where things are

| | |
|---|---|
| Repo | `C:\AI\Projects\NetPI` (this folder) |
| Build output | `artifacts\dev\app\` (what a build makes: `NetPI.exe` — WinForms + WebView2 desktop shell, starts the server in-process — `netpi-server.exe`, `plugins\<Name>\`, `wwwroot\`), installed into `artifacts\app\` by `build.ps1 -Publish` |
| User data | `%USERPROFILE%\.netpi\`: `settings.json`, `netpi.db` (SQLite), `logs\netpi-YYYYMMDD.log`, `logs\failed-requests\`, `window.json`, `webview\`, `workspace\` |
| Old NetPI v1 | its data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`. Reference only; don't run it against the new data folder |
| Model stack | AiProxy `http://127.0.0.1:8090` (AiSwitcher, `C:\AI\archived\apps\AiSwitcher-legacy`) → nInfer `:8080` (source `C:\AI\Projects\ninfer-windows`, AiSwitcher profile `quasar-v3`), serving `qwen3.8-27b` with concurrency 2 and 2 × 262k KV. See `docs/AIPROXY-AGENT-GUIDE.md` |
| Claude | Anthropic Messages API with an API key (`providers.anthropic.apiKey` or `ANTHROPIC_API_KEY`). Only tested against a mock so far (no API key available) |
| OpenRouter | `providers.openrouter.apiKey` or `OPENROUTER_API_KEY`; tested live with the free `stealth/space-bunny-alpha` |
| Plans | `docs/plans/`: active plans (open questions at the end); `docs/archive/`: one dated record per completed plan (what was done, results, commits) |

## Current state (2026-09-28)

### Storage port and the core diet (2026-10-02)

The swap-over of [docs/archive/2026-10-02-replaceable-parts.md](archive/2026-10-02-replaceable-parts.md) is written and
passed its final gate (zero-warning build, six unit suites, the whole e2e suite, `core-size`); the revert point is the
tag **`pre-swapover-2026-10-02`** (master before it; `git reset --hard pre-swapover-2026-10-02` or branch from it). It
is **installed** in the owner's app: the one-off migration of their `netpi.db` ran on 2026-10-02 and was verified (every
count and the messages' checksum equal); the tool is deleted and lives in git history at `c6480ff`
(`scripts/migrations/001-storage-port`). The owner's home still holds `netpi.db.pre-storage-port` (the old database,
the one-step rollback) and `%USERPROFILE%\netpi-pre-swapover-2026-10-02` holds a snapshot and the old app folder.
Storage is now a port: `IStorageProvider` chosen by
`storage.provider` (default `sqlite`, `--ephemeral` = `memory`), the only SQL lives in
`src/NetPI.Host/Storage/Sqlite`, and a plugin keeps its data in `ctx.Data` as collections of JSON documents —
`tests/NetPI.Storage.Tests` is the contract in executable form and a new provider must pass it. A session has no
workspace column: the Workspaces plugin writes `meta.workspaceId` and `meta.cwd`, and the budget ledger belongs to
the Agents plugin, so the core refuses no model and polices no money. `node scripts/core-size.mjs` keeps the kernel
to the harness's own concepts.

The review fixes are in master: unique approval IDs, cleared fork permissions, atomic
spending reads, persistent per-attempt budget reservations, and the backup plugin plus offline restore.
[STATUS.md](STATUS.md) is the current state and limitations; [TESTING.md](TESTING.md) records fresh validation.
[BACKUPS.md](BACKUPS.md) explains snapshot scope, retention and recovery.

Earlier real-model results and deployment notes are historical, preserved in
[archive/2026-09-25-status.md](archive/2026-09-25-status.md). Do not treat old “not yet deployed” notes as current
machine state. Live Anthropic, paid billing reconciliation, and Linux/macOS still need fresh verification.

## Decisions and preferences to keep

- **Everything is a runtime-reloadable plugin.** The host kernel stays small. Contracts in `src/NetPI.Abstractions` change freely when the design needs it (nothing outside this repository depends on them): no compatibility layers, and data a change strands is migrated for the owner's local setup by a one-off script, not by product code. The higher abstractions' vocabularies live in `src/NetPI.Contracts`, which the kernel must not reference; `node scripts/core-size.mjs` checks both rules. Plugins never reference each other; they use services, RPC and events. See `docs/PLUGINS.md`.
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
  budget is spent (decided 2026-09-24/25). The ledger, the caps and the reservations are the Agents plugin's own
  (`ctx.Data`); the core's catalog refuses no model, so without that plugin nothing meters or limits a paid call.
- **The Responses transport is standard and stateless.** It sends `store:false` and the full input every call, and replays reasoning items (`reasoning` → `message` → `function_call`). No `previous_response_id` chaining.
- **No workarounds that hide backend problems.** No silent retries or self-healing for backend failures: what is
  retried is announced in the chat and bounded.
  - Errors show up unchanged, with the server's `x-request-id` and response id.
  - Failed request bodies are saved to `logs\failed-requests\`.
  - The retry plugin covers what the provider itself calls transient: lost connections, stalled streams, rate limits
    and overload (429/529, waiting at least the `Retry-After`) and 5xx/server-error responses
    (`shared/ProviderKit/ProviderErrors.cs`), up to `retry.maxAttempts`; after that the error surfaces unchanged, with
    its request id. The e2e suite pins this (`retry.503`, `retry.in-stream-errors`).
- **Narrow side panels.** The user keeps them at about 230–320px. Every tab is designed narrow first: no horizontal scroll, single-line rows with truncation. Screenshots the user shares are weak references only; the design is our own.
- **Minimal dependencies.** The only NuGet package is WebView2. SQLite is a P/Invoke wrapper over the OS library. The UI uses Svelte 5 + Vite with marked, DOMPurify and highlight.js. Ask before adding dependencies.
- **Committed UI builds.** The built bundles (`web/dist`, `plugins/*/wwwroot/ui.js`) are committed, so building needs Node only when the UI changes.
- **Line endings.** The file tools are CRLF/LF agnostic and preserve each file's line endings and BOM.

## nInfer findings that matter for NetPI

Source: `C:\AI\Projects\ninfer-windows\.local\stateless-agents-20260924\report.md`.

- **Crash fixed.** `capture owner has no planning ID` is fixed. `response.failed` now carries `error.code: "server_error"`.
- **Stateless requests keep their KV-cache context.** nInfer now tracks each stateless `store:false` conversation by its prompt prefix and protects its latest cached state. In a real test, two concurrent agents ran 17 tool turns each up to ~253k tokens with side requests: no cold re-prefills and no fallbacks.
- **Keep reasoning replay on** (`providers.aiproxy.replayReasoning`, the Responses default). nInfer runs with `--preserve-thinking`; together there is no re-prefill of the whole run after a mid-run user message or notice.
- **Two settings must stay unset for nInfer:** `reasoningSummary` and `includeEncryptedReasoning`. nInfer rejects `reasoning.summary` and non-empty `include` with HTTP 400.
- **Cache reporting is accurate.** `usage.input_tokens_details.cached_tokens` equals the reused prefix, so NetPI's usage and cached figures can be trusted.

## Suggested next steps

1. **Goals with a real model.** Run a goal on `qwen3.8-27b` (a throwaway server or the user's NetPI): does it keep
   working, call `goal_update` complete only when done, stay within the no-progress rule? Then consider an
   independent check of "complete" (a verifier subagent) if it declares done too early.
2. **Claude provider live test.** Needs an Anthropic API key (none available yet): thinking, tools, prompt caching, and the
   adaptive-thinking settings (`docs/SETTINGS.md`). Claude through OpenRouter (paid) would exercise OpenRouter's side of
   it (signed `reasoning_details`, `cache_control`), not the native provider.
3. **Try the harness gaps with a real model** (all built, `docs/archive/2026-09-26-harness-gaps.md`): does `qwen3.8-27b`
   use `ask_user` for real decisions and not for routine steps, close ideas when done, and keep the cache when a chat
   is forked (the per-turn line shows it)? The guardrails' defaults block only the catastrophic; see whether they need more.
4. **Plan mode with a real model** (`plugins/NetPI.Plan`, `docs/TOOLS.md` "Plan mode"; built 2026-10-03): does `qwen3.8-27b`
   explore with grep/find/read instead of reaching for the shell, pick a research agent from `agent_choices`, and submit a plan that
   is concrete? The Home Assistant MCP server (ha-mcp) marks its reading tools with `readOnlyHint: true`
   (checked in its source: `ha_search` does), so they pass without setting anything; confirm with `mcp.tools` (`readOnly`) on the
   user's NetPI. `plan.mcpAllow` (`ha_get_*`, `ha_list_*`) covers any a server forgets to mark. Not built yet: a read-only git tool for plan mode, plan revision diffs.
5. **Open items** in `docs/STATUS.md`, under "Known limitations / ideas".

## Working rules

- **Build.** The user runs builds from cmd: `build` (`build.cmd` runs `build.ps1` with the same options), and starts NetPI from `artifacts\app\NetPI.exe` (pinned to the taskbar). A warning is a build error (`Directory.Build.props`, `TreatWarningsAsErrors`), and `global.json` pins the SDK to 10.0.100 with `rollForward: latestFeature` (any 10.0.xxx band). A build lands in `artifacts\dev\app` and never touches the running app — `artifacts\app` is the installed build, and only `.\build.ps1 -Publish` writes there. Publishing while NetPI runs swaps changed plugins while the old versions still serve, so their tools remain available; the host files it replaces are moved into `artifacts\app\.old` (a running exe or DLL can be renamed, not overwritten), so the next start of NetPI runs the new host; after a contract change the plugins wait in `artifacts\app\.pending`, and that start installs them. The user works in their NetPI: publish with `.\build.ps1 -Publish` by default. Use `-Publish -NextStart` when the owner is mid-turn or asks to defer changes (plugins and the web UI wait in `.pending` too); `-Pending` lists what waits. When it prints "Ready for the next start", tell the user to restart NetPI. One plugin live on purpose: `dotnet build plugins\<Name> -p:AppOutDir=artifacts/app/ -p:BuildProjectReferences=false` (`AppOutDir` is the app folder; a plugin's output is `$(AppOutDir)plugins/<Name>/`, where the host loads it from).
- **Leave the user's live setup alone.** Don't modify `%USERPROFILE%\.netpi` data, and don't stop NetPI, AiSwitcher or nInfer without asking. The E2E suite and `netpi-server --home <temp>` use their own homes.
- **Tests and docs with every change.** Every behaviour change gets a test in the owning suite (the console runners in `tests/`). Also update `docs/PROTOCOL.md`, `docs/SETTINGS.md`, `docs/TOOLS.md` or `docs/UI.md` as relevant.
- **End-to-end by scope, not whole.** `.\scripts\e2e.ps1 -Changed` (or `-Only <id>`, `-Smoke`, `-Failed`) for the affected behavior, including before a merge; full suites are reserved for shared contracts, cross-cutting behavior or an explicit request. Docs, formatting and line-ending edits need their relevant checks, not a full gate; a UI section with `npm run e2e -- --only "<section>"`. A run lists every failure with its evidence under `artifacts/e2elogs`; a test that fails sometimes is measured with `-Only <id> -Repeat 20 -Fresh`, never retried. See `docs/TESTING.md` and `docs/archive/2026-09-30-e2e-feedback.md`.
- **Commits.** Make small commits with descriptive messages.
- **Plans.** Write a plan for larger work to `docs/plans/<topic>.md`. When a plan is done, record it in `docs/archive/YYYY-MM-DD-<topic>.md` (what was done, results, commits)
  and keep this page and `docs/STATUS.md` about the current state only.
