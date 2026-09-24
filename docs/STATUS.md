# Status (2026-09-24)

## Done and verified (Linux, cloud sandbox)
- Host kernel, headless server, 16 plugins, Svelte UI + 4 plugin tabs (Work, Ideas, Diagnostics, Files).
- Unit suites: Providers 35, Tools 52, Agent 53, Aux 74, Host 38 — all passing.
- End-to-end suite against the scripted mock model server (`tests/MockLlm`): 55 tests / 687 checks passing,
  including a Playwright UI smoke test (tool runs, steering, abort, retry, 3 subagents on a 2-lane pool,
  compaction, hot reload, restart persistence).

## Verified on Windows (2026-09-24)
- The desktop shell builds and runs; Git Bash, pwsh, winsqlite3 and window placement work.
- `.\build.ps1 -Test` (Release): Providers 35, Tools 52, Agent 53, Aux 74, Host 38, all passing.
- E2E suite: 55 tests / 683 checks passing, including the Playwright UI smoke (run on Edge).
- Plugins hot-reload while NetPI runs (they load from shadow copies); only the host DLLs are locked, so `build.ps1`
  needs NetPI closed.
- Real model (AiProxy → nInfer `qwen3.8-27b`, concurrency 2): two concurrent agents, one spawning a subagent, the other
  steered mid-run. Never more than 2 requests in flight; the subagent queued until its parent yielded in `agent_wait`
  and the parent resumed with priority; the steer arrived as a `[steer]` message at the next turn boundary; continuation
  turns reused the previous prompt + output exactly (88–99.7 % cached, 115–220 ms TTFT); no failed requests.

## Not yet verified
- The Anthropic provider was tested against a mock of the Messages API only (the adaptive-thinking request shape
  and the fallback model ids are best guesses; both are configurable).

## Backend (nInfer) — resolved 2026-09-24
- `response.failed: capture owner has no planning ID` was an nInfer bug (live-session commit 2fb27c35); fixed in
  `C:\AI\src\ninfer-windows`, which now also retains stateless (`store:false`) agent chains as lineage heads.
  Report: `C:\AI\src\ninfer-windows\.local\stateless-agents-20260924\report.md`. The Windows smoke test above ran
  on the fixed build without backend failures.

## Known limitations / ideas
- The system prompt's Environment section shows the time to the minute (`- Date: … HH:mm`, about 100 tokens in) and is
  rebuilt every turn, so the first call after a minute boundary re-prefills the whole conversation (nInfer and Anthropic
  caching alike). Seen in the Windows smoke test: an 8.1k-token turn with 0 cached tokens and ~1 s TTFT, the only miss.
- The lane rows of the Work tab label every top-level agent `main`; the session title would tell them apart.
- `ToolContext.ResolvePath` maps Git Bash's `/c/…` but not its mounts (`/tmp` = `%TEMP%`), so a `/tmp/…` path copied from
  bash output does not resolve in the file tools on Windows.
- Steering an orchestrator that is waiting on its workers makes it stop waiting, but it still needs a lane back;
  if its own workers hold every lane of the pool, the reply waits for one of them to finish.
- Reloading the lanes plugin mid-run can briefly let a pool run more requests than its capacity.
- Models with ≤ 8k context are impractical (system prompt + tool schemas ≈ 4k tokens).
- `sessions.messages` has no `afterSeq`: after paging far back, "jump to latest" reloads the newest page.
- Old NetPI (c:\ai\projects\netpi) data was moved to `%USERPROFILE%\.netpi\legacy-netpi-v1\`.
