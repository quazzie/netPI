# Move the mock UI suite to the real server

Status: planned 2026-10-11, not started. The suite is **frozen**: no new section or check goes into `web/mock`. A new UI
test is a script in `tests/NetPI.E2E/ui` that runs against the real server and MockLlm, registered in
`tests/NetPI.E2E/UiTests.cs`.

## Why

`web/mock` is two things. One is a Playwright walkthrough: `e2e.mjs`, 49 sections, about 430 checks. The other is a
JavaScript copy of the server it runs against: `server.mjs`, `agent.mjs`, `store.mjs`, `work.mjs`, `ideas.mjs`,
`diag.mjs`, `content.mjs` and `settingsSchema.mjs`, about 3,900 lines. Every protocol change has to be copied into that
code by hand. Eight commits in the week before this plan touched it. A check that passes against the copy proves the UI
against the copy, not against NetPI. `tests/NetPI.E2E/ui` already drives the real server with 13 scripts.

The cost of moving is speed: the mock suite needs no .NET build and runs in about 2.5 minutes. A second cost is that CI
runs the real-server suite with `-NoUi`, so the move has to bring the browser tests into CI (step 4).

## Done when

- `web/mock` is gone. That covers the server copy, `e2e.mjs`, `sections.mjs`, `pw.mjs`, `fake-openai.mjs`,
  `sample-plugin` and `screenshots/`.
- These are gone too: the `mock`, `e2e` and `e2e:sections` scripts in `package.json`, the `ws` devDependency (only the
  mock server uses it), the CI job "web UI end-to-end (mock server)", and the mock passages of docs/UI.md (from line 18,
  and the timings at about line 364), docs/TESTING.md (around line 435) and the HANDOFF.
- The CI job "end-to-end (real server, mock model)" runs the browser tests, so `-NoUi` is gone. The Windows runners have
  Edge, which `launchBrowser` falls back to, so no browser download is needed. The job needs `npm ci` for
  `playwright-core`. The node checks of the mock job (`check-host-shims`, `core-size`, `check-tab-lifecycle`, …) and the
  bundle check move to that job or to the unit job.
- Every check of the walkthrough is either in a real-server script or named in a port commit with the reason it was
  dropped.

## Rules for every port

1. **One commit per port unit (table below).** A commit contains:
   - the new or extended script
   - its wrapper in `UiTests.cs` and its tag in `Catalog.cs` (`needs-node`, plus `ideas` when it needs the Ideas plugin)
   - the sections deleted from `e2e.mjs`
   - the fake-backend code only those sections used (`agent.mjs` scripts, `server.mjs` controls, `store.mjs` seeds)
   - the commit message: checks ported, checks dropped and why, and the run time.
2. **Check parity.** Keep the mock's check names where they still read true, so `git log -S` finds both sides. A check
   that the real server makes meaningless (a mock counter, a fake delay) is restated as what the user sees, or dropped
   with the reason.
3. **Reuse the shared server, but leave it as you found it.** Shards run their tests one after another on one server.
   A script that changes server-wide state (settings, agents, budget, `plugins.setEnabled`, tool switches) restores it in
   `finally`. The wrapper then compares `settings.get` / `plugins.list` with the values from before the script. Names
   are unique per run (`e2e-<area>-<stamp>`), as `ui.start-project` already does.
4. **No new packages.** `playwright-core` is already a devDependency. `routeWebSocket` and `page.clock` (both in 1.63)
   replace the mock's controls.
5. **Verify each unit:**
   - `.\scripts\e2e.ps1 -Only ui.<id> -Repeat 5 -Fresh` is green on a loaded machine. A flake gets fixed, not retried.
   - `npm run e2e` passes over the sections that are left. That is the deletion's check: a section a later one builds on
     is listed in `NEEDS`.
   - `npm run build` leaves no bundle diff. Run it only when the unit touched UI sources.

## Step 1: the harness (before any port, in this order)

**H1. `tests/NetPI.E2E/ui/lib.mjs` and one wrapper.** Every script copies about 60 lines today: argument parsing,
`check`, `shot`, `loadPlaywright`, `launchBrowser` with its msedge/chrome fallback, the error listeners and the final
`{ ok, checks, errors }` line. Move all of it into `lib.mjs`:

