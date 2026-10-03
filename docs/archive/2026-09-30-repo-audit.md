# NetPI repository audit — 2026-09-30

Archived 2026-10-03: historical audit of the commit named below, not a current defect list. Consult the Ideas backlog and current source for individual findings; archiving this report does not claim every finding is fixed.

Reviewed commit `62d40102b97e58e54ac1ed604b38195355b3ed6f` on isolated branch `codex/repo-audit`. This is a review report; application source was not changed or installed. The running app was queried only for read-only RPC metadata. Consolidated into the main repository on 2026-10-01. References now point into the main checkout; line numbers and findings describe the reviewed commit above and may have changed since. The original report and probe evidence are preserved under artifacts/consolidation/2026-10-01/NetPI-repo-audit/.

P1 means fix soon because the behavior can execute unintended work, lose state, or cross chat boundaries. P2 means a concrete correctness defect. P3 means a lower-impact resource/lifecycle defect. Reproduced means a focused local probe exercised actual application logic; it does not imply a live provider or full browser walkthrough.

## Problems, in recommended order

### 1. P1 — Tool repair executes documentation examples

[ToolRepairPlugin.cs:61](C:/AI/Projects/NetPI/plugins/NetPI.ToolRepair/ToolRepairPlugin.cs:61), [ToolCallTextParser.cs:49](C:/AI/Projects/NetPI/plugins/NetPI.ToolRepair/ToolCallTextParser.cs:49).

Repair scans assistant text for tool markup without distinguishing an intended call from a quoted example. A probe supplied a normal answer beginning “Here is an example; do not run it” and a fenced XML `write` example. Repair returned a real `write` call and changed the stop reason to `tool_use`. The setting is enabled by default. Normal tool hooks still run, but an allowed write or shell operation can occur despite being presented as an explanation.

Restrict repair to an explicit standalone call envelope or provider-specific recovery format, and leave documentation/prose examples as text. Existing tests deliberately support fenced calls, so establish an unambiguous recovery rule rather than banning every fence. Add negative tests for explaining tool syntax, quoted source and answers containing both prose and markup.

### 2. P1 — Settings writes race, and failures leave unsaved live state

[SettingsStore.cs:118](C:/AI/Projects/NetPI/src/NetPI.Host/Settings/SettingsStore.cs:118), [line 147](C:/AI/Projects/NetPI/src/NetPI.Host/Settings/SettingsStore.cs:147), [line 224](C:/AI/Projects/NetPI/src/NetPI.Host/Settings/SettingsStore.cs:224).

`Set` and `Replace` update the in-memory document and `_lastText` under a lock, then persist outside that lock. All writers use `settings.json.tmp`. Concurrent callers can interfere with the temporary file or install an older snapshot after a newer snapshot. A 12-round probe with 16 simultaneous writers produced 60 write failures and memory/disk mismatches in 11 rounds.

A deterministic second probe made the destination read-only, attempted a change, removed the restriction and retried the same value. The first attempt threw, but the live value was already changed; the retry returned as a no-op. Result: `memory=new disk=old`. No change event is published on the failed write even though readers see the new value.

Serialize writes with an ordered writer gate separate from readers, use unique temporary paths, and make mutation/persistence/event publication one defined success operation. Add concurrent distinct-key tests and failed-write-then-identical-retry coverage. Preserve the goal of keeping disk retries off the reader lock.

### 3. P1 — Concurrent ask_user questions can receive the wrong chat's answer

[AskPlugin.cs:69](C:/AI/Projects/NetPI/plugins/NetPI.Ask/AskPlugin.cs:69), [line 94](C:/AI/Projects/NetPI/plugins/NetPI.Ask/AskPlugin.cs:94), [asks.svelte.js:8](C:/AI/Projects/NetPI/web/src/lib/state/asks.svelte.js:8), [line 87](C:/AI/Projects/NetPI/web/src/lib/state/asks.svelte.js:87).

