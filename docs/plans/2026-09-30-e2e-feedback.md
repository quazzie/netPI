# Faster E2E feedback: implementation plan for local agents

Status: proposed; implementation has not started. Date: 2026-09-30. Inspected master baseline: `19c463a`.

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
