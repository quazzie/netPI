# Restructure the end-to-end tests

Status: **done** (2026-10-15). The C# server tests are as they were. The browser suite went from 30 scripts to eight
journeys and the RPC contract check; the UI's pure logic is unit-tested with `node --test`; `-Changed` runs the journeys
for UI files only. How to run and write them: [TESTING.md](../TESTING.md) ("UI journeys", "Unit suites").

## Why (measured 2026-10-14)

- `tests/NetPI.E2E/*Tests.cs`: about 87 C# tests against the real server and MockLlm. Stable: one has ever flaked, 3 of
  40 runs. Kept as they are.
- `tests/NetPI.E2E/ui/*.mjs`: 30 scripts, 634 checks, 5,640 lines. 922 CSS-class locators against 27 role or test-id
  ones; 184 `innerText` reads and 66 wording regexes; 318 waits whose timeout was swallowed (`.catch(() => {})`) and then
  a one-shot `check()` that read the page once; 130 fixed `waitForTimeout` sleeps; 35 pixel and layout checks.
- The "eight failures" fixed earlier that day (1c8d685) were not flakes: seven tests checked UI or request shapes that
  had changed on purpose. Four more (`ui.smoke`, `ui.settings`, `ui.chat-run`, `ui.connection`) failed with the machine
  at about 71% CPU and passed alone.
- What the UI tests caught over their life: 7 product bugs, when they moved from the mock UI to the real server
  ([2026-10-11-retire-mock-ui-suite.md](2026-10-11-retire-mock-ui-suite.md)), mostly server-side contract bugs, which
  `ui.rpc-contract` and the C# tests are the place for.

The owner chose to restructure rather than patch: keep the C# tests, a few UI journeys a person would notice broken,
unit tests for pure UI logic, and UI tests only when UI files change.

## What was done