Pending questions are globally keyed only by the provider's tool-call ID. Two chats with the same call ID generated two question events but one pending entry. An answer intended for the first chat was delivered to the second; the first stayed yielded. UI maps and `ask.answer` carry the same insufficient identity.

Generate a host question ID and propagate it through events, RPC, stored UI state and cards, following the existing unique guard-approval ID pattern. Test duplicate provider IDs across simultaneous chats and withdrawal of each question independently.

### 4. P2 — Goal token accounting skips calls intercepted by earlier hooks

[AgentRunner.cs:220](C:/AI/Projects/NetPI/plugins/NetPI.Runtime/AgentRunner.cs:220), [GoalPlugin.cs:64](C:/AI/Projects/NetPI/plugins/NetPI.Goal/GoalPlugin.cs:64), [line 76](C:/AI/Projects/NetPI/plugins/NetPI.Goal/GoalPlugin.cs:76).

The first after-call hook decision ends hook dispatch. Nudge, Loops and ToolRepair run before the Goal hook, which counts usage. With Goal plus the default Nudge behavior and a 200-token goal budget, the probe made eight calls consuming 880 tokens, while the goal recorded 220. This concerns the goal token budget; it is separate from the persistent dollar reservation ledger.

Separate unconditional observers/accounting from decision hooks, or meter goal calls through model middleware. Add a composed Goal + Nudge test comparing recorded usage against all assistant calls. Moving GoalHook earlier also changes its before-call ordering and is a fragile fix. Counting child-agent tokens toward parent goals is an already documented future capability.

### 5. P2 — Default subagents inherit a stored model rather than the effective model

[AgentRunner.cs:84](C:/AI/Projects/NetPI/plugins/NetPI.Runtime/AgentRunner.cs:84), [AgentRuntime.cs:791](C:/AI/Projects/NetPI/plugins/NetPI.Runtime/AgentRuntime.cs:791), [line 884](C:/AI/Projects/NetPI/plugins/NetPI.Runtime/AgentRuntime.cs:884).

A parent selected through `meta.agent` can have `session.Model=null` while running a named agent's model. Default spawning reads only the nullable stored model. Reproduced: parent ran `cloud/big`; child with no explicit model ran global default `fake/local`. A global default without a configured agent can instead make the child fail.

Centralize effective-model resolution and use it for turns, default delegation and compaction. Add inheritance tests with only a named-agent selection on the parent, including reasoning inheritance.

### 6. P2 — Removing an agent still grants its queued requests

[AgentScheduler.cs:305](C:/AI/Projects/NetPI/plugins/NetPI.Agents/AgentScheduler.cs:305), [line 216](C:/AI/Projects/NetPI/plugins/NetPI.Agents/AgentScheduler.cs:216), [line 559](C:/AI/Projects/NetPI/plugins/NetPI.Agents/AgentScheduler.cs:559).

Removal marks a pool unconfigured and clears its models. Unconfigured pools are considered available, so existing waiters receive leases when an owner releases. Reproduced with one occupied slot, one waiter, deletion, then release: the waiter received the deleted agent's lease with `Configured=False`. Clearing its model association also impairs shared local-model slot accounting while owners remain.

Represent retired named pools separately from ordinary anonymous pools. Reject queued requests on removal, let existing owners finish and preserve their model association until release. Test removal while busy and queued.

### 7. P2 — Completed streaming text survives reconnect as a duplicate

[chat.svelte.js:172](C:/AI/Projects/NetPI/web/src/lib/state/chat.svelte.js:172), [line 367](C:/AI/Projects/NetPI/web/src/lib/state/chat.svelte.js:367), [app.svelte.js:222](C:/AI/Projects/NetPI/web/src/lib/state/app.svelte.js:222).

If the answer finishes while the browser is disconnected, it misses `stream.end` and `message.added`. Reconnect reloads persisted messages, but leaves the old stream. `runEnded` clears only an already-ended stream. The UI logic probe ended with the final persisted answer plus an active stale partial answer; that stream also prevents cache eviction.

