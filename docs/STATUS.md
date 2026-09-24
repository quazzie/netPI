# Status (2026-09-24)

## Done and verified (Linux, cloud sandbox)
- Host kernel, headless server, 16 plugins, Svelte UI + 4 plugin tabs (Work, Ideas, Diagnostics, Files).
- Unit suites: Providers 35, Tools 52, Agent 53, Aux 74, Host 38 — all passing.
- End-to-end suite against the scripted mock model server (`tests/MockLlm`): 55 tests / 687 checks passing,
  including a Playwright UI smoke test (tool runs, steering, abort, retry, 3 subagents on a 2-lane pool,
  compaction, hot reload, restart persistence).

Verified on Windows: the desktop shell builds and runs; Git Bash, pwsh, winsqlite3 and window placement work.

## Not yet verified
- The Anthropic provider was tested against a mock of the Messages API only (the adaptive-thinking request shape
  and the fallback model ids are best guesses; both are configurable).

## Backend (nInfer) — resolved 2026-09-24
- `response.failed: capture owner has no planning ID` was an nInfer bug (live-session commit 2fb27c35); fixed in
  `C:\AI\src\ninfer-windows`, which now also retains stateless (`store:false`) agent chains as lineage heads.
  Report: `C:\AI\src\ninfer-windows\.local\stateless-agents-20260924\report.md`. Real-model NetPI runs
  have not been repeated since — see docs/HANDOFF.md, next steps.

## Known limitations / ideas
- Steering an orchestrator that is waiting on its workers makes it stop waiting, but it still needs a lane back;
  if its own workers hold every lane of the pool, the reply waits for one of them to finish.
- Reloading the lanes plugin mid-run can briefly let a pool run more requests than its capacity.
- Models with ≤ 8k context are impractical (system prompt + tool schemas ≈ 4k tokens).
- `sessions.messages` has no `afterSeq`: after paging far back, "jump to latest" reloads the newest page.
- Old NetPI (c:\ai\projects\netpi) data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`.
