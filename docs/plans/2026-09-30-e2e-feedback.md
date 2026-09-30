# Faster E2E feedback: implementation plan for local agents

Status: A and B delivered, C and D partly, on branch `claude/e2e-feedback` (one agent, sequentially; see "Results and amendments"
below). Date: 2026-09-30. Plan written against master `19c463a`; implemented on `66c5097`.

## Results and amendments (2026-09-30)

**The brief from the user:** agents spend 99 % of a task running E2E, then a small problem at the end (3+ minutes in) means a
small fix and another full run, again and again. "2 minutes 30000 times during development is still an eternity." Agents also
"retry" a known flaky test and tell their subagents to do the same.

**Measured baselines** (16 cores, quiet machine, master `66c5097`, no other E2E running):

| | before | after |
| --- | --- | --- |
| real-server E2E, whole suite | 126 s (64 tests; 124.7 s of test bodies, 1.3 s setup), serial | ~35 s, 4 shards |
| one test, server start included | ~a full run | ~2 s |
| smoke set (9 tests) | none | ~9 s |
| one plugin edited + its tests (`-Changed`) | a full run plus a build of everything | ~9 s (incremental build 1.7 s) |
| UI walkthrough (`npm run e2e`), whole | 144 s, **and it died at check 218 of ~300** on master | see the last bullet below |
| UI walkthrough, one section | the whole walkthrough | 8–20 s |
| unit-suite-style "is anything stale" check before a run | n/a | 0.2 s |

The docs said "61 tests, about three minutes": it is 64 tests and ~2 min serially on a quiet machine. The 3-4 minutes agents see
are that, plus a build of everything, plus the 144 s UI walkthrough, plus other agents' load. The point is not the 126 s → 35 s
(that helps the gate); it is that **the development loop no longer contains the suite**.

**What shipped** (commits on `claude/e2e-feedback`):

1. `Shell: a finished process lets go of its caller's callbacks`: a real retention bug found by the flake work (below), with a
   Tools-suite regression test that fails without the fix.