| commit | what |
|---|---|
| c32c8a3 | `web/test/*.test.js` under `node --test` (no npm package): the Work tab's slot placement and priority levels, the Schedules, Webhooks, Ideas and MCP models (a schedule's skip reasons read from `ScheduleRunner.cs`), web/src/lib's formatters, diff, turn numbers, live split, stream order and RPC deadline. The last three were CI-only check scripts, converted and removed. `scripts/test.ps1` runs them as a seventh suite, `UI` (first, no build); `npm test` runs the same. 74 tests, 0.3 s |
| 6352012 | `e2e.ps1` rebuilt the bundles only for `web/src` changes: a plugin tab's change never triggered `npm run build` (its inputs were absolute paths joined onto the repo root) |
| 7a44376 | the components carry `data-testid` / `data-state` / `data-*` hooks for the journeys; a settings field out of range says so with `aria-invalid`. Nothing changes on screen |
| eb46d45 | `expectEventually`, `PAGE`/`MODEL`/`PROBE`, `SLOW` and the seven journeys |
| 42391cc | `-Changed`: the most specific `areas.json` rule decides; UI files go to the journeys, server and plugin files to the C# tests |
| 8eef6b7 | the profile picker keeps its list when a reload fails (below), with its regression check in the settings journey |
| 5e1a947 | the eighth journey, `ui.new-chat` (the owner's "yes + 8") |
| (this commit) | the other 29 UI scripts and `host.mjs` removed with their registrations and ledger rows, `settings.next.mjs` renamed `settings.mjs` (`ui.settings`), docs |

### The journeys

| id | what a person does | about |
|---|---|---|
| `ui.chat` | opens a chat and has the agent edit a file: the answer streams, the tool rows finish, the edit's diff, the answer; every side-panel tab opens | 9 s |
| `ui.chat-control` | steers with Enter and queues with Alt+Enter while it runs (chips held at gates), stops an answer with Esc | 11 s |
| `ui.tools` | watches thinking live and opens it, a command's output while it runs, a dropped stream retried without its discarded text | 7 s |
| `ui.reconnect` | loses the connection: a run that ended meanwhile, an answer that goes on with no gap, a new run after it | 27 s |
| `ui.work` | sees an agent's two instances busy, a third chat queued, cancels it from the tab; rows stay put as runs end | 4 s |
| `ui.settings` | changes a number (saved, reset, refused out of range), adds an agent from the model search, edits and removes it; keeps a chat's profile picker open while the settings change | 5 s |
| `ui.asks` | answers an `ask_user` card after a reload, by option, in their own words and with Send | 6 s |
| `ui.new-chat` | starts a chat from the start screen in a project, sees it listed after the first answer, starts the next one there | 4 s |

Plus `ui.rpc-contract` (no browser, 0.2 s). The nine take about 40 s in 4 shards.

## What was removed, and where its behaviour is still covered

"Server side" means the RPC or the engine behind the UI is tested, not what the page does with it. Every journey fails on
a console or page error, and `ui.chat` opens every side-panel tab, so "the tab loads" is covered for all of them.

| script (id) | what it covered | still covered by | nowhere |
|---|---|---|---|
| `smoke.mjs` (`ui.smoke`) | streaming, tool rows, diff; project picker; a pasted ~4 MB image shrunk; plugin tabs; Ideas overview; Files tab; Work tab full with a queued subagent; steer, Esc, retry; backup create and verify | `ui.chat`, `ui.chat-control`, `ui.tools`, `ui.work`; server side: E2E `slots.subagents`, Host `BackupTests`, `ServerTests` (websocket message limit) | image shrinking, Ideas overview, the project picker, the backup UI |
| `chat-run.mjs` (`ui.chat-run`) | status line, compaction refused while busy, retry banner and `stream.reset`, live shell output, chips, pinned to the bottom, folded steps with diffs and turn numbers, the showcase chat (prompt row, notices as sent, multi-file edit, a command's changed files), `/compact`, images, the compaction banner | `ui.chat`, `ui.tools`, `ui.chat-control`; server side: E2E `retry.drop`, `tools.bash`, `control.abort-stream`, `compaction.manual`, `provider.images`, Aux `CompactionTests`, Agent `ContextTests`, Tools `FileTests`, `ShellTests`; `web/test` (`turnStats`, `parseDiff`, stream order) | the showcase rows, scroll stability, the compaction banner, an image in the chat |
| `connection.mjs` (`ui.connection`) | a plugin tab remounting on a new bundle, its error state and Retry; the socket cases; the composer and Diagnostics with the runtime, context and compaction plugins off; desktop notifications | `ui.reconnect` (the socket cases); server side: Agent `StreamReplayTests`, Host `PluginTests` (UI version), E2E `plugins.toggle-repair` | remount and Retry, the UI with those plugins off, notifications |
| `chat-decisions.mjs` (`ui.chat-decisions`) | ask cards, a guardrail asking before a push, fork, `/goal`, schedule proposal cards, plan mode | `ui.asks`; server side: E2E `ask.user`, `plan.flow`, `chat.fork`, Agent `AskTests`, `GuardrailsTests`, `GoalTests`, `PlanTests`, Aux `SchedulesToolTests` | the guardrail, fork, goal, plan and proposal UI |
| `tool-cards.mjs` (`ui.tool-cards`) | thinking opened live, the plan strip, web search/fetch/screenshot cards, `show_image`, no tools notice for deferred tools, file links that ask first, links/images/HTML kept safe, ssh cards, the Claude CLI's own tool rows | `ui.tools` (thinking); server side: Aux `TodoTests`, `MediaTests`, `WebTests`, `SshTests`, Agent `DeferredToolsTests`, `LoopTests`, E2E `mcp.deferred`, Tools `OpenTests`, Providers `ClaudeCliTests`, Host `ServerTests` (CSP) | card rendering, the link confirm, markdown sanitising in the page |
| `work-tab.mjs` (`ui.work-tab`) | instance rows with commands and subagents, waiting on subagents, elapsed time, a not-loaded agent and its switch, counts, queued list, cancel, any-agent queue, freed rows, background output and kill, the agent dialog | `ui.work`, `web/test` (`placeSlots`); server side: E2E `agents.setup`, `slots.three-chats`, `slots.abort-queued`, `slots.subagents`, `tools.bash`, Agent `SchedulerTests`, Aux `PanelTests`, Tools `ShellTests` | the rest of the tab |
| `settings.mjs` (`ui.settings`, old) | defaults, reset, range; agents (price, note, off, rename); budget; providers; built-in texts; tools and plugins; per-chat tools; profiles; project skills | the new `ui.settings`; server side: Host `SettingsTests`, E2E `agents.setup`, `profiles.defaults`, `skills.catalog`, Agent `SessionToolsTests`, `ProfilesTests`, `SkillsTests`, `SchedulerTests` | price, note, rename, providers, built-in texts, the tools page, profile dialogs |
| `remove-agent.mjs` (`ui.remove-agent`) | a confirmed removal takes the agent out of the list and the settings | the new `ui.settings`; Agent `SchedulerTests` | – |
| `composer.mjs` (`ui.composer`) | agent picker, commands and skills popup, @ mention, pickers open while a reply streams, middle-click, tab dots and a failed call's request id, narrow widths, drag to reorder tabs, drafts | server side: E2E `agents.setup`, `skills.catalog`, `startup.plugins`, `errors.rejected`, Tools `FileTests`, Aux `InspectTests` | all of the UI |
| `chat-history.mjs` (`ui.chat-history`) | paging back with a still anchor, the 200-message cap, paging forward, the context ring, Compact now, a page racing a new message, deleting a chat or a subagent | server side: Host `MessagePageTests`, `ServerTests`, `SessionStoreTests`, E2E `tools.files`, `chat.stream`, `compaction.auto`, `compaction.manual`, `sessions.delete-during-run` | the window cap and anchor, the ring, the race, deleting one subagent |
| `projects.mjs` (`ui.projects`) | the composer's project picker and notices, new chats without a project, the start screen's last project, the projects dialog, the folder picker | server side: E2E `context.project-switch`, `sessions.title-usage-projects`, Host `SessionStoreTests` | the UI |
| `start-project.mjs` (`ui.start-project`) | the start screen's split button | `ui.new-chat` (both halves) | – |
| `sessions-panel.mjs` (`ui.sessions-panel`) | resize and fold, project favorites, 230 px, archived paging, groups' stored state and search | `web/test` (`recencyBucket`); server side: Host `SessionStoreTests` (archive window) | the rest |
| `panel-tabs.mjs` (`ui.panel-tabs`) | drag a tab between panels, the layout stored and restored | server side: Host `ServerTests` (`ui.state`) | drag and restore |
| `pin.mjs` (`ui.pinned-sessions`) | a Pinned group above the recency groups | server side: Host `SessionStoreTests` | the group |
| `close-empty-session.mjs` (`ui.close-empty-session`) | closing an empty chat removes its row | server side: Host `SessionStoreTests` (transient, evicted) | the row |
| `ideas-tab.mjs` (`ui.ideas-tab`) | cards, status, new idea with a pasted image, sections, send to chat, reorder, delete, Ctrl+I and `/idea`, the target, a conflicting save, refine, tags, `ideas.changed`, the start screen's ideas | `web/test` (tags, statuses, word search); server side: Aux `IdeasTests`, `IdeasStorageTests`, E2E `ideas.tool` | the dialog, keyboard, paste, the start screen |
| `idea-conflict.mjs` (`ui.idea-conflict`) | the dialog opens on the current revision, a status change claims none, the play button | `web/test` (groups); server side: Aux `IdeasStorageTests` (conflict) | the tab passing the revision, the play button |
| `idea-meaning.mjs` (`ui.idea-meaning`) | the ≈ toggle and `ideas.similar` | `web/test` (word search); server side: Aux `EmbeddingsTests` | the toggle |
| `idea-image.mjs` (`ui.idea-image`) | a failed image attach toasts and the button comes back | server side: Aux `IdeasTests` (images) | the toast and button |
| `files-tab.mjs` (`ui.files-tab`) | select vs open, @ mention, the git line, the context menu, search, no `files.scope` per message, a workspace switch dropping a late answer | server side: Tools `OpenTests`, `FileTests`, `WorkspaceToolTests`, E2E `context.project-switch` | all of the UI, the RPC frugality |
| `files-mount.mjs` (`ui.files-mount`) | unmounting the Files tab removes its focus listener | – | all |
| `mcp-tab.mjs` (`ui.mcp-tab`) | server rows, a failed server, a rejected tool, details and switches, read-only, filter, a switch saving the config, the add/edit dialog | `web/test` (`mcp-config`); server side: E2E `mcp.deferred`, Aux `McpTests` | the UI |
| `mcp-smoke.mjs` (`ui.mcp-panel`) | the MCP tab at 230/380/1000 px, its action menu, a failed save keeping the draft | `web/test` (`mcp-config`) | the rest |
| `diagnostics-tab.mjs` (`ui.diagnostics-tab`) | a failed plugin first, reload, events, logs, context, tool changes, the Calls view polling single-flight | server side: Aux `PanelTests`, `InspectTests`, Host `PluginTests`, Agent `ContextTests`, E2E `inspect.diag` | the polling and the UI |
| `usage-budget.mjs` (`ui.usage-budget`) | the Usage tab, a chat's cost with subagents, the Work tab's budget, the pill, going over a stopped chat's budget | `web/test` (`usd`); server side: Agent `BudgetTests`, E2E `errors.budget-stopped` | the UI |
| `schedules-tab.mjs` (`ui.schedules-tab`) | row states, pause, the finished-once guard, delete confirm, the update payload, reload, 230 px | `web/test` (`schedules-model`, the skip reasons against `ScheduleRunner.cs`); server side: Aux `SchedulesTests`, `RpcReadOnlyTests` | delete confirm, payload, reload, width |
| `webhooks-tab.mjs` (`ui.webhooks-tab`) | the receiver banner, row states, the add form's payload, the token box and curl line, Preview/Send, pause, delete, re-key, reload, 230 px | `web/test` (`webhooks-model`); server side: Aux `WebhooksTests`, `WebhookHooksTests`, `RpcReadOnlyTests` | the banner, dialog, confirm, reload, width |
| `dev-proxy.mjs` (`ui.dev-proxy`) | the vite dev server proxies socket, API and bundles; `?token=` signs in | server side: Host `ServerTests` (the token login) | the vite proxy |

## Load: what the runs showed

- The machine was not idle when nothing of ours ran: eight orphaned `python -` processes (agent shell scripts started on
  13 and 14 October, their parents gone) each spun one core, about 64% of 16 cores. That was the "machine load" behind the
  four failures of 2026-10-14. The owner had them stopped on 2026-10-15.
- On top of that, 12 CPU burners (the TESTING.md recipe) starve the E2E runner process itself: the MockLlm instances it
  hosts for all shards answered `/v1/models` in over 5 s and one echo reply took 127 s, so C# and UI tests alike wait on
  the mock. That is past what a test can be robust to; the journeys are held to the task's own bar instead.
- With a solution build looping beside them (and those python processes still running), the journeys passed 24 of 24
  (three rounds, 4 shards) once two things were fixed: `ui.tools` checked a tool row in the moment between its end and the
  run folding its steps (it now checks the row after the run, unfolded), and a read inside a probe could hold one round for
  Playwright's 30 s (`PROBE`). Those development runs are not in the flake ledger: it was reset to the committed one less
  the removed tests' rows, so the journeys' history starts with the gate runs below.

## The gate (2026-10-15)

Three full `.\scripts\e2e.ps1` runs in a row after the deletion, 79 tests each (70 C#, 8 journeys, the contract check),
4 shards: 79/79 in 51 s, 79/79 in 48 s, and 79/79 in 69 s with a no-incremental solution build looping in another worktree
the whole time (seven builds finished during the run, CPU at 94% twenty seconds in). The unit gate (`scripts\test.ps1`, the
six .NET suites and `UI`) ran after them: all green, `UI` 74 of 74. After merging master (four commits: the top bar's
waiting menu, Webhooks without a collection window, tool timers) a fourth full run passed 79 of 79; the Webhooks node test
follows master's `waiting` state, and the fixture test master had updated stays deleted.

## From the stopped load-flake session (findings in the session notes, read here)

- `ui.smoke`'s "1 queued" existed only between the orchestrator's yield and the first worker's end: replaced by
  `ui.work`, whose states wait at gates.
- `ui.connection`'s `ERR_INTERNET_DISCONNECTED` console errors were the test's own (its drop aborts the page's HTTP
  fallback): `tapSocket` counts those and `run()` does not report them.
- The profile picker set its list to `[]` on any failed `profiles.list` (ProfilePicker.svelte), which hid the button and
  unmounted an open picker until the next good load: the likely cause of `ui.settings`' detaching options. Fixed in 8eef6b7
  (a failed load keeps the list; the picker goes with the plugin's methods; only the newest of overlapping loads counts),
  with a check in `ui.settings` that fails one `profiles.list` on purpose; it failed before the fix and passes after.
- The ~7 s between a tool batch and the next model request, measured afterwards (stage marks on the turn path, four
  servers at 93% CPU, 60 turns): NetPI's own part is 0-67 ms from a tool batch's end to the provider's POST, and the
  parent's turn after `agent_spawn` starts at once. The seconds were MockLlm stalling inside the loaded runner (one first
  event took 5.7 s after a POST sent in 1 ms) and slot waits by design. One real defect turned up on the way: a read of
  a stale model catalog with no refresh running started one and waited for every provider's listing (up to 5 s each),
  and a turn's model lookup is such a read. Fixed: such a read answers from the cache and refreshes in the background.

## Left for the owner

- CI: `.github/workflows/ci.yml` last ran on 6 October and failed; master is about 250 commits ahead of `origin` and
  nothing was pushed. The workflow now runs `npm test` in place of the three converted checks. Revive it or delete it?
