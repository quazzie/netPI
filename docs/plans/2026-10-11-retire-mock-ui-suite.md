# Retire the mock UI suite

Status: started 2026-10-11. The suite is **frozen**: no new section or check goes into `web/mock`; a new UI test is a
script in `tests/NetPI.E2E/ui` against the real server and MockLlm, registered in `tests/NetPI.E2E/UiTests.cs`.

## Why

`web/mock` is two things: a Playwright walkthrough (`e2e.mjs`, 49 sections, 424 checks) and a JavaScript copy of the
server it runs against (`server.mjs`, `agent.mjs`, `store.mjs`, `work.mjs`, `ideas.mjs`, `diag.mjs`, `content.mjs`,
`settingsSchema.mjs`: about 3,900 lines). The copy has to follow every protocol change by hand — eight commits in the
week before this plan touched it — and a check that passes against it proves the UI against the copy, not against
NetPI. `tests/NetPI.E2E/ui` already drives the real server (13 scripts). The cost of moving is speed: the mock suite runs
without a .NET build, in about 2.5 minutes.

## How

Area by area, each section becomes a real-server script (or joins one), its checks kept one for one or each dropped
check named in the commit with the reason. When a section is ported it is deleted from `e2e.mjs` in the same commit,
with the fake backend code only it used. When the last one is gone: `web/mock` goes, with `npm run mock` / `npm run e2e`
/ `e2e:sections`, the `ws` dev dependency (only the mock server uses it), the CI job "web UI end-to-end (mock server)"
and the mock passages of docs/UI.md and docs/TESTING.md. The sample plugin (`web/mock/sample-plugin`) moves to
`tests/` if a real-server test still needs a plugin tab of its own.

## The sections

Checks per section, and the mock-only controls a section uses (each needs a real-server way to get the same state):

| checks | section | mock controls |
|---:|---|---|
| 15 | long session | |
| 17 | new session + agent run | `queueInternal` |
| 11 | showcase session | |
| 10 | composer popups | |
| 13 | popups survive a chat update | |
| 1 | tabs | |
| 5 | tab dots: a run that ends behind another tab | `thinkDelay` |
| 5 | panels + plugin tabs | |
| 5 | sessions: the archived section lists what a newest-first window hides | `seedMany`, `listCalls`, `clearMany` |
| 30 | plugin tab: Work | `agentStatus`, `workRelease`, `workTake`, `workSetUnassigned`, `procStats`, `procTailDelay` |
| 9 | plugin tab: Usage | |
| 39 | plugin tab: Ideas | `ideaRefines` |
| 12 | plugin tab: Diagnostics | `diagCallsDelay`, `diagCallsStats` |
| 16 | plugin tab: Files | `filesOpened`, `filesOpenCalls`, `filesCalls`, `filesDelay` |
| 7 | plugin tab: MCP | |
| 5 | ask_user: questions in the chat | |
| 18 | plan mode: the pill, the plan card and its decisions | |
| 5 | guardrails: a tool call waits for your OK | |
| 3 | fork: a new chat from a message | |
| 1 | composer: narrow widths | |
| 1 | narrow side panels | |
| 8 | project picker + new session project | |
| 10 | start screen: the project new sessions start in | |
| 8 | welcome screen: ideas to work on | `capabilities` |
| 15 | projects dialog | |
| 26 | settings: controls, agents, budget, tools | |
| 6 | chat tools: switched per session | |
| 13 | budget: chat cost, Work tab, a chat stopped by the budget | |
| 13 | profiles: settings, a project default, per chat | |
| 1 | settings | |
| 1 | drag to reorder tabs | |
| 1 | abort | |
| 1 | images | |
| 5 | composer drafts | `msgLoads` |
| 24 | web tools, todo plan, tools notice, file links | `thinkDelay`, `filesOpened`, `filesOpenCalls` |
| 6 | ssh tools | |
| 6 | fast steps: layout stability | |
| 4 | provider tools: results live | |
| 3 | compaction banner | |
| 6 | goals | |
| 6 | projects: folder picker | |
| 3 | plugin tab hot reload / error | |
| 16 | notifications | `offline`, `online` |
| 2 | a snapshot answer must not undo what it missed | `messagesHold`, `msgLoads` |
| 2 | deleting a chat leaves nothing of it behind | |
| 5 | capability removal and recovery | `capabilities` |
| 10 | sessions: collapsible groups (defaults, persistence, search) | |

The sections without mock controls need only state the real server can be given: sessions, projects, ideas and
settings through RPC, and a model's behaviour through MockLlm's scenario tags (`[s:tools]`, `[s:ask]`, …; add one where a
section needs a reply MockLlm cannot give yet). Port those first.

The mock controls map to:

- `thinkDelay`, `queueInternal`, `agentStatus`, `workRelease`, `workTake`, `workSetUnassigned`: a model call held open
  and released on demand. MockLlm's `HoldMs` is a fixed delay; per-request hold/release gates are the follow-up already
  named in [the e2e follow-ups](2026-10-03-e2e-followups.md).
- `seedMany`/`clearMany`: sessions created through RPC; `listCalls`, `msgLoads`, `diagCallsStats`, `filesCalls`: the
  page's own WebSocket frames, read in Playwright (`page.on('websocket')`), not counters in a fake server.
- `diagCallsDelay`, `filesDelay`, `messagesHold`, `procTailDelay`: a slow answer — a held model call, a large
  directory, or a frame held back in Playwright.
- `filesOpened`/`filesOpenCalls`: `files.open` hands a path to the operating system; the test reads the `files.open`
  frames it sent instead of opening anything.
- `capabilities`: switch the plugin off and on (`plugins.setEnabled`).
- `offline`/`online`: Playwright's `context.setOffline`.
- `procStats`: a real background `bash` process.
- `ideaRefines`: the chat `ideas.refine` creates, read with `sessions.list`.
