# Ideas audit — 2026-10-01

Reviewed all 107 records in the initial live backlog: 91 stamped NetPI and 16 external or unbound. Read the active plans and historical completion/consolidation records, traced NetPI implementation claims through source and history, and used the owning unit suites and full E2E gate for the changes made here. Historical completion records were retained unless source contradicted them; this was not a new live reproduction of every old bug.

External model research, NInfer engine work, radio features and remote administration were reviewed as backlog records. Their source/deployment state was not independently verified in this NetPI audit. Their status was preserved and the limitation recorded; no remote cleanup or device control was performed.

Updated 44 existing records. Closed four: effective model resolution, end-of-run Ideas checks, the completed turn-context optimization, and a duplicate compound item whose remaining SSH task already has a keeper. Reopened one incorrectly completed native Windows plugin build. Added two follow-ups. The resulting backlog has 109 records: 74 done, 2 rejected, 30 open, 2 planned and 1 parked.

## Work landed

| Commit | Result |
| --- | --- |
| 1e70e6f (merge 8df87bb) | Shared effective-model resolution for turns, implicit children and compaction. Named-agent parents now pass their effective model and compatible reasoning to children. |
| 22db03d (merge 2c11cf7) | Deterministic report-before-wait regression: three model slots, an explicitly held quick report and synchronization on its queued notification. |
| fbad7b4 (merge 37eb4cf) | Commit checks wait for active project runs and revalidate the judged revision. Tab-close save checks consult runtime state and suppress a same-title plan already saved by that session. |

The test correction was required by a failure met during the full merge gate: two slots could deadlock the test when the slow child started first, and the quick report's arrival was otherwise uncontrolled. It was fixed, not accepted on a passing retry. The corrected case passed 20/20 fresh test-process measurements.

Final gate: 615 unit tests passed (Providers 44 / 347 checks, Tools 69, Agent 143, Host 81, Aux 278); full E2E 64/64, 831 checks. The affected Ideas E2E selection also passed 11/11, 213 checks. Evidence is retained under artifacts/ideas-audit-2026-10-01. No build was published or installed into the running app.

## Material corrections

- Native Windows UIA automation was marked done without its plugin or windows.list/read/do in current source. The cited commit bf86e00 was measurement documentation. Reopened idea-xb9rhc for the native build; the already implemented BrowserTool remains separate. Corrected the linked research entry idea-72c2hh.
- Turn-context caching was already shipped in 53cf32e and 4c74294. Closed idea-jl8q3b and split the small deletion-query optimization into idea-3kcbzn at low priority.
- The old "plugin tabs never unmount" claim is obsolete: SidePanel keeps three plugin tabs. The stricter cache branch was reverted, so long-lived map caps remain open under idea-wwm10y.
- Integer Ideas revisions, hard bounded admission, SQLite storage, large-file read paging, file-index gate retirement, web-fetch coalescing/cache controls and single-flight Diagnostics polling have already landed. Updated their keepers to describe only remaining work.
- Read-only RPC metadata exists, but Ideas read methods still register as writable by default. Kept incomplete adoption with the declarative RPC/schema item rather than creating a duplicate.
- The learnings-journal idea is rejected by design: lean AGENTS.md and AgentsMd guidance are the chosen home. CI, first-class web tools, screenshots and the global SQLite backlog were verified and their stale summaries corrected.
- Event-bus isolation/backlog bounds remain open. Recorder and WebSocket work have landed; another agent's watchdog work is separate from replacing serial delivery or bounding the channel.

## New follow-ups

- **idea-99obnl — Diagnostics calls polling can restart after its view unmounts** (medium): CallsView starts load().then(poll), but teardown only clears the current timer. If a delayed diag.calls resolves after destruction, its continuation schedules another poll and retains the destroyed view.
- **idea-3kcbzn — Batch session and project deletion reads** (low): The high-impact turn-context reread work in idea-jl8q3b is done. Keep the smaller deletion-path query reduction separately: avoid repeated GetSession reads when deleting sessions or detaching a project’s sessions.

