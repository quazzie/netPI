# Status (2026-09-24)

## Done and verified (Linux, cloud sandbox)
- Host kernel, headless server, 16 plugins, Svelte UI + 4 plugin tabs (Work, Ideas, Diagnostics, Files).
- Unit suites: Providers 35, Tools 52, Agent 53, Aux 74, Host 38 — all passing.
- End-to-end suite against the scripted mock model server (`tests/MockLlm`): 55 tests / 687 checks passing,
  including a Playwright UI smoke test (tool runs, steering, abort, retry, 3 subagents on a 2-lane pool,
  compaction, hot reload, restart persistence).

## Verified on Windows (2026-09-24)
- The desktop shell builds and runs; Git Bash, pwsh, winsqlite3 and window placement work.
- `.\build.ps1 -Test` (Release): Providers 41, Tools 53, Agent 58, Aux 74, Host 38, all passing.
- E2E suite: 55 tests / 703 checks passing, including the Playwright UI smoke (run on Edge); the UI mock e2e
  (`npm run e2e`) 71/71.
- Plugins hot-reload while NetPI runs (they load from shadow copies); only the host DLLs are locked. `build.ps1` then
  builds everything except the host, and a rebuild after a commit no longer reloads unchanged plugins.
- Real model (AiProxy → nInfer `qwen3.8-27b`, concurrency 2): two concurrent agents, one spawning a subagent, the other
  steered mid-run. Never more than 2 requests in flight; the subagent queued until its parent yielded in `agent_wait`
  and the parent resumed with priority; the steer arrived as a `[steer]` message at the next turn boundary; continuation
  turns reused the previous prompt + output exactly (88–99.7 % cached, 115–220 ms TTFT); no failed requests. The one
  cache miss came from the minute in the system prompt's date line, which is gone now.
- OpenRouter, live (`stealth/space-bunny-alpha`, free): the same two-agent run with a subagent and a steer. 14 turns,
  3 agents in parallel on the `openrouter` pool, `reasoning_details` replayed on later turns without errors, 86–99.6 %
  cached after the first turn, ~0.9–1.2 s TTFT, generation ids and the upstream provider recorded, no failed requests.
- Prefix kept across state changes (nInfer, live): one session in project alpha, switched to project beta, then beta's
  AGENTS.md edited. Every turn reused exactly the previous prompt + output; after the switch 5662 of 5967 prompt tokens
  were cached (the rest: the project and instructions notices plus the new message). The frozen system prompt held no
  path or AGENTS.md text, and the model followed beta's rules, then the edited ones.

## Not yet verified
- The Anthropic provider was tested against a mock of the Messages API only (the adaptive-thinking request shape
  and the fallback model ids are best guesses; both are configurable).

## Backend (nInfer) — resolved 2026-09-24
- `response.failed: capture owner has no planning ID` was an nInfer bug (live-session commit 2fb27c35); fixed in
  `C:\AI\src\ninfer-windows`, which now also retains stateless (`store:false`) agent chains as lineage heads.
  Report: `C:\AI\src\ninfer-windows\.local\stateless-agents-20260924\report.md`. The Windows smoke test above ran
  on the fixed build without backend failures.

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
  changes the tool definitions, so the backend re-prefills once. The frozen system prompt keeps the tool guidelines of
  the session's first call.
- `sessions.messages` has no `afterSeq`: after paging far back, "jump to latest" reloads the newest page.
- Old NetPI (c:\ai\projects\netpi) data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`.
