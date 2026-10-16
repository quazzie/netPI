# Whole-repo review and one-pass fixes (2026-10-16)

## How
Two read-only review passes over the whole repository by Haiku subagents (first: kernel, agent loop + providers, tools,
UI; second: the parts the first only skimmed, plus a cross-cutting check for `call_tool`). The parent re-read every finding
it reported; refuted ones were dropped (e.g. "a failed plugin swap leaks its load": `StartInstanceAsync` unloads it).
Fixes ran as workstreams, each in its own worktree and branch (Sonnet: A, B, C, C2, D1, H, I, T; local qwen3.8-27b: D2, E,
F, G), merged one by one into master by the parent. Base 46fc85a8, end ade7406f.

## Workstreams and outcome
- **A — `call_tool`**: deferred tools (most built-ins) are called as `call_tool {name, arguments}` and stored that way;
  only execution unwrapped them. Readers now go through `ToolCalls.Effective` / `effectiveCall`: chat cards (plan_enter,
  plan_submit, ask_user, schedules, show_image), artifact chips, step counts, Todo, Goal, GoalReview, Loops, Compaction
  file lists and transcript, Skills, Coordinator, Diagnostics, ClaudeCli prompt labels. Found because a `plan_enter`
  card never appeared.
- **B — processes**: `shared/ProcessKit` (one `ChildJob` instead of three copies, `ProcessJob`); webhook hooks run in a
  job with a drain grace after exit (a hook's own background children now end with the hook call); MCP stdio servers in
  a job; the Claude CLI is killed when its stream is abandoned; GitRunner returns the real exit code when a helper holds
  the pipe; Shell eviction keeps live trees, timeout timers end with the process, long commands count the environment
  block; Lsp git killed on cancel; WindowsAgent/Desktop fixes.
- **C / C2 — runtime and agents**: ordered agent-record saves; all-or-nothing `agent_spawn` batches; `PromoteQueued`
  keeps input while stopping; a throwing before-call hook blocks the call; scheduler grant continues past a refused pool;
  corrupt waiting batch reported; linear stream replay; paged usage history. Added: `agent wait` end reason
  (`AgentWaitResult`), per-subagent cost, `SpawnRequest.MaxUsd/MaxTurns`.
- **D1 — plan/goal/ask**: plan submit re-checks plan mode under its gate; enter-answer undoes plan mode on failure;
  over-limit plans are errors; the "file" decision goes through the workspace mutation check; the run that sets a goal is
  charged; an empty goal review is "not reviewed", not PASS; malformed `ask_user` questions are an error.
- **D2 — small plugin fixes**: schedules cancel path, webhook awaiting query + task lifetime, idea image delete order,
  diagnostics reload observed, MCP command after stop, extension relay message cap, browser follow failure, memory sweep
  per chat, compaction estimate, restore-backup checks; `IWorkspaceResolver.ResolveById` removed (unused, unchecked).
- **E — files/ssh**: write keeps EOL/BOM of large files, CR line endings, capped listings in name order, ssh edits keep
  per-line endings; grep/find/ls skip nested repositories/worktrees and read `info/exclude`.
- **F — kernel/scripts**: `checkout.ps1` stale-index warning (was a no-op: a `param([string[]]$Args)` swallowed the first
  argument), `UpdateMessage` cache drop, EventBus backlog count and ring lock, ReloadGate registration, SQLite ctor
  handle, unknown role, paged back-fill, fork context size, shadow copies keyed by pid + start time, ContextCache
  generations bounded, query token only for login and websockets, publish rollback, provider migration, PluginManager
  `Load` object.
- **G — providers**: shared transport helpers in ProviderKit, repeated tool names, AiProxy cache-write usage, Responses
  call ids and id-less items, SSE line cap (buffered scan), model-list fingerprint aligned with the host, Claude CLI
  output tokens summed over its turns. The "ThinkTagSplitter dead branch" finding was wrong (kept).
- **H — web UI**: dead-socket detach, `loadNewer`/`loadPlans`/`loadAgents` races, browser view socket, thinking row live
  split, output buffer slack, status map, timers, dialogs, schedule `once` cadence compare, sequence guards, shared
  helpers (`firstLine`, `scrollIntoView`, `BUSY`).
- **I — additions**: `lsp references|rename` + `files.applyEdits`; the `test` tool (`.netpi/tests.json`, results file
  written by `test.ps1`/`e2e.ps1`); `ssh run {background}`, `job`, `grep`, `find`; `ssh.hostNotes`; the global
  `code-review` skill (`~/.agents/skills`, not in the repo); `skills.userFolder` (off in the E2E suite, which otherwise
  saw the developer's own skills).
- **T — test**: `subagents: agent_spawn and agent wait return at the first report…` failed on every run (an instant
  child report reached the parent as a seen notice); the test now holds the quick child until the parent waits.

## Left as is (decided)
- `GitProbe` sync-over-async: the synchronous members are required by `IWorkspaceRepoProbe` callers (resolver,
  provisioning); the hooks and tools use the async ones.
- `HostKernel` startup cleanup waits synchronously: its caller is a synchronous factory.
- A subagent's explicit `tools` list is not reduced by the chat's switched-off tools (documented in agent_spawn).
- The Work plugin keeps its own `firstLine` copy (plugin bundles cannot import host modules; its node test imports it).

## Verification
Full unit gates (UI 82, Tools 131, Providers 114, Agent 312, Storage 121, Host 241, Aux 398; 0 failed) and the full E2E
suite (79 cases, 858 checks, all green) on the final tree before the last merge.