The Diagnostics finding is source-derived: load().then(poll) can rearm after teardown because cleanup only clears the currently scheduled timer. Its idea records a held-RPC regression to prove the fix. No runtime reproduction was claimed in this audit.

## Every original record

“Retained” means its existing completion, rejection or consolidation record was reviewed and not contradicted; it does not claim a fresh live verification. External titles are omitted here to keep infrastructure details out of repository documentation; the original records remain intact in the Ideas store.

| Idea | Scope / current title | Before → after | Audit disposition |
| --- | --- | --- | --- |
| idea-69ld2i | Decision add/done/update should run on turn_end | open → done | Closed; evidence/scope split recorded |
| idea-eag4x5 | When clicking load more in chat scroll should stay | done → done | Historical completion record retained |
| idea-zj3o8s | Steering - send | done → done | Historical completion record retained |
| idea-dac7rt | Use a low-priority qwen agent for decision verification | open → open | Scope/evidence corrected; status preserved |
| idea-lk4gm7 | On git commit/merge insert instruction to perhaps close idea | done → done | Historical completion record retained |
| idea-zk1fbw | Sleep blocker plugin | open → open | Scope/evidence corrected; status preserved |
| idea-pii7hv | MCP tab - move buttons to a dropdown | done → done | Historical completion record retained |
| idea-hai71q | Ideas should support images also | done → done | Historical completion record retained |
| idea-8hfc3m | cant send pasted image | done → done | Historical completion record retained |
| idea-hd002w | AiSwitcher — external record | done → done | Historical status retained; live state unverified |
| idea-fg89jf | Ideas update | done → done | Scope/evidence corrected; status preserved |
| idea-qrp60h | Idea tab - collapse new idea | done → done | Historical completion record retained |
| idea-9smcwz | Agent capacity and delegation: finish shared model bounds, fairness and truthful UI | open → open | Scope/evidence corrected; status preserved |
| idea-ohbk76 | Decision - JEV local models | done → done | Retained: Merged into idea-61dla9 (2026-09-29) |
| idea-plfnjb | Use decision to update TODO list | done → done | Retained: Merged into idea-0m4hml (2026-09-29) |
| idea-vln7ps | Use laya for skill selection ? | done → done | Retained: Merged into idea-0m4hml (2026-09-29) |
| idea-qz1a5z | Retry message is to verbose | done → done | Historical completion record retained |
| idea-b7himr | Ideas - Send | done → done | Historical completion record retained |
| idea-xzsy6z | Change the fav-buttons | done → done | Historical completion record retained |
| idea-kp4fq5 | More context info | done → done | Historical completion record retained |
| idea-nt6ozx | First-class web tools: http_fetch + web search | done → done | Scope/evidence corrected; status preserved |
| idea-9uimir | Screenshot tool for the running UI | done → done | Scope/evidence corrected; status preserved |
| idea-78xc1t | Project learnings journal (separate from ideas backlog) | rejected → rejected | Scope/evidence corrected; status preserved |
| idea-2pmvrl | Nudge: reset the counter when an acceptable response comes in | done → done | Scope/evidence corrected; status preserved |
| idea-icqyn3 | Transient sessions: don't save a session closed before doing anything | done → done | Historical completion record retained |
| idea-xmh9nu | Global project-stamped Ideas backlog (now stored in SQLite) | done → done | Scope/evidence corrected; status preserved |
| idea-99skcc | Sysadmin — external record | done → done | Historical status retained; live state unverified |
| idea-k7q2vn | AiSwitcher — external record | done → done | Historical status retained; live state unverified |
| idea-tdxz0s | Fix the two failing mock-e2e checks: reconnect clicks a forked session; folded steps jump 19px at run start | done → done | Historical completion record retained |
| idea-0m4hml | In-conversation checks on the agent's own cached context (stuck? finished? ask the user?) | planned → planned | Scope/evidence corrected; status preserved |
| idea-sxhqrz | Turn on the Guardrails second opinion | done → done | Historical completion record retained |
| idea-swh60a | decide: bulk work to the nuc, single questions to NInfer | done → done | Retained: Merged into idea-0m4hml (2026-09-29) |
| idea-ud0b0a | Decision-based routing: thinking on/off per turn, which subagent or tool | done → done | Retained: Merged into idea-0m4hml (2026-09-29) |
| idea-c7xyem | Ideas follow the session: recall on the first message, save on tab close, close on commit | done → done | Historical completion record retained |
| idea-72c2hh | Browser and computer use with decision models | done → done | Scope/evidence corrected; status preserved |
| idea-grc0s8 | Decisions: treat near-ties as uncertain | done → done | Retained: Merged into idea-0m4hml (2026-09-29) |
| idea-f82y7z | Run the E2E suite on the merged prompt-openrouter-fixes | done → done | Scope/evidence corrected; status preserved |
| idea-61dla9 | Decision models: quality work on the nuc | open → open | Scope/evidence corrected; status preserved |
| idea-goh85a | AiSwitcher — external record | rejected → rejected | Historical status retained; live state unverified |
| idea-2a3kig | Unbound — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-im8862 | Unbound — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-ok4vjr | Unbound — external record | done → done | Retained: Merged into idea-im8862 (2026-09-29) — still parked |
| idea-x54mtm | Unbound — external record | done → done | Historical status retained; live state unverified |
| idea-hmwtec | Unbound — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-n2ez7n | Sysadmin — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-c66jtk | AiSwitcher — external record | done → done | Historical status retained; live state unverified |
| idea-i07yfc | Evaluate CLM-8B (contrastive System One) behind the gateway | done → done | Historical completion record retained |
| idea-v9dg6g | AiSwitcher — external record | parked → parked | Notes reviewed; deployment unverified; status preserved |
| idea-evz0xv | Train a CLM-style head on our own decisions | done → done | Retained: Merged into idea-61dla9 (2026-09-29) |
| idea-xb9rhc | Native Windows computer-use plugin: UIA read/actions, guards and step journal | done → planned | Reopened: implementation absent |
| idea-sbdays | ninfer: a decision right after an agent turn can destroy the agent's cached context | done → done | Historical completion record retained |
| idea-rlp1hi | Unbound — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-43oruq | Ideas tab: a calm overview, titles only | done → done | Historical completion record retained |
| idea-ndw51f | E2E flake: server went unresponsive after "delete an orchestrator" (one run) | done → done | Historical completion record retained |
| idea-b51rg6 | Yue2-Radio — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-kuul8u | Self-diagnosis: a diag tool for agents, and tool-set changes with their cause | done → done | Historical completion record retained |
| idea-yvcy8b | RPC schemas, parameter validation and generated protocol checks | open → open | Scope/evidence corrected; status preserved |
| idea-m7vmue | A `session.changed` event, so plugins stop string-matching each other's notice kinds | done → done | Historical completion record retained |
| idea-xiv057 | Dev builds never touch the running app: an isolated `artifacts/dev` output for agents, and `publish` / `publish --next-start` as the deliberate install | done → done | Historical completion record retained |
| idea-woa84g | A `process wait` that returns the moment a background job exits — waiting becomes one call, not a sleep loop | done → done | Historical completion record retained |
| idea-de1s7t | diag: reach the rest of the app — an RPC passthrough, and a journal that does not lose events | done → done | Historical completion record retained |
| idea-84qzie | diag rpc: a numeric parameter never reaches the method | done → done | Historical completion record retained |
| idea-jl8q3b | Incremental per-turn conversation context | open → done | Closed; evidence/scope split recorded |
| idea-07x8iz | Isolate event-bus subscribers and bound the delivery backlog | open → open | Scope/evidence corrected; status preserved |
| idea-cov6rl | All database work is serialized on one global lock, and row JSON is parsed inside it | done → done | Historical completion record retained |
| idea-wwm10y | Bound client caches that survive long-lived sessions | open → open | Scope/evidence corrected; status preserved |
| idea-z92w8c | Bound scheduler and Ideas work; SSH reuse consolidated | open → done | Closed; evidence/scope split recorded |
| idea-ui6yc9 | Performance pass: what was implemented, what was rejected, and two test traps | done → done | Historical completion record retained |
| idea-ndum5b | Sysadmin — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-r77wjz | Sysadmin — external record | open → open | Notes reviewed; deployment unverified; status preserved |
| idea-pac35h | Reuse one SSH connection per host on Windows (not via ControlMaster) | open → open | Scope/evidence corrected; status preserved |
| idea-21mwu3 | Show Ideas background-check waits and drops in the Work tab | open → open | Scope/evidence corrected; status preserved |
| idea-lpenip | Tool repair executes documentation examples — it must leave quoted syntax as text | done → done | Historical completion record retained |
| idea-zezxus | Settings writes race, and a failed write leaves unsaved live state | done → done | Historical completion record retained |
| idea-2opoge | ask_user: a pending question is keyed only by the provider's tool-call ID | done → done | Historical completion record retained |
| idea-ib25hg | Goal token accounting skips calls an earlier hook already decided | done → done | Historical completion record retained |
| idea-cozvvq | One effective-model resolver for turns, delegation and compaction | open → done | Closed; evidence/scope split recorded |
| idea-me3lbx | Removing an agent still grants its queued requests | done → done | Historical completion record retained |
| idea-nw51b4 | A run that finishes while disconnected comes back as a duplicate answer | done → done | Historical completion record retained |
| idea-79qa3w | Files tab: late responses can apply to the wrong workspace | done → done | Historical completion record retained |
| idea-9igyww | Composer attachments disappear when the chat cache is evicted | done → done | Historical completion record retained |
| idea-6ro0nf | Older archived chats disappear: an archived-only filter and real pagination | done → done | Historical completion record retained |
| idea-icpj5p | SSH: the timeout starts after stdin transmission | done → done | Historical completion record retained |
| idea-wpxukm | ssh_edit can clobber concurrent edits and leave a partial file | done → done | Historical completion record retained |
| idea-qbjhk9 | Chat Completions parsers lose fragmented tool-call names | done → done | Historical completion record retained |
| idea-o934y1 | Read-only diagnostics are registered as writable RPCs | done → done | Historical completion record retained |
| idea-ckqma4 | Compaction budgets the previous summary as if it were fresh output | done → done | Historical completion record retained |
| idea-7c1wg7 | Collapsing a process row during its open await leaks a subscription and a timer | done → done | Historical completion record retained |
| idea-k9xztu | CI gates for unit, UI and end-to-end suites | done → done | Scope/evidence corrected; status preserved |
| idea-pth2w0 | Measure and bound concurrent grep work; report searched files accurately | open → open | Scope/evidence corrected; status preserved |
| idea-f7o4yp | Share the provider Common source at compile time | open → open | Scope/evidence corrected; status preserved |
| idea-2o4rmv | Benchmark and reduce long streaming Markdown reparses | open → open | Scope/evidence corrected; status preserved |
| idea-t6odez | Forward message pagination for long chats | open → open | Scope/evidence corrected; status preserved |
| idea-fe26oh | Plugin: language-server diagnostics and symbol navigation | open → open | Scope/evidence corrected; status preserved |
| idea-lvk9z0 | Plugin: PDF and Office text extraction | open → open | Scope/evidence corrected; status preserved |
| idea-1y1174 | Plugin: scheduled runs | open → open | Scope/evidence corrected; status preserved |
| idea-ye3so7 | MCP connect: 1s server/discover probe kills HTTP servers on slow-resolving hostnames | done → done | Historical completion record retained |
| idea-tc1vzd | MCP: read remote resources (resources/list + resources/read) instead of forcing the server's fallback tool | open → open | Scope/evidence corrected; status preserved |
| idea-8t4amp | Compaction: the condense threshold's comment mis-describes its value (the value was right) | done → done | Historical completion record retained |
| idea-marq9s | Compaction: Chunk truncates a single oversized entry instead of splitting it | done → done | Historical completion record retained |
| idea-12wuj4 | Aux harness: FakeModelCatalog happily returns responses longer than the model allows | done → done | Historical completion record retained |
| idea-wz5uor | SETTINGS.md: the auto-compaction section doesn't mention the prior-summary condense step | done → done | Historical completion record retained |
| idea-ol00fp | Tools: say when a write or a commit leaves the chat's own checkout (worktrees) | done → done | Historical completion record retained |
| idea-phu8yb | agent_spawn: let the caller choose the project (one worktree per subagent) | done → done | Historical completion record retained |
| idea-r7kxmg | Work tab: show a changed budget limit at once | open → open | Scope/evidence corrected; status preserved |
| idea-7zha4j | Plugin independence: base features owned, enhancements optional, capabilities typed (review done, 6 steps to implement) | open → open | Scope/evidence corrected; status preserved |
| idea-8wvt4n | Investigate the E2E suite's transient failures: when a run goes red, the evidence does not say why | open → open | Scope/evidence corrected; status preserved |