- `const ui = await start({ args: ['project', …] })` returns `{ page, context, browser, args, check, shot, rpc, finish }`
- `rpc(method, params)` is `POST {url}/api/rpc/<method>` with the token header, as `rpc-contract.mjs` does
- `openApp(page)` is goto `?token=` and wait for `.panel.left`
- `openSession(page, title)` and `waitIdle(sessionId)` (poll `agent.status`)

In `UiTests.cs`, one `RunUiScript(env, "name.mjs", timeout, params (string, string)[] args)` replaces the 25 copied
lines per wrapper. It always passes `--url`, `--token`, `--mock` (`env.MockUrl`), `--app` (`env.AppDir`) and `--out`,
and it prints the browser errors on a non-zero exit (as `ui.idea-image` does now). The 13 existing scripts and wrappers
move to it in the same commit. That is mechanical, and smaller.

**H2. `wsTap(page)` in `lib.mjs`, built on `page.routeWebSocket(/\/ws/)`.** Install it before `openApp`. It
connects every page socket through to the server and:

- records frames both ways (`sent('sessions.list')`, `received('session.message')`, `since(mark)`)
- holds an answer: `hold(frame => frame.id === …)` returns a release function
- `drop()` closes the live socket and refuses new ones until `restore()`. This is the reconnect test without restarting
  the server, and stronger than `context.setOffline`, which does not close a socket that is already open.

This replaces the mock counters (`listCalls`, `msgLoads`, `diagCallsStats`, `filesCalls`, `filesOpenCalls`) and the
held answers (`messagesHold`, `diagCallsDelay`, `filesDelay`, `procTailDelay`). The UI's socket URL comes from
`wsUrl()` in `web/src/lib/rpc.svelte.js`. Match it, not a guess.

**H3. MockLlm gates.** The tag `[s:<any> gate=<name>]` holds the stream after its first chunk until
`POST /_release?gate=<name>`. The `hold` code in `MockServer.TickAsync` is where it goes. A gate that is never released
gives up after 60 s, the same cap as `hold`.

- `GET /_gates` lists the waiting gates, so a script can wait for "the call is in flight".
- `/_reset` releases every gate.
- `sumgate=<name>` on a user message gates the summarizer call that compacts it (`Summary` reads the tags).

This replaces `thinkDelay`, `queueInternal`, `agentStatus`, `workRelease`, `workTake` and `workSetUnassigned`: a run
held open, then released when the check wants it. Document it in docs/TESTING.md ("Mock model server": the endpoint
table and the `hold=` sentence). MockLlm has no tests of its own, so the first port that uses it is its test.

**H4. MockLlm scenarios the ports need.** Add each one with the port that first uses it, not up front:

