# NetPI: handoff to Claude Code (2026-09-24)

NetPI was built in a Linux cloud sandbox by several agents working in parallel. It was then delivered here, built on Windows and run against the real model stack. This page covers what exists, what was verified, the decisions that must hold, and what to do next.

## Where things are

| | |
|---|---|
| Repo | `C:\AI\NetPI` (this folder) |
| Build output | `artifacts\app\`: `NetPI.exe` (WinForms + WebView2 desktop shell, starts the server in-process), `netpi-server.exe` (headless), `plugins\<Name>\`, `wwwroot\` |
| User data | `%USERPROFILE%\.netpi\`: `settings.json`, `netpi.db` (SQLite), `logs\netpi-YYYYMMDD.log`, `logs\failed-requests\`, `window.json`, `webview\`, `workspace\` |
| Old NetPI v1 | source `C:\AI\Projects\NetPI`; its data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`. Reference only; don't run it against the new data folder |
| Model stack | AiProxy `http://127.0.0.1:8090` (AiSwitcher, `C:\AI\AiSwitcher`) → nInfer `:8080` (source `C:\AI\src\ninfer-windows`, AiSwitcher profile `quasar-v3`), serving `qwen3.8-27b` with concurrency 2 and 2 × 262k KV. See `docs/AIPROXY-AGENT-GUIDE.md` |
| Claude | Anthropic Messages API with an API key (`providers.anthropic.apiKey` or `ANTHROPIC_API_KEY`). Only tested against a mock so far |

## Verification so far

- **Linux sandbox:** 5 unit suites (Providers 35, Tools 52, Agent 53, Aux 74, Host 38) and the end-to-end suite (`tests/NetPI.E2E`, 55 tests against the scripted mock model server `tests/MockLlm`, including a Playwright UI smoke test) all pass.
- **Windows:**
  - Verified: `build.ps1` builds, `NetPI.exe` runs, and Git Bash, pwsh, `winsqlite3.dll` and window placement work.
  - `.\build.ps1 -Test` and the E2E suite (55 tests, UI smoke included) pass. The Windows-only failures were all in the
    tests and harness (Git Bash quoting and `/tmp` spelling, a Debug-only build path, UI smoke browser and a race).
- **Real model:**
  - One real run exposed a bug in nInfer. nInfer has since been fixed; details below.
  - Smoke test after the fix (two agents, a subagent, a steer): lanes, steering and cache reuse behave. One problem was
    found: the minute in the system prompt's date line invalidates the cache (`docs/STATUS.md`, known limitations).

## Decisions and preferences to keep

- **Everything is a runtime-reloadable plugin.** The host kernel stays small. Contracts in `src/NetPI.Abstractions` change additively only. Plugins never reference each other; they use services, RPC and events. See `docs/PLUGINS.md`.
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

1. **Run the tests on Windows.** Close NetPI, run `.\build.ps1 -Test`, then `dotnet tests\NetPI.E2E\bin\Release\NetPI.E2E.dll --no-ui`. The UI test also needs Node and Playwright Chromium. Fix any failures specific to Windows: paths, Git Bash quoting, file locking, process-tree kill, CRLF.
2. **Real smoke test.** Run two concurrent agents on `aiproxy/qwen3.8-27b` in one project, including a spawned subagent and a steering message. Check that:
   - the lanes and queueing work;
   - cached tokens per turn are near the full prompt after the first turn;
   - no failed requests are saved;
   - the Work tab is correct.
3. **Claude provider live test.** Test with a real API key: thinking, tools, prompt caching, and the adaptive-thinking settings (`docs/SETTINGS.md`).
4. **Open items** in `docs/STATUS.md`, under "Known limitations / ideas".
5. **Idea:** show per-turn cache reuse (cached / prompt tokens, TTFT) in the chat or the Work tab. The data is already in each assistant message's `usage`.

## Working rules

- **Build.** Close NetPI before running `.\build.ps1`, because the running app locks its DLLs. While NetPI runs, `dotnet build plugins\<Name>` hot-reloads that plugin and `npm run build:plugins` reloads the plugin tabs.
- **Leave the user's live setup alone.** Don't modify `%USERPROFILE%\.netpi` data, and don't stop NetPI, AiSwitcher or nInfer without asking. The E2E suite and `netpi-server --home <temp>` use their own homes.
- **Tests and docs with every change.** Every behaviour change gets a test in the owning suite (the console runners in `tests/`). Also update `docs/PROTOCOL.md`, `docs/SETTINGS.md`, `docs/TOOLS.md` or `docs/UI.md` as relevant.
- **Commits.** Make small commits with descriptive messages.