Reconcile transient state against refreshed run/message state even when the terminal event was missed. Give streams an identity and a snapshot/replay recovery path for runs still active. Test a run finishing while offline, not only events arriving after reconnection.

### 8. P2 — Files tab late responses can cross workspace boundaries

[FilesTab.svelte:30](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/ui/FilesTab.svelte:30), [line 105](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/ui/FilesTab.svelte:105), [line 127](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/ui/FilesTab.svelte:127).

Directory responses apply without verifying the workspace captured at dispatch. Workspace switches also fail to invalidate a pending search or rerun an unchanged search query. Reordered-response probe: request A, switch to B, resolve B then A; the active chat was B but the file root was A. Opening a file or inserting a relative mention can target the wrong workspace.

Capture a workspace generation on every directory/search/git request; discard stale results, clear caches immediately and rerun an existing query when scope changes. Test reordered responses and unchanged-query workspace switches.

### 9. P2 — Draft image attachments disappear after chat-cache eviction

[chat.svelte.js:151](C:/AI/Projects/NetPI/web/src/lib/state/chat.svelte.js:151), [line 159](C:/AI/Projects/NetPI/web/src/lib/state/chat.svelte.js:159), [line 399](C:/AI/Projects/NetPI/web/src/lib/state/chat.svelte.js:399).

Only draft text is persisted. Images belong to the disposable five-store history cache. Attach an image, visit five other idle chats, return: the probe's image count changed from one to zero, even though the original tab could remain open.

Keep unsent composer state independently of cached message windows, with a bounded attachment store and explicit lifecycle. Test cache eviction before send.

### 10. P2 — Older archived chats disappear from the archive interface

[SessionsTab.svelte:68](C:/AI/Projects/NetPI/web/src/components/panels/SessionsTab.svelte:68), [SessionStore.cs:212](C:/AI/Projects/NetPI/src/NetPI.Host/Sessions/SessionStore.cs:212), [line 220](C:/AI/Projects/NetPI/src/NetPI.Host/Sessions/SessionStore.cs:220).

Source-verified: the UI fetches the newest 200 active-plus-archived sessions, then filters archives locally. Two hundred newer active chats hide every older archive and can make the UI say “Nothing archived.” Normal search excludes archives too.

Add an additive archived-only filter and paginate the result. Main-list and search caps also need usable pagination rather than silent omission.

### 11. P2 — SSH timeout does not cover stdin transmission

[SshCore.cs:353](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Ssh/SshCore.cs:353), [line 361](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Ssh/SshCore.cs:361).

The launcher creates its timeout after writing stdin. With 8 MiB of input and a child that never reads, the real launcher ignored a configured 100 ms timeout and returned only on caller cancellation after about two seconds: `TimedOut=False`, `Aborted=True`. Without abort, transmission can remain stuck.

Create one deadline before starting/transmitting, and apply its linked token to writing, waiting and cleanup. Add a blocked-stdin launcher test; fake SSH launchers do not expose this path.

### 12. P2 — ssh_edit can overwrite concurrent changes or leave partial files

[SshTools.cs:480](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Ssh/SshTools.cs:480), [line 605](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Ssh/SshTools.cs:605).

Source-verified: the concurrent-change marker is byte size plus whole-second mtime. A same-size modification within the second passes validation. The subsequent `cat >` truncates and rewrites the file; there is also a check/write race and interruption can leave partial contents.

Validate content identity and replace through a neighboring temporary file with metadata preservation. Use a remote lock for cooperating edits, and make clear that arbitrary external writers require coordination; a hash alone does not close the check/write race. Test a same-size change with unchanged whole-second mtime and interrupted replacement.

### 13. P2 — Chat Completions parsers lose fragmented tool names

[ChatTransport.cs:266](C:/AI/Projects/NetPI/plugins/NetPI.Providers.AiProxy/ChatTransport.cs:266), [OpenRouterChat.cs:411](C:/AI/Projects/NetPI/plugins/NetPI.Providers.OpenRouter/OpenRouterChat.cs:411).