## Recorded source findings for updated items

- **idea-69ld2i — Decision add/done/update should run on turn_end**: Implemented as fbad7b4, merged into master by 37eb4cf. Full gate: 615 unit tests and 64/64 E2E tests (831 checks); focused Ideas tests also passed. Commit sweeps wait while any project agent is running, queued or yielded, then resume on a terminal runtime event and read the current open ideas. A decision made against an old revision is discarded without advancing the commit cursor. Tab-close checks also consult runtime state, leave a still-active run retryable after the bounded wait, and suppress a same-title draft already saved by that session. Semantic deduplication of differently titled plans and a separate verifier agent are outside this fix.
- **idea-dac7rt — Use a low-priority qwen agent for decision verification**: Still open. IdeaAdmission already gives background checks low priority, bounded admission and paid-model gating; that is not the proposed verification agent. No second verifier, suspend/resume-on-higher-priority-work policy or verified automatic idea update was found. Keep separate from idea-69ld2i: end-of-run deferral fixes timing, not model verification.
- **idea-zk1fbw — Sleep blocker plugin**: Still open. process action wait and shell prompt guidance are implemented, but no hook/guard that refuses long deliberate sleeps was found in Guardrails or Shell. Define the sleep threshold and supported command forms before implementing, and avoid matching quoted documentation or legitimate commands whose arguments happen to contain sleep.
- **idea-fg89jf — Ideas update**: Historical handoff complete. Its later “still open” section is stale about SQLite storage, timestamp conflicts and slot escape: those have since shipped as plugin-owned SQLite tables, integer revisions and hard bounded admission. Keep the transaction/migration/history tests as completion evidence; Windows SSH and other separate tasks do not belong to this handoff.
- **idea-9smcwz — Agent capacity and delegation: finish shared model bounds, fairness and truthful UI**: The capacity/delegation plan is still partly outstanding. The scheduler has bounded queues, deadlines, retired-agent handling, catalog concurrency and priority/FIFO. CanGrant shares a local model bound only when ModelSlots can read catalog Concurrency; the fallback models.localSlots does not currently bound the sum across named pools. Fairness across pools, catalog-refresh behavior and truthful shared-capacity UI still need the planned tests. The effective-model subtask is now shipped as idea-cozvvq.
- **idea-nt6ozx — First-class web tools: http_fetch + web search**: Completion verified in current source: NetPI.Tools.Web registers web_fetch, web_search, screenshot and browser; TOOLS.md documents the first-class tools. The old summary describing curl-only access is obsolete.
- **idea-9uimir — Screenshot tool for the running UI**: Completion verified: ScreenshotTool captures a URL through headless Edge/Chrome and the NetPI desktop through desktop.capture when no URL is supplied. The headless server cannot capture a nonexistent desktop window; this limit is documented in TOOLS.md.
- **idea-78xc1t — Project learnings journal (separate from ideas backlog)**: The separate journal was rejected by an explicit design choice: docs/archive/2026-09-24-agent-tools.md records that durable learnings go into lean AGENTS.md rather than a separate journal. AgentsMdPlugin supplies this instruction and loads the instruction files. The old sketch was misleading without that decision.
- **idea-2pmvrl — Nudge: reset the counter when an acceptable response comes in**: Verified current NudgeHook clears the counter on an acceptable/non-nudged response. The recorded 3bdd894 work is in the current master tree; the old “not yet merged” wording describes an earlier point in time.
- **idea-xmh9nu — Global project-stamped Ideas backlog (now stored in SQLite)**: Historical completion retained, architecture corrected: the global project-stamped backlog shipped, then moved to plugin-owned SQLite tables in netpi.db. Ideas JSON now belongs to legacy migration/import/export; it is not the live primary store. See PLUGIN-IDEAS.md for the cutover and backup semantics.
- **idea-0m4hml — In-conversation checks on the agent's own cached context (stuck? finished? ask the user?)**: Keep planned. Existing Loops and Ideas consumers are not the full roadmap: Loops uses a clipped recent trace rather than the complete cached agent input; no automatic commit-based todo check-off, measured skill picker or decide.bulkModel routing was found. The folded ideas remain historical pointers here. Implement and measure one consumer at a time; preserve the near-tie safety requirement.
- **idea-72c2hh — Browser and computer use with decision models**: Research completion retained, but corrected the linked build claim: the measurements were done, browser interaction exists in NetPI.Tools.Web, and native Windows UIA integration is still outstanding under reopened idea-xb9rhc. The prior section saying the Windows plugin had landed was incorrect. Real-step training data remains a later task; no new measurements were performed here.
- **idea-f82y7z — Run the E2E suite on the merged prompt-openrouter-fixes**: Historical test-run task remains done. The later full gate for the effective-model change passed 64/64 E2E tests (823 checks), with all five unit suites green. This is verification of current behavior, not proof that unrelated historical transient failures have been diagnosed.
- **idea-61dla9 — Decision models: quality work on the nuc**: Reviewed the research and consolidated candidate list. Still open; this is decisions-lab/nuc model-quality work, not an implemented NetPI capability. No fresh model evaluation or nuc inspection was performed in this repository audit; old measurements are historical evidence. Truncated model links and unevaluated candidates still require verification when this research resumes.
- **idea-2a3kig**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-im8862**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-hmwtec**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-n2ez7n**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-v9dg6g**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-xb9rhc — Native Windows computer-use plugin: UIA read/actions, guards and step journal**: Reopened after source verification: plugins/NetPI.Tools.Windows and windows.list/windows.read/windows.do do not exist in current source or TOOLS.md. The cited bf86e00 commit is measurement documentation, not plugin implementation; the checklist is still all unchecked. The browser capability did ship through NetPI.Tools.Web/BrowserTool, so the remaining build is native Windows UIA automation and its guards, test window and step journal.
- **idea-rlp1hi**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-b51rg6**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-yvcy8b — RPC schemas, parameter validation and generated protocol checks**: Partly shipped: RpcMethodInfo carries ReadOnly, RegisterReadOnly exists, rpc.list exposes it and diag action rpc plus the CLI enforce it. Remaining: typed parameter schemas, validation and generated protocol/drift checks. Adoption is incomplete: Ideas read methods including ideas.list and ideas.get still use writable-default registration, so the CLI requires --write even for these reads. This audit verified List is SELECT-only before using it.
- **idea-jl8q3b — Incremental per-turn conversation context**: The high-impact turn-start/context work is complete: warm incremental context caching and safe invalidation landed in 53cf32e and 4c74294; tool parameter sizing and stale-catalog/ETag paths have also been corrected. JsonNode tool schema cloning remains necessary because nodes cannot have two parents; Recorder.InputChars reads string lengths without copying the input. The small remaining session/project deletion-query optimization is split into a new low-priority idea from this audit.
- **idea-07x8iz — Isolate event-bus subscribers and bound the delivery backlog**: Partly shipped: Recorder uses bounded previews and avoids the former lock-heavy payload work; WsHub limits clients, message size and queued bytes. The bus still has one serial dispatch loop and an unbounded channel. A slow-subscriber watchdog is being worked on by another agent; timeouts/diagnostics alone do not isolate subscribers or bound the backlog. Keep the remaining architectural work open and coordinate with that agent.
- **idea-wwm10y — Bound client caches that survive long-lived sessions**: Corrected stale scope. Source already has a three-plugin-tab LRU in SidePanel, a byte-bounded Markdown cache, streaming/frame work from e8bd46f and session-removal pruning. The stricter client-cache branch 299ae01 was reverted by 66c5097, so ancestry is not proof its caps are live. Remaining: closed asks/cleared approvals and other maps can accumulate throughout long-lived sessions; recall and saidJustNow primarily prune on deletion. A distinct CallsView teardown race is filed by this audit.
- **idea-z92w8c — Bound scheduler and Ideas work; SSH reuse consolidated**: Closed as consolidated. Three original items are implemented: scheduler queue/deadline bounds, SQLite Ideas storage, and single-flight commit rescans with reused timers. The one remaining task, Windows SSH connection reuse, is already tracked by idea-pac35h. Closing this duplicate does not claim connection reuse exists; its historical notes stay here.
- **idea-ndum5b**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-r77wjz**: external record reviewed; deployment/source outside NetPI was not independently verified. Status preserved.
- **idea-pac35h — Reuse one SSH connection per host on Windows (not via ControlMaster)**: Still open and now the sole keeper for the duplicated SSH task from idea-z92w8c. Windows SshCore starts a new ssh process per call and omits ControlMaster; no persistent per-host broker was found. Measure handshake cost and define framing, cancellation, reconnect and concurrent-command ownership before replacing the process-per-call path.
- **idea-21mwu3 — Show Ideas background-check waits and drops in the Work tab**: Corrected stale claim: integer revisions (e00c699), hard held/skip/drop admission with a bounded checkWaitSeconds deadline (18264e2), and paid-model gating are implemented. Remaining from the notes is Work-tab visibility of Ideas background-check waits, queue depth and drop reasons. The timing/revalidation correction is handled separately by idea-69ld2i.
- **idea-cozvvq — One effective-model resolver for turns, delegation and compaction**: Done on master: commit 1e70e6f, merge 8df87bb. Additive SessionModel.ResolveRefAsync resolves stored override → named-agent configuration → catalog default. Turns, implicit delegation and compaction use it; reasoning inheritance compares canonical effective model refs. Regression tests cover a named-agent parent with and without a global default, inherited reasoning and explicit overrides. All five unit suites and the full E2E gate passed (64/64, 823 checks) before merge.
- **idea-k9xztu — CI gates for unit, UI and end-to-end suites**: Done verified: .github/workflows/ci.yml now gates the five Windows unit suites, app/plugin UI builds, mock/UI tests, E2E and runner self-tests with failure artifacts. The launcher-handoff defect described in the notes has a source fix, not merely a passing retry. Remote GitHub execution status was not inspected in this local audit.
- **idea-pth2w0 — Measure and bound concurrent grep work; report searched files accurately**: Corrected scope: FileIndex per-root gates now have bounded ownership/retirement (7150def), and large-file Read paging stops after the requested page/lookahead or uses a bounded tail ring (0a05c7f). Remaining: each concurrent grep can use all processors and load large whole files; FilesSearched reflects candidates rather than necessarily files actually searched after an early cap. Measure concurrent grep allocations/latency before choosing a shared bound.
- **idea-f7o4yp — Share the provider Common source at compile time**: Still open. The five Common sources remain duplicated in the three provider projects; their csproj files do not link one shared source set. Compile-time sharing remains compatible with collectible plugin isolation and must not introduce plugin-to-plugin assembly references.
- **idea-2o4rmv — Benchmark and reduce long streaming Markdown reparses**: Corrected scope: CallsView polling is single-flight, and web_fetch coalesces in-flight requests with cache-age/refresh control (ce9de1a). The remaining candidate is AssistantText reparsing the accumulated live Markdown every 100 ms; measure long streamed output before changing it. A newly found late-poll-after-unmount race is filed separately with its exact trigger.
- **idea-t6odez — Forward message pagination for long chats**: Still open. The message RPC/store support beforeSeq paging; no forward afterSeq cursor was found. Keep explicit cursor semantics, ordering and concurrent append behavior in the eventual protocol/tests rather than increasing fixed caps.
- **idea-fe26oh — Plugin: language-server diagnostics and symbol navigation**: Still open. No language-server diagnostics/symbol-navigation plugin was found in the current plugin tree. Existing file search does not implement these semantic capabilities. Keep plugin isolation and workspace resolution as requirements.
- **idea-lvk9z0 — Plugin: PDF and Office text extraction**: Still open. No PDF/Office text-extraction plugin was found in the current plugin tree. Host tools can read ordinary text and images, but that is not document extraction. Implement as an optional plugin with bounded output and clear format failures.
- **idea-1y1174 — Plugin: scheduled runs**: Still open. No persisted scheduled-runs plugin was found. Goal continuation and runtime queueing are not a persisted calendar schedule; availability/budget checks and cancellation remain requirements.
- **idea-tc1vzd — MCP: read remote resources (resources/list + resources/read) instead of forcing the server's fallback tool**: Still open. McpConnection negotiates tools and handles tools/list_changed, but there is no resources/list/resources/read flow. A resources-only server is currently rejected by the required tools capability. Implement resource capability negotiation and reads as optional MCP features with size and URI handling.
- **idea-r7kxmg — Work tab: show a changed budget limit at once**: Still open. Work refreshes usage on usage.changed and its periodic refresh; changing budget settings does not immediately emit that usage refresh. Agents settings handling refreshes the scheduler but not the budget snapshot. Add a deterministic settings-change check and a live refresh event.
- **idea-7zha4j — Plugin independence: base features owned, enhancements optional, capabilities typed (review done, 6 steps to implement)**: Keep open. The review remains valid: fallback execution lacks parts of identity/prompt composition, Profile relies on context.reset, Goal has executor-dependent behavior, and the agent tool surface is not fully conditional on available capabilities. The six-step plan has not been implemented. SessionModel reduces one duplicated decision, but does not establish full plugin independence.
- **idea-8wvt4n — Investigate the E2E suite's transient failures: when a run goes red, the evidence does not say why**: Keep high/open. A clean 64/64 E2E run during the model-resolver gate does not diagnose the historical empty-evidence failures. This audit also met a unit-test race in ReportBeforeWait: with two slots, a slow child could occupy the spare slot while parent waited for quick before releasing slow. Fixed as 22db03d, merged by 2c11cf7, with explicit three-slot capacity, a held quick report and durable queued-report synchronization; the regression passed 20/20 fresh-process measurements and the full gate; this does not close the separate E2E observability/rate investigation.
