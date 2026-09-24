# Status (2026-09-24)

## Verified on Windows
- The desktop shell builds and runs; Git Bash, pwsh, winsqlite3 and window placement work.
- Unit suites (Debug, built into a scratch app folder while NetPI ran): Providers 41 (330 checks), Tools 54, Agent 82,
  Aux 97, Host 38, all passing.
- E2E suite: 55 tests / 703 checks passing, including the Playwright UI smoke (run on Edge); the UI mock e2e
  (`npm run e2e`) 173/173.
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
- SSH tools, live on `nuc` and `server`: a script full of quoting traps came back byte for byte; write, read and edit
  (a path with a space, relative to `cwd`, and a 5 MB file); a 70 KB binary copied up and back unchanged; a timeout
  and an abort both ended the remote process group, background children included.
- The chat no longer jumps while the agent works: in a mock run of 8 quick steps, with every frame recorded, the
  chat moved down 11 times (520 px) before the fix and not once after, with steps expanded or folded.
- Goals, with the real runner and a scripted model (Agent suite): the loop until `goal_update` complete, and the
  pauses on stop, failure (also before the first model call), no progress, `goal.maxContinuations` and the token
  budget; notices after edits, resumes and compaction. The UI mock covers `/goal`, the strip, pause and resume.
- Lanes, the budget and the settings dialog, with the real runner and a scripted model (Agent, Host and E2E suites)
  and the UI mock: every model call recorded with its cost, the monthly, daily and per-lane stops, "ask" and going
  over; `agent_spawn { lane }` from a local agent onto a cloud lane; settings saved and reset from the dialog; tools
  switched per chat (before the first message, mid-chat with a notice, inherited by subagents).
- Profiles, with the real runner and a scripted model and in the UI mock: a new chat gets its project's default; the
  profile's text opens the prompt and its tools are off; a switch after the first message renders the prompt again
  with a notice and no stray tools notice; subagents get their owner's tools or exactly the ones it names.
- Numbers and details: `docs/archive/2026-09-24-windows-bringup.md`, `docs/archive/2026-09-24-agent-tools.md`,
  `docs/archive/2026-09-24-ssh-tools.md`, `docs/archive/2026-09-24-goals.md`,
  `docs/archive/2026-09-24-lanes-budget-settings.md`, `docs/archive/2026-09-24-profiles.md`.

## Not yet verified
- Lanes, the budget, the settings dialog, per-chat tools and profiles in the real app: the host and the contracts changed, so
  they need NetPI closed, `.\build.ps1` and a restart. Then: costs from a paid OpenRouter model (`usage.cost`), and
  whether `qwen3.8-27b` picks lanes by their notes and avoids the paid one.
- Goals with a real model (only the scripted model so far): whether `qwen3.8-27b` follows the continuation
  notices and calls `goal_update` at the right time.
- The desktop zoom setting (`desktop.zoom`, zoom kept in `window.json`): built, but needs NetPI closed for
  `.\build.ps1` and a restart.
- The Anthropic provider was tested against a mock of the Messages API only (the adaptive-thinking request shape
  and the fallback model ids are best guesses; both are configurable).
- Linux/macOS: the suites last ran in the Linux sandbox, before the Windows work, and have not been re-run since.

## Known limitations / ideas
- OpenRouter: a 429 reports its `Retry-After`, but the retry plugin keeps its own backoff; the catalog offers every tool-capable model (~390; narrow it with
  `providers.openrouter.include`).
- Per-turn cache reuse and TTFT are not shown in the UI (usage is in each assistant message; TTFT is not recorded).
- Steering an orchestrator that is waiting on its workers makes it stop waiting, but it still needs a lane back;
  if its own workers hold every lane of the pool, the reply waits for one of them to finish.
- Reloading the lanes plugin mid-run can briefly let a pool run more requests than its capacity.
- Small context windows are tight: with every plugin on, the system prompt and the 36 tool schemas take about 7k tokens.
  Switch tools off per chat to make room; compaction keeps fewer recent messages when that overhead is large.
- Changing the tool set mid-session (a tool plugin enabled, disabled or reloaded with new tools; `tools.disabled`; the
  chat's own tool switches)
  changes the tool definitions, so the backend re-prefills once. A "tools" notice tells the model what changed and
  carries the new tools' guidelines.
- `screenshot` without a url needs the desktop app's `desktop.capture` (a desktop shell built after 2026-09-24); in the
  headless server it asks for a url.
- Not built from the lanes plan: a dollar budget for goals (`goal.budgetUsd`) and a "test" button per provider.
- `sessions.messages` has no `afterSeq`: after paging far back, "jump to latest" reloads the newest page.
- Projects live in the host (the store, the `projects.*` RPC and the Projects panel); only what the model is told about
  them comes from plugins. Moving projects entirely into a plugin was discussed on 2026-09-24 but not decided.