2. `E2E runner`: stable ids (`area.name`) and tags, selection that is decided before anything starts and rejects a typo (exit 2),
   in-process shards (a server + mock pair each, free ports, split by each test's last duration), a failure file per failed test,
   `report.json`, `timings.json`, `failing.json`, `--repeat`, `--fresh`, `--self-test`, containment of a timed-out test, run-specific
   screenshots, `areas.json` (changed files → tests).
3. `scripts/e2e.ps1`: `-Changed`, `-Only`, `-Tag`, `-Smoke`, `-Failed`, `-List`, `-Repeat`, `-Fresh`, `-SelfTest`; incremental builds
   (per-project stamps) into the dev output only.
4. `MockLlm hold=<ms>` and the UI smoke using it; the two hidden order/timing dependencies fixed in `AdvancedTests.cs`.
5. The UI walkthrough: sections that can be selected (`--only`, `--list`, `--with`, `--port`, `NEEDS`), `web/mock/sections.mjs` (every
   section alone), `mock.thinkDelay` re-landed.
6. `AGENTS.md`, `docs/TESTING.md`, `docs/UI.md`, CI `e2e` job.

**How this differs from the plan above**

- *In-process shards, parallel by default.* The plan kept real-server cases serial "initially". The runner isolates every shard
  completely (own server, mock, home, ports, work folder, screenshots), two full runs and several repeat/fresh stress runs found no
  cross-talk, and the gain is 3.6×. `--parallel auto` is the default (about one shard per 12 s of estimated work, at most
  `min(cores/2, 4)`); `--parallel 1` gives the old serial behaviour.
- *Containment by fresh server.* A timed-out body cannot be cancelled cooperatively (the bodies take no token), so its server is
  stopped and the rest of its shard gets a fresh one, instead of reporting the rest as not run. `--self-test` pins it and was
  mutation-checked.
- *Ids live next to the test* (`r.Add("<id>", "<name>", …)`); tags and smoke membership are in `Catalog.cs`. `--self-test` fails
  when they drift.
- *No C# migration and no new packages.* The MockLlm knob is `hold=<ms>` (first answer only), not a global gate; the plan's C4/C5
  (gates for overlap tests) is still open, see below.

**Ideas that were not in the plan** (all implemented): failure evidence files so a failure is diagnosed without another run; the
failing list across runs (`-Failed`); `-Repeat N -Fresh` to measure a flaky test and `-Fresh` to find order dependencies; `-Changed`
with `areas.json` (and a self-test that every plugin folder has a rule); per-project build stamps; one rerun hint in the caller's own
syntax; a UI section audit; the policy text in `AGENTS.md` ("never run the whole suite while you work", "never retry, measure").

**Flaky and order-dependent tests that were found, root-caused and fixed (none was retried):**

| test | cause | fix |
| --- | --- | --- |
| `reload.runtime-midrun` (deterministic after any bash call; passed only when `reload.all-plugins` had reloaded the shell plugin first) | **product bug**: a finished shell process stayed in the registry (last 50) holding the caller's live-output and exit callbacks, so the old agent-runtime load context was never collected | `ManagedProcess` drops them when done; Tools-suite test with a weak reference |
| `sessions.title-usage-projects` (fails alone) | asserted the server's running usage total (`calls > 10`) | asserts what its own run added |
| `subagents.wait-steer` (1 in ~60 under heavy load) | steered the moment the parent yielded and counted on the 2 s worker being recorded as done; a worker gives up its slot a few ms before it is recorded as finished | waits for worker-1 to be recorded, and holds the freed slot with a third chat so the parent still queues with priority (16/16 under the load that broke the old one) |
| `ui.smoke` "streaming rows shown" | looked at a state that exists ~100 ms | `hold=1500` in the scenario tag; the streaming state is now in `ui-02-streaming.png` every time |
| UI walkthrough "streaming thinking opens" (failed in 4 of 5 full runs on master, always at the same step) | **not** the short thinking window that AGENTS.md blames. The step pressed Ctrl+T and waited for `.intro`, which is already on screen when the tab you are on is an empty chat: the text was typed into the old tab's composer while the new session was still being created, and Enter then hit the new, empty one. The page sent `sessions.create` and `agents.use` and never `agent.send` (seen in the frames the page and the mock logged) | `newTab()` waits for the tab count to grow (10 call sites); `mock.thinkDelay` is re-landed too, unchanged, but it was not the cause |
| UI walkthrough "budget" check | read the Work tab's budget line before the tab's 30 s refresh | the section refreshes the tab like a user would, then waits for the text |

**How that one was found:** it never failed when its section ran alone, and a prefix run with the sections silenced passed too. So the walkthrough now saves `FAILED.png` and `FAILED.txt` when it dies (page text, toasts that showed, the frames the page sent, the frames and clients the mock saw; `mock.wsLog` is new), and two or three full runs at once reproduced it within minutes.

**Found but not fixed (product, for the owner to decide):** the Work tab does not show a changed budget limit until its next refresh
(30 s, or a recorded call): the real host only publishes `usage.changed` after a call is recorded. Also: a worker gives up its
slot before it is recorded as finished (harmless, but it is what made `wait-steer` racy).

**Still open**

- C: replace multi-second streams used only to create overlap (`slots.*`, `subagents.*`, `sessions.delete-orchestrator` 6.6 s,
  `compaction.*` 4-5 s) with MockLlm gates; the coverage ledger. Measure first: they are already parallel, the suite is bounded by
  `ui.smoke` (~26 s).
- Split `ui.smoke` into a small browser round trip (join `smoke`) and the panel/steer/abort/backup parts; trim its fixed sleeps.
- The UI walkthrough: a full end-to-end run on this machine, see the last bullet of the table's note; `NEEDS` is the honest list of
  what sections build on.
- D: the CI `e2e` job is written and unverified on a runner; a manually triggered full job and a scheduled run are not added.
- Projects and git worktrees: see the separate findings in the session report (edits can land in the project checkout when an agent
  is told to use another worktree).

## Outcome

Make focused development checks execute only the relevant work, retain a small real-app smoke suite, and keep full-system regression coverage available as an explicit broader gate. Preserve assertions and the guarantees they establish while moving detailed permutations to faster fixtures.

The first delivery is useful on its own: honest test selection, scoped commands, timing reports, and a small smoke manifest. A framework migration is not a prerequisite.

## Evidence and boundaries

- `tests/NetPI.E2E/Program.cs` accepts substring filters and `--no-ui`. `Harness.cs` executes selected cases sequentially against one shared `Env`.
- `Env.cs` starts MockLlm in-process and one separate server, normally from a private copy of `artifacts/dev/app` (with `artifacts/app` as a fallback). Tests share settings, model capacity, mock statistics and plugin state.
- `web/mock/e2e.mjs --only` gates reporting in `check()` and screenshots in `shot()`. The surrounding walkthrough still executes. Fixing this is the clearest immediate saving.
- `tests/NetPI.Agent.Tests/TestHost.cs` already starts real runtime/agents/context plugins with fake host services and controllable model replies. Scheduler/subagent tests already use completion gates. It is a starting point, not proof of real host persistence or transport correctness.
- Host tests already exercise real HTTP/WebSockets, SQLite, plugin load contexts and reloads. Reuse them where appropriate.
- `.github/workflows/ci.yml` currently runs the five .NET suites and the mock-backed UI walkthrough. It does not run the separate real-server E2E suite.
- Existing unit-speed work is on `codex/test-speed`, including `docs/TEST-SPEED-PLAN.md`. That branch reports shell-process fixes, isolated temp roots, combined builds, parallel suite processes and result reporting. Verify its ancestry and current implementation; do not assume those changes are on master or merge another agent's branch as part of this task.
- This plan is based on source inspection and saved unit logs. No fresh E2E benchmark was run. The documented roughly three-minute E2E duration is an estimate, not a measured baseline for this work.

## Ownership and order

Give each agent one worktree and one branch from current master. Follow both AGENTS.md files. Each agent takes one piece at a time, commits explicit paths, and has that piece merged before starting the next. One integrator coordinates merges and verifies ancestry; agents do not reset or move shared branch pointers.

| Package | Owner | Files primarily owned | Dependencies |
| --- | --- | --- | --- |
| A: baseline, selection and reports | Harness agent | `tests/NetPI.E2E/{Program,Harness,Env}.cs`, new E2E wrapper/manifest | First |
| B: truly scoped UI cases | UI agent | `web/mock/e2e.mjs` and new UI fixture/case modules | Can begin alongside A; agree CLI/report shape first |
| C: deterministic execution and coverage placement | Runtime-test agent | `tests/MockLlm/*`, owning .NET suite tests; selected E2E cases | Baseline and selection from A; take small migrations in sequence |
| D: policy, CI and final verification | Integrator | `.github/workflows/ci.yml`, `docs/TESTING.md`, relevant AGENTS.md test guidance | A and B merged; selected C changes merged |

`tests/NetPI.E2E/ui/smoke.mjs` is owned by B. Its C# registration is owned by A. Runtime-test changes to individual E2E cases must be coordinated with A. Do not have two agents edit the same file concurrently. If fewer agents are available, execute A through D sequentially in one agent's worktree.

## A. Make selection and cost visible

1. Check what unit-speed work is already merged, and reuse existing result conventions if available. Leave unrelated unit-runner or shell fixes with their owner.
2. Run one fresh full E2E baseline and one full mock UI baseline from isolated worktrees. Record build/setup, server startup, selected test bodies, browser startup, teardown and total caller wall time. Keep failure artifacts. Do not benchmark against the user's running instance.
3. Add stable case IDs and feature tags alongside readable names. Define a checked-in smoke membership list. Preserve existing substring filters and `--no-ui` compatibility. Keep no-argument behavior as full until the explicit commands and policy are documented.
4. Provide explicit modes: full backend, backend smoke, UI smoke, feature-selected checks, and list-only. Resolve selections before launching servers or browsers. A typo or empty selection must exit nonzero with an explanation. List-only must not launch anything.
5. Add a PowerShell entry point under `scripts/` for E2E, reusing the private build-copy behavior. It must build only the necessary graph into dev output, support skipping a known-current build, propagate failures, and print an exact rerun command. Avoid changing `scripts/test.ps1` until its owner's work is reconciled.
6. Save a small JSON report plus readable output: mode, selected/executed IDs, count, pass/fail, test durations, phase durations, exit code, and artifact locations. Include setup/cleanup in total wall time. Selected-case counts must be trustworthy.
7. Keep the real-server cases serial initially. Audit setup dependencies: a selected case must work alone even if startup, agent, model or profile cases were not selected.
8. Address runner containment: the existing outer timeout races a body against a delay without cancelling the body. A timed-out body must not continue mutating the shared fixture while the next case starts. Use cooperative cancellation with bounded cleanup where possible; otherwise stop that fixture/run, retain artifacts, and report remaining cases as not run.
9. Use unique homes, temp roots, browser profiles and available ports for each invocation. Make log/report/screenshot paths run-specific. Never clean a shared parent directory. Do not offer the installed app as a convenience shortcut for mutating reload tests.

Acceptance:

- Each selected case works alone; unmatched selection fails before expensive setup.
- Full remains a coverage superset of smoke; listing makes that relationship inspectable.
- Reports distinguish test failures, setup failures, abnormal process exits and cases not run.
- A runner regression proves a timed-out body cannot overlap the next case's fixture mutations.
- Two focused invocations with independent fixtures do not share ports, screenshots or cleanup roots.
- Record baseline and after timings with the exact commands/build revision; do not infer wall time from body timers alone.

## B. Turn the UI walkthrough into independently executable cases

1. Inventory each walkthrough section and its prerequisites: seeded session, project, settings, panel, message history and browser storage. Build an explicit ID-to-case table before splitting code.
2. Extract named async case functions/modules. Select functions before invoking them. `--only` must skip unrelated actions and setup, not just suppress their assertions. Keep compatibility for existing section names through an explicit mapping and reject unknown names.
3. Give cases self-contained fixture setup. Seed the necessary mock state directly rather than clicking through preceding walkthrough sections. Preserve coverage of the setup UI itself in its own case. Fixtures must reflect the current RPC/event contract.
4. Split the real-server browser smoke into a minimal browser-to-backend round-trip and separately selectable panel/settings/MCP/control cases. Keep one check for rendering streamed text, tool result/diff, final persisted answer, and console/network failures on the critical path. Broad panel CRUD need not run for every backend edit.
5. Replace positive-condition fixed sleeps with observable DOM/event assertions. Preserve bounded observation windows for negative guarantees such as an action not occurring. Keep screenshot settling separate from behavioral correctness.
6. Retain full coverage and run-specific screenshots. Report which functions actually executed and what fixture setup ran. Verify a selected case cannot silently invoke an unrelated case.
7. Start with the existing `playwright-core` helper. Optional follow-up: evaluate `@playwright/test` after scoped cases work. It can improve fixtures, assertions and reporting, but is not required for this delivery. Do not migrate the .NET runners or add NuGet dependencies.
8. Keep browser cases serial until mock resets, server lifecycle, database state and output paths are independent. Browser contexts alone do not isolate the backend.

Acceptance:

- A late-section selection executes without all preceding UI actions.
- A fixture/selection regression records executed IDs and proves unrelated actions were skipped.
- A deliberately broken selected assertion produces a failing exit; a typo cannot produce a green empty run.
- The full walkthrough's assertion inventory is accounted for after the split.
- A focused run is measurably cheaper in executed actions and wall time than full.

## C. Reduce timing dependence and move detailed cases carefully

Work in small commits, starting with one expensive group identified by A's timings. Do not attempt a wholesale rewrite.

1. Build a coverage ledger: old E2E ID/assertions, guarantee, destination fixture, retained boundary smoke, replacement test IDs and measured cost. Mark each move as planned, validated or removed-from-E2E.
2. Prefer the existing Agent harness for scheduling, steer/queue/abort ordering, hook decisions and budget permutations when their assertions do not require host transport or disk persistence. Keep representative real-provider/transport paths at the boundary.
3. Use real host storage for guarantees about transactions, WAL, persistence, restart, plugin unloading and shared registrations. Agent TestHost's fake session store/NullDatabase cannot replace those tests.
4. Add narrowly scoped test controls to MockLlm: hold a specific invocation after a known stream point, observe that it reached the gate, and release/cancel it explicitly. Scope control state to the run/session/request; avoid a global gate that blocks unrelated scenarios. Add bounded failure cleanup and tests for cancellation/release.
5. Replace multi-second streams used only to create overlap with gates: hold worker A, observe worker B queued or parent yielded, assert the state, then release. Keep SSE chunking and provider request validation real in transport integration checks.
6. Do not globally raise `--speed`: it scales deliberate slow scenarios and may remove the window a steering/abort test needs. Make ordinary non-timing scenarios fast through an explicit setting while controlled scenarios remain observable.
7. For genuinely timer-driven retry/stall/queue expiry logic, consider a plugin-local injectable `TimeProvider` or delay seam, retaining the production default. Do this only when measured savings justify it. Preserve real cancellation, backoff and negative-observation guarantees; keep at least a narrow real-clock integration check. No shared-contract change unless independently necessary and justified.
8. Validate the new test against a meaningful failure or controlled mutation where practical. Remove an old detailed E2E case only after its replacement preserves its assertions and the coverage ledger records the retained boundary check. Keep cases whose end-to-end guarantee cannot be reproduced below that layer.

Acceptance:

- Controlled concurrency tests establish the relevant state before triggering the action, rather than relying on elapsed sleep.
- Cancellation releases held mock calls and leaves no children or requests running.
- No persistence/unload guarantee is downgraded to an in-memory fake.
- The ledger accounts for every moved/deleted assertion and explains remaining E2E-only checks.
- Report actual savings for the selected group; preserve full-suite health.

## Smoke membership and validation policy

Choose exact IDs after inventory. Aim for a small set of representative workflows rather than every permutation:

| Boundary | Retained checks |
| --- | --- |
| Built app startup | Expected plugins and required tools registered; assets reachable |
| Chat/tool round-trip | Mock provider streams; a real file tool runs; events and persisted result agree |
| Provider adapters | Representative Responses, Chat and Anthropic continuation/replay behavior |
| Control/scheduling | One deterministic cancellation/steering path and one yield/queue/worker-report path |
| Storage/lifecycle | Persistence across restart and representative plugin/provider reload while active |
| Browser wiring | Send, streamed/tool rendering, final answer, console/network health |

A may keep restart/unload/browser cases in a separate lifecycle smoke group if their measured cost would dominate the everyday smoke. Their coverage remains explicit and required for lifecycle changes and broad validation. Document the distinction instead of silently omitting them.

Targets are goals to validate on the same machine: focused backend checks usually complete in a few seconds after build; warm backend smoke ideally under 30 seconds excluding build. Measure the minimal UI smoke before setting its budget. Do not cut required boundaries or shorten safety timeouts merely to meet a target.

Proposed routine:

- During edits: owning suite filters or feature-selected UI/backend cases.
- Before merging each implementation piece: one full five-suite run as the current AGENTS.md requires, plus the affected focused E2E/UI checks. While fixing failures, rerun only those failures.
- Cross-plugin/provider/runtime/transport work: relevant real-server smoke and integration cases before merge.
- Full real-server E2E: baseline once, after the coordinated harness changes, before releases, and whenever coverage changes or failures leave a system-level concern unresolved.
- CI: retain the existing five-suite and full mock UI gates. Add real-server backend smoke on pull requests/pushes after its fixtures and runtime are reliable. Keep a manually triggerable full real-server job; propose a scheduled run only after its runtime/reliability are measured.

These are proposed guidance changes for D to land. Existing AGENTS.md rules continue to apply until updated.

## D. Integrate, document and verify

1. Merge completed pieces in order, checking branch ancestry and preserving concurrent work. Resolve contract decisions before merging overlapping files.
2. Update `docs/TESTING.md` with exact scoped/full/list commands, selection semantics, build freshness, artifacts, smoke membership and real-clock/real-storage boundaries. Fix stale `artifacts/app` descriptions to match the actual private dev-build source.
3. Update test guidance in AGENTS.md to describe the intended inner loop and when full validation is required. Keep the owning-suite regression requirement, safe build output and worktree rules.
4. Implement CI jobs using isolated dev builds and no publishing. Check that the full real-server job builds all bundled plugins, UI assets, MockLlm and the E2E runner; installing only the five unit projects does not establish a complete app fixture. If real browser coverage is included, install its Node dependencies and use a documented browser.
5. Validate the integrated commands: list-only, smoke, one backend feature, one late UI case, invalid/empty selection, deliberate failure, timeout containment, and two isolated invocations. Repeat only checks affected by a subsequent correction.
6. Run final broad validation once against the integrated revision, including full backend E2E and full mock UI; run the affected real-server browser cases. Keep failures and diagnose narrowly. Record commands, selected counts, phase timings, environment, baseline comparison and any unverified platform.
7. Compare the assertion/coverage ledger with the original suite. Report any remaining cases that require full E2E and the reason.

## Handoff prompt for each local agent

Read both AGENTS.md files and this plan. Check current master and the existing `codex/test-speed` work before editing. Take only your assigned package, in your own worktree/branch. Coordinate file ownership and the case-ID/report contract with the integrator. Preserve correctness assertions and use isolated dev output; do not publish or attach to the user's app. Complete one reviewable piece and its owning-suite regression tests, rerunning only failures while iterating. Report your commit, exact validation commands/results, measured timing changes, files owned and dependencies for the next package. The integrator owns merges, policy edits and final broad validation.
