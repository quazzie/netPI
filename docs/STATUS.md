# Status (2026-09-24)

## Verified on Windows
- The desktop shell builds and runs; Git Bash, pwsh, winsqlite3 and window placement work.
- `.\build.ps1 -Test` (Release): Providers 41, Tools 54, Agent 60, Aux 83, Host 38, all passing.
- E2E suite: 55 tests / 703 checks passing, including the Playwright UI smoke (run on Edge); the UI mock e2e
  (`npm run e2e`) 105/105.
- Plugins hot-reload while NetPI runs (they load from shadow copies); only the host DLLs are locked. `build.ps1` then
  builds everything except the host, and a rebuild after a commit no longer reloads unchanged plugins.
- Real model (AiProxy → nInfer `qwen3.8-27b`, 2 lanes): two concurrent agents, one spawning a subagent, the other
  steered mid-run. Lanes, queueing and yielding are correct, the steer arrives at the next turn boundary, and continuation
  turns reuse the previous prompt + output exactly. No failed requests.
- OpenRouter, live (`stealth/space-bunny-alpha`, free): the same run works, with `reasoning_details` replayed on later
  turns and 86–99.6 % of each prompt cached after the first turn.
- The cached prefix survives state changes (nInfer): after a project switch and an edited AGENTS.md, every turn reused
  exactly the previous prompt + output; the changes arrived as appended notices.
- Web tools, todo and tool notices, live (nInfer `qwen3.8-27b` through a throwaway server): the model planned with
  `todo_write`, searched through SearXNG, read the docs page with `web_fetch` and answered correctly; it read the
  headline off a `screenshot` of svelte.dev; disabling and re-enabling the web plugin mid-session produced "tools"
  notices it answered from. Every turn reused the previous prompt + output except the one after the tool set changed.
- Numbers and details: `docs/archive/2026-09-24-windows-bringup.md`, `docs/archive/2026-09-24-agent-tools.md`.

## Not yet verified
- The Anthropic provider was tested against a mock of the Messages API only (the adaptive-thinking request shape
  and the fallback model ids are best guesses; both are configurable).
- Linux/macOS: the suites last ran in the Linux sandbox, before the Windows work, and have not been re-run since.

## Known limitations / ideas
- OpenRouter: a 429 reports its `Retry-After`, but the retry plugin keeps its own backoff; each answer's cost is stored
  (`meta.openrouter.cost`) but not shown yet; the catalog offers every tool-capable model (~390; narrow it with
  `providers.openrouter.include`).
- Per-turn cache reuse and TTFT are not shown in the UI (usage is in each assistant message; TTFT is not recorded).
- Steering an orchestrator that is waiting on its workers makes it stop waiting, but it still needs a lane back;
  if its own workers hold every lane of the pool, the reply waits for one of them to finish.
- Reloading the lanes plugin mid-run can briefly let a pool run more requests than its capacity.
- Models with ≤ 8k context are impractical (system prompt + tool schemas ≈ 4k tokens).
- Changing the tool set mid-session (a tool plugin enabled, disabled or reloaded with new tools; `tools.disabled`)
  changes the tool definitions, so the backend re-prefills once. A "tools" notice tells the model what changed and
  carries the new tools' guidelines.
- `screenshot` without a url needs the desktop app's `desktop.capture` (a desktop shell built after 2026-09-24); in the
  headless server it asks for a url.
- `sessions.messages` has no `afterSeq`: after paging far back, "jump to latest" reloads the newest page.
- Projects live in the host (the store, the `projects.*` RPC and the Projects panel); only what the model is told about
  them comes from plugins. Moving projects entirely into a plugin was discussed on 2026-09-24 but not decided.