| tag | what it does | used by |
|---|---|---|
| `[s:ask n=2]` | a second, multiple-choice question (the mock's `[ask2]`) | H |
| `[s:plan enter=1]` | step 0 calls `plan_enter` with a reason (the mock's `[planenter]`) | H |
| `[s:guard]` | `bash git status --short`, then `bash git push origin main`. A guardrails rule `ask: ^git push` in settings makes the second one wait | H |
| `[s:goal]` | `goal_update` after a step, for the goal notices | H |
| `[s:multiedit]` | one `edit` over two files, then a `bash` that changes a file | B |
| `[s:web]` | `todo_write` (three items, done one by one), `web_search`, `web_fetch`, `show_image`, `browser screenshot {url}`, and file links in the answer | I |
| `[s:fast]` | short steps (read, grep, bash, edit) with a gap between model calls | B |
| `[s:bgproc]` | `bash` with `background: true`, a loop that prints a line every 200 ms until it is killed | E |

Check `Scenarios.cs` first: `ask`, `plan`, `tools`, `parallel`, `bash`, `slow`, `slowtools`, `drop`, `error`, `fail`,
`spawn`, `spawnbg`, `long`, `ideas` and `codemode` already exist. Extend one where it nearly fits.

For `[s:web]`, MockLlm also serves:

- `GET /search?format=json` with SearXNG's shape. The script sets `web.search.searxngUrl` to `{mock}` and restores it.
- `GET /page`, a small HTML page with a title, for `web_fetch` and the screenshot.

## Step 2: the ports

Do them in this order: least new harness first. Check counts are the mock's. "Covered" means an existing real-server
script already holds that check. Confirm each one, then delete it without porting.

| # | unit → script | sections (checks) | setup on the real server | mock control → replacement |
|---|---|---|---|---|
| J | `ui/projects.mjs` | project picker + new session project (8), start screen (10), projects dialog (15), projects: folder picker (6) | `projects.create` ×2 with unique names; one with `AGENTS.md` and a skill file, for "lists the instruction files" | none. Start screen: `ui.start-project` covers the split button (17 checks). Port only the "last project remembered", "recreates a session" and "no tab open" checks |
| K | `ui/settings.mjs` | settings: controls, agents, budget, tools (26); chat tools per session (6); profiles (13) | the agents are MockLlm's models. Add one from the model search; give it a price through the dialog; delete it after | none. Restore settings in `finally` (rule 3). `ui.remove-agent` covers deleting an agent |
| D | `ui/sessions-panel.mjs` | panels + plugin tabs (5), narrow side panels (1), archived section (5), collapsible groups (10) | Archived: one chat with a message, then `sessions.fork` ×261 (a fork with messages is stored at once, with no model call). Archive the oldest 60 with `sessions.update { archived: true }` and delete all of them after | `seedMany`/`clearMany` → forks. `listCalls` → `wsTap.sent('sessions.list')`. The "Earlier" group: `page.clock.install({ time: now + 10 days })` makes every chat Earlier, so "older groups are closed by default" is checkable without a backdated chat. Panels + plugin tabs use real plugin tabs (Files, Work) instead of `sample-plugin` |
| F | `ui/ideas-tab.mjs` | plugin tab: Ideas (39), welcome screen: ideas to work on (8) | `SeedIdeas` in `UiTests.cs` (already there) with a project; ideas in three statuses for the welcome offer | `ideaRefines` → the refine chat found with `sessions.list { attachedKey }`. `capabilities` (welcome without `ideas.picks`) → `plugins.setEnabled netpi.ideas false`, then back on in `finally`. Covered: image (`ui.idea-image`), "Load the current version" (`ui.idea-conflict`), meaning toggle (`ui.idea-meaning`) |
| G | `ui/diagnostics-tab.mjs`, `ui/files-tab.mjs`, MCP into `mcp-smoke.mjs` | Diagnostics (12), Files (16), MCP (7) | Diagnostics: a plugin that fails to load is a broken copy written into `{app}/plugins/e2e-broken`, then `plugins.rescan`; it is removed after. MCP: the fixture server `McpTests` registers. Files: a git project with a change, a deletion and an untracked file | `diagCallsStats`/`filesCalls` → `wsTap`. `diagCallsDelay`/`filesDelay` → `wsTap.hold`. `filesOpened`/`filesOpenCalls` → `wsTap.sent('files.open')` with `user: true`, answered by a hold that is never released, so nothing opens on the desktop. Covered: `ui.files-mount` for the mount |
| C | `ui/composer.mjs` | composer popups (10), popups survive a chat update (13), composer narrow widths (1), composer drafts (5), drag to reorder tabs (1), tabs (1), tab dots (5) | two agents (qwen, tiny-ctx); a project with a skill and files for `/` and `@` | `thinkDelay` → `[s:slow]` or a gate. Tab dots: a gated run in a background tab, released; `[s:fail]` for the error dot. `msgLoads` → `wsTap.sent('sessions.messages')`. "Store evicted": open tabs past the cache size |
| A | `ui/chat-history.mjs` | long session (15), a snapshot answer must not undo what it missed (2), deleting a chat leaves nothing behind (2) | Long: `[s:long n=110 lines=1]` (about 220 messages; measure it, and if it takes over 30 s add `tool=ls` to `Long`). Then `compaction.run` twice for the compaction counts. Delete: `[s:spawn]` gives a parent with two subagents | `messagesHold` → `wsTap.hold` on the `sessions.messages` answer while `agent.send` over HTTP publishes a message. `msgLoads` → `wsTap` |
| H | `ui/chat-decisions.mjs` | ask_user (5), plan mode (18), guardrails (5), fork (3), goals (6) | `[s:ask n=2]`, `[s:plan]`, `[s:plan enter=1]`, `[s:guard]` with the rule set and removed in `finally`, `[s:goal]`, `/goal` | none |
| B | `ui/chat-run.mjs` | new session + agent run (17), showcase session (11), abort (1), images (1), fast steps (6), compaction banner (3), settings: `/compact` (1) | Retry banner: `[s:error]`. `stream.reset`: `[s:drop]`. Tool tail: `[s:slowtools]`. Diff: `[s:tools]`. Showcase: `[s:multiedit]` and a report from `[s:spawn]`. Banner: the tiny-ctx model with `[s:long … sumgate=c]` | `queueInternal` → the result of a background subagent (`[s:spawnbg]`) is an internal input and must not show as a chip. Queue chips → a gated run plus two sends. Covered by `ui.smoke`: retry, `stream.reset`, Esc abort, pasted image. Port only what differs |
| E | `ui/work-tab.mjs` (extended), `ui/usage-budget.mjs` | plugin tab: Work (30); plugin tab: Usage (9); budget (13) | Work: qwen has 2 slots. Two gated runs fill it, a third waits; `[s:spawn]` gives a chat that waits for its subagents; gemma-4 is "not loaded"; `[s:bgproc]` gives the process tail. Budget: prices on the agent and a monthly/daily limit, all restored in `finally` | `agentStatus`/`workRelease`/`workTake`/`workSetUnassigned` → gates released one by one; "waiting for any agent" → a run sent with no agent while both slots are held. `procStats`/`procTailDelay` → the real process and `wsTap.hold`. Covered: `ui.work-tab` (8 checks) and the smoke's full-agent check |
| I | `ui/tool-cards.mjs` | web tools, todo plan, tools notice, file links (24); ssh tools (6); provider tools: results live (4) | `[s:web]` against MockLlm's `/search` and `/page`. **SSH (spike):** `ssh.path` → a fake `ssh.cmd` (node) that answers the broker's probes and runs the payload locally; give it an hour, and if the broker's protocol makes it bigger, drop the 6 checks and name `SshTests` in the commit. **Provider tools:** `providers.claudeCli.executable` → a fake `claude.cmd` that prints stream-json with provider-executed `Bash`/`Read` and a pause before the result. `ClaudeCliTests` (line ~379) already builds such a batch file | `thinkDelay` → a gate. `filesOpened` → `wsTap` as in G |
| L | `ui/connection.mjs`, `ui/dev-proxy.mjs` | notifications with reconnect (12 + 4 dev server), capability removal and recovery (5), plugin tab hot reload / error (3) | Notifications: `addInitScript` stubs `Notification` and records calls; a budget stop gives the budget ask. Reconnect: `wsTap.drop()` mid-run (gated), release the gate, `restore()`. Capabilities: `plugins.setEnabled` off/on for the runtime, compaction and context plugins (`finally` turns them back on). Hot reload: overwrite a plugin's UI bundle in `{app}` (as `reload.file-change` does with a dll) for `ui.changed`; a throwing bundle for the error state; restore it for the retry. Dev server: vite with `NETPI_URL={url}` on a free port | `offline`/`online` → `wsTap.drop/restore`. `capabilities` → `plugins.setEnabled` |

When unit L lands, nothing is left in `e2e.mjs`. Delete `web/mock` and everything listed under "Done when" in one
commit. Then make the CI change and confirm one green run on a branch before merging.

## Step 3: delegating

The harness (H1–H3) and units L and I are design work with spikes. Keep those. Units J, K, D, F, G, C, A, H, B and E
are mechanical once H1–H3 exist. Each one can go to one local agent (qwen 27B via `node scripts/netpi.mjs`):

- one unit at a time, in its own worktree and branch
- the brief names the mock line range of its sections, the target script, the `lib.mjs` helpers and this plan's row
- the definition of done is rule 5's three commands
- the diff is reviewed before it merges
- no publishing while an agent works (nothing here needs a publish: tests, MockLlm and docs only)

## Step 4: CI

In `.github/workflows/ci.yml`, the job "end-to-end (real server, mock model)":

- add `npm ci` after "Set up Node"
- change `e2e.ps1 -NoUi` to `e2e.ps1`
- move the node checks of the mock job here, and delete the mock job

Run it on the branch first. A browser test that fails only on the runner is a flake to fix (rule 5), not a reason to
keep `-NoUi`.