Both parsers stop updating the name once the ToolCallPart exists. Actual parser probes receiving `wr` then `ite` emitted `wr`, causing lookup failure. Late call IDs have a related stale-part problem.

Accumulate name/ID metadata until the call is complete, or update the emitted part as fragments arrive. Add split-name and late-ID tests alongside argument-fragment coverage.

### 14. P2 — Read-only diagnostics are declared writable

[DiagnosticsPlugin.cs:27](C:/AI/Projects/NetPI/plugins/NetPI.Diagnostics/DiagnosticsPlugin.cs:27), [ScopedRegistries.cs:100](C:/AI/Projects/NetPI/src/NetPI.Host/Plugins/ScopedRegistries.cs:100), [scripts/netpi.mjs:80](C:/AI/Projects/NetPI/scripts/netpi.mjs:80).

The diagnostics methods use the Register overload whose metadata defaults to `readOnly=false`. Verified against the running app: `node scripts/netpi.mjs` refused `diag.overview`, and `methods diag.` marked every diagnostic view writable. The arbitrary read-only RPC action of the diag tool has the same metadata boundary. Its fixed named actions remain separately callable.

Explicitly mark the read-only diagnostic registrations, retaining reload as writable. Audit other plugin read RPCs such as goals, budget, files and pending questions, then test the actual `rpc.list` metadata consumed by the CLI/tool.

### 15. P3 — Process row collapse can leave subscriptions and polling alive

[ProcessRow.svelte:43](C:/AI/Projects/NetPI/plugins/NetPI.Work/ui/ProcessRow.svelte:43), [line 55](C:/AI/Projects/NetPI/plugins/NetPI.Work/ui/ProcessRow.svelte:55).

Opening awaits output before subscribing. Collapse or destruction during that await cleans up resources before they exist; the old opening operation then creates a listener and timer. The UI logic probe finished closed with an active subscription and polling interval. Repeated toggles can overwrite handles and leak more resources.

Use a generation/disposed guard after the await and one lifecycle owner for both resources. Test delayed output followed by collapse and destruction.

## Additional compaction concern

[CompactionService.cs:286](C:/AI/Projects/NetPI/plugins/NetPI.Compaction/CompactionService.cs:286), [line 299](C:/AI/Projects/NetPI/plugins/NetPI.Compaction/CompactionService.cs:299): request budgeting assumes the previous summary fits the current output allowance, then includes the entire previous summary. After switching to an 8K summarizer, a 48K-character prior summary produced 58,049 request characters, or 14,512 tokens under the harness estimate. This probe used a fake overflow response, not a real tokenizer/provider. Budget the actual prior summary and compact it separately when needed; add a large-summary-to-small-model regression.

## Refactor opportunities

1. Separate after-call observers from decision hooks. Metering and diagnostics must always observe a call; nudge/repair/stop decisions may still use ordered arbitration.
2. Use one effective-model resolver for turns, delegation and compaction. Nullable stored model fields and named-agent overrides currently drift.
3. Give scheduler pools explicit configured, anonymous and retired states, keeping owners and waiters subject to their actual lifecycle.
4. Share provider helper source at compile time. The five Common files are mirrored in three providers; namespace differences prevent byte-identical hashes, but their code explicitly requires manual synchronization. Linked shared source compiled into each plugin preserves collectible plugin isolation and avoids plugin-to-plugin references. Tool-call parsing is another useful extraction.
5. Share UI scoped-request/lifecycle helpers and keep composer drafts separate from chat history caching. These are direct fixes for several reproduced UI defects.
6. Centralize read-only RPC registration helpers and add metadata assertions for public read surfaces.

## Optimization opportunities

- [ReadTool.cs:130](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/Tools/ReadTool.cs:130): every page of a file over 32 MiB scans to EOF to count lines; negative offsets scan twice. Stream only through the required window where possible, or cache a line index/count by file version. Its StreamReader also uses UTF-8 even when the sample was identified as Latin-1; preserve decoding consistency.
- [GrepEngine.cs:107](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/Search/GrepEngine.cs:107): each concurrent grep can use every processor and load whole files up to 32 MiB. Measure concurrent-agent memory/latency, then cap shared work and stream plain searches. `FilesSearched` currently reports all candidates even when scanning stopped early.
- [AssistantText.svelte:23](C:/AI/Projects/NetPI/web/src/components/chat/AssistantText.svelte:23): long streaming answers reparse/sanitize all accumulated Markdown every 100 ms. Benchmark long answers and consider a stable rendered prefix or adaptive timing. No speedup measurement was made in this review.
- [CallsView.svelte:18](C:/AI/Projects/NetPI/plugins/NetPI.Diagnostics/ui/CallsView.svelte:18): make polling single-flight and schedule the next refresh after completion; slow RPCs currently permit overlap.
- [WebFetchTool.cs:14](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Web/WebFetchTool.cs:14): coalesce concurrent cold requests for the same URL/format. Add a refresh flag and expose cache age so checking a changing page does not silently reuse five-minute-old content.
- [FileIndex.cs:14](C:/AI/Projects/NetPI/plugins/NetPI.Tools.Files/FileIndex.cs:14): cache eviction does not evict the per-root semaphore dictionary. Use a bounded per-root lifecycle without disposing a gate still in use.

These are source-based optimization candidates, not measured claims that they dominate normal workloads. Start with read paging, orphan polls and concurrent grep because their unnecessary work is explicit.

## Missing capabilities worth adding

- CI enforcement first: no checked-in workflow runs the existing suites. Start with Windows build + unit + mock/UI checks, and add Linux core coverage. Add composed-plugin, reconnect, reordered-response, cache-eviction and pagination regressions from this audit.
- Forward message pagination plus archived-only retrieval/search: useful existing-history access is currently limited by beforeSeq-only history and fixed result caps.
- Language-server diagnostics/symbol navigation as a plugin: improves code work with fast changed-file feedback.
- PDF/Office text extraction as a plugin: expands useful source material beyond text/images.
- Scheduled runs with persisted schedules, cancellation, model/agent availability and the existing budget controls.
- Explicit web_fetch refresh/cache-age controls: small feature with immediate value for app testing and changing information.

Language servers, document reading and scheduling are already acknowledged in docs/STATUS.md. MCP, partial rollback, automatic worktrees and goal-completion verification were deliberately dropped/deferred in the earlier harness decisions; this review does not relabel them accidental omissions.

## Validation and limits

| Check | Result |
|---|---|
| Full .NET solution build | Passed; 7 warnings, 0 errors |
| Host | 58 passed |
| Tools | 59 passed |
| Agent | 128 passed |
| Providers | 41 passed; 336 checks |
| Aux | 185 passed, 1 failed |
| Total existing unit tests | 471 passed, 1 failed |
| Focused settings/runtime/provider probes | Reproduced the cases described above |
| UI probes | Actual source logic with mocked Svelte primitives/RPC/timers; four issues reproduced |

The Aux failure is the isolated headless “user's Chrome remains alive after plugin unload” case at [WebTests.cs:551](C:/AI/Projects/NetPI/tests/NetPI.Aux.Tests/WebTests.cs:551). A focused rerun also failed; cause is unresolved. It is not evidence that the actual user's browser was closed. No live paid-provider, Linux/macOS, full UI walkthrough or full E2E run was performed here.

Retained probes in this checkout:

- `artifacts/audit/probe/Probe.csproj` — settings race and failed-write retry.
- `artifacts/audit-runtime-repros/AuditRepros.csproj` — questions, goals, inheritance, scheduler removal, compaction request shape.
- `artifacts/audit-providers/Audit.csproj` — quoted repair, fragmented names, blocked SSH stdin.

Build output and probes live under the isolated worktree's ignored artifacts directory. No application source was edited, no build was published, and the user's live settings/data were not modified.
