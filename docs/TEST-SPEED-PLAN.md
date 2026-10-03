# Test speed investigation and change plan

Date: 2026-09-30. Baseline: commit `2acb758`. Investigation branch: `codex/test-speed` in `C:/AI/Projects/NetPI-test-speed`.

The largest avoidable slowdown is surviving shell children, not assertion execution. The Tools runner can finish its tests and exit while its caller remains blocked waiting for inherited output pipes. Process termination tests currently pass without proving that the Windows child processes died. Fix that before adding concurrency or shortening timeout tests.

This document proposes changes; application and test source changes used for profiling were restored. Builds and tests used this worktree's `artifacts/dev/app`; no publishing occurred.

## Measurements

One fresh full unit run, sequential builds and suite processes, with output captured as the normal script does:

| Suite | Build | Reported test bodies | Process invocation including output capture | Result |
| --- | ---: | ---: | ---: | --- |
| Providers | 4.40 s | 5.17 s | 6.57 s | 41 pass |
| Tools | 2.95 s | 22.35 s | 64.85 s | 59 pass |
| Agent | 4.03 s | 12.90 s | 13.06 s | 130 pass |
| Aux | 5.05 s | 26.26 s | 26.99 s | 204 pass, 1 fail |
| Host | 2.50 s | 20.48 s | 20.67 s | 63 pass |
| Total | 18.93 s | 87.16 s | 132.15 s | 497 pass, 1 fail |

Build plus execution took **151.08 s**. Tools alone accounts for **42.50 s** outside its test timers. The saved passing run in the main checkout (`artifacts/testlogs/2026-09-30-101218.txt`) reported about 82 s of test bodies. Those logs omit build time and do not persist suite wall time, so they cannot establish total elapsed time.

Additional build-only comparison, after warming the same worktree:

- Five separate project builds: **9.00 s**.
- One temporary solution containing the same five test projects and their dependency graph: **5.28 s**.
- This is one measurement of each, not a repeated benchmark; projected savings need checking after implementation.

Profiling outputs remain under `artifacts/test-speed/`: `phases.json`, `tests.json`, per-suite build/run logs, `warm-builds.json`, `graph-build.json`, and the focused probes below.

## Confirmed causes

### 1. Shell descendants outlive passing tests and retain captured output

A focused orphan test run measured actual process exit separately from output EOF:

- `dotnet` exited at **1.13 s**.
- Captured stdout closed at **20.27 s**.
- The test itself reported **0.915 s**; explicit cleanup took **0.123 s**.

A focused timeout test, temporarily changing its two 60-second sleeps to six seconds, reported PASS in **2.18 s**. `dotnet` exited at **2.39 s**, but stdout remained open until **6.27 s**. The test therefore failed to detect a surviving descendant. All source was restored afterward.

Relevant code:

- `tests/NetPI.Tools.Tests/ShellTests.cs:18`: `ProcessAlive` checks `Process.GetProcessById` on Windows.
- `ShellTests.cs:138`: the timeout test writes Bash `$!` to `child.pid`; the assertion interprets that MSYS PID as a Windows PID.
- `ShellTests.cs:158`: abort uses the same PID assumption.
- `ShellTests.cs:184`: orphan cleanup uses the same assumption and swallows failure.
- `plugins/NetPI.Tools.Shell/Processes.cs:185`: termination relies on `.Kill(entireProcessTree: true)`; passing assertions currently cannot establish that it reaches every MSYS descendant.

The evidence proves surviving children/output handles. It does not yet identify the exact MSYS/.NET handle inheritance or reparenting mechanism. The normal 60-second sleeper is consistent with the full Tools invocation taking 64.85 s. Directory deletion was an initial hypothesis; the instrumented probe disproved it as the explanation for the reproduced delay.

### 2. All execution is serialized; the build graph is entered repeatedly

`scripts/test.ps1:38` invokes `dotnet build` separately for each suite, including overlapping dependencies and restore/evaluation work. Line 51 starts each suite only after the previous invocation and output collection finish. Every console runner also executes its selected tests sequentially.

`build.ps1 -Test` already uses one solution build, so the repeated-build finding specifically applies to `scripts/test.ps1`. The broader build script deletes the dev output before building, which also makes it a poor incremental inner-loop path.

### 3. Several so-called unit tests run expensive real integrations

The slowest fresh test groups are:

| Group | Measured bodies | Reason |
| --- | ---: | --- |
| Aux browser + screenshot | 14.31 s | Multiple real browser launches, many actions, rendering, teardown |
| Host plugin tests | 9.39 s | First test builds three sample variants; live hosts, watchers and unloading |
| Tools Bash/background/process waits | 16.04 s | Shell startup, real one-second timeouts, startup grace, output draining |
| Host build tests | 3.93 s | Four separate `dotnet msbuild` property evaluations plus PowerShell parsing |

`BrowserTab.cs:196` and `:203` add 150 ms and 250 ms for most actions other than open/snapshot. They are production settling waits exercised repeatedly by the browser test, not solely test sleeps. `ShellService.BackgroundStartupWait` defaults to 500 ms; `ManagedProcess.DrainGrace` defaults to 750 ms. Reducing these globally would change product behavior and needs separate correctness coverage.

Other intentional real-time checks include scheduler queue expiry (~2.08 s), Ideas slot expiry (~2.07 s), two provider connection failures (~2.05 s each), and consumer/stall timing (~1.84 s). These are concentrated costs; hundreds of storage, protocol and transformation assertions already run in milliseconds.

### 4. Isolation and runner reporting need repair before concurrency

Tools, Aux and Agent delete shared temporary roots at exit. Tools/Aux create test folders directly below those roots. Two invocations of the same suite can delete each other's artifacts even from separate Git worktrees. Host already uses a process-specific root.

Providers also shares mutable mock state and catalog setup between tests; some filtered tests depend on setup performed by an earlier selected test. E2E tests share one server, settings and mock state. Parallelizing individual tests indiscriminately would introduce races.

`scripts/test.ps1:56` records a suite exit code but does not use it to determine success. It can report all green for a crashed process with no recognizable FAIL line. Provider output formatting also differs from the other runners. The generated rerun command emits repeated `-Only` arguments for multiple failures instead of one array argument. These are adjacent correctness problems to address when editing the runner.

## Implementation order

1. **Make shell lifetime checks truthful, and eliminate surviving test children.** Add a Windows-aware mapping from MSYS IDs to native process identity, or launch test children through a helper that reports native IDs. Verify identity before checking or terminating a process. Give each shell test an owner that cleans its processes in `finally`, including after a failed assertion. The orphan test must retain its assertion that foreground output returns promptly, then terminate its deliberately detached child using the correct identity. Strengthen timeout/abort tests to prove both the foreground and background child ended, with event/marker evidence and captured-output EOF. Investigate and fix production termination for the demonstrated MSYS case; a Windows Job Object or native descendant tracking may be appropriate, but preserve normal foreground/background semantics and confine termination to the launched command. Do not merely reduce the 60-second sleep or ignore output EOF. Acceptance: no sleepers survive; process exit and output completion are close; the timeout regression fails against the old behavior.

2. **Add trustworthy measurements and invocation isolation.** Save restore/build, suite startup, test-body, cleanup, process-exit and output-drain timing, plus selected counts and exit codes. Emit a machine-readable result with a common shape while keeping readable console output. Give Tools/Aux/Agent a run-specific temp root and delete only that root. Preserve and restore the caller's `NETPI_APP_DIR` in `finally`. Fix crash detection and rerun argument generation. Acceptance: no-match selections are explicit; abnormal exit cannot appear green; two instances of the same filtered suite do not interfere; overhead is visible in logs.

3. **Build the selected test graph once.** Use a small test solution/filter or generated solution containing only requested suites and project references, preserving `-Suite`, `-Config`, and safe dev output. Restore once and build once; do not launch independent concurrent builds that write the same dependency outputs. Make sample variants an incremental build fixture with distinct variant intermediates/outputs and freshness checks, rather than three unconditional build invocations inside the first plugin test. Keep build-contract checks real; batch MSBuild property queries in one driver where practical. Acceptance: a source change rebuilds all necessary dependencies/variants, a warm filtered run does not rebuild unrelated projects, and artifacts/app remains untouched.

4. **Add bounded parallel suite processes.** After steps 1-2, allow two or three suite processes after the single build. Keep tests within each suite sequential initially; keep E2E sequential. Collect independent logs/results and merge them in a stable order. Run longer suites first and retain a serial diagnostic option. Verify browser profile ownership and other shared external resources before enabling concurrency by default. Based on current body timings, three workers have an ideal scheduling floor near 33 s before build/startup/contention. Treat a warm full-unit target of roughly 40-55 s as a benchmark goal, not a promised speedup.

5. **Replace unnecessary waits and separate fast/integration selection.** Replace positive-condition sleeps with completion signals, event waits or readiness predicates. For scheduler/retry/Ideas timeout logic, prefer an internal controllable clock where justified; retain a few real-clock integration tests and preserve negative observation windows. Audit the browser's fixed 400 ms settling path separately: use load/effect/stability detection with a bounded fallback and regressions for delayed DOM changes, navigation and selects. Tag real browser/process/build integrations for explicit fast selection, while the default full validation still runs all coverage. Do not weaken SQLite/WAL/transaction/unload tests or substitute a fake database for their guarantees.

## E2E follow-up

E2E and UI smoke were reviewed statically, not executed or benchmarked in this investigation. They are not included in the 151-second measurement.

`tests/NetPI.E2E/ui/smoke.mjs` contains fourteen fixed wait sites (300, 200, 800, 200, 300, 900, 900, 400, 700, 700, 500, 500, 500, 500 ms): 7.4 s if each executes once, with some sites in repeated flows. Replace them with DOM/event assertions. Mock streams add per-chunk delays; `--speed` scales those delays, including deliberately slow scenarios. Raising it blindly could invalidate steering/abort/concurrency observations. Add deterministic scenario gates or a fast mode limited to scenarios whose timing is irrelevant. Leave stateful E2E cases serial unless each worker receives its own server, ports, home and mock.

## Existing failure and validation boundaries

The Aux test `browser: the user's Chrome over its DevTools port: own background tabs, checkout refused, leave hands the tab back, never closed` failed both in the full run and when rerun alone, at `user.HasExited`. The saved baseline passed. Investigate whether the tracked launcher exits after handoff or the actual browser closes, and confirm its profile/DevTools ownership. This is a repeatable issue here; its exact cause is unproven. Do not resolve it with a longer sleep or by weakening the ownership assertion.

No production optimizations have been applied. Temporary diagnostic source edits were restored. The full suite was run once; subsequent executions targeted only the failure or unresolved shell lifetime probes. Future implementation should use that same loop: one baseline, targeted regressions while editing, then one complete validation before merge. Include serial and concurrent invocations, check captured-output EOF and leftover native children, and record median/range of warm phase timings before claiming a speedup.

---

# Outcome (steps 1-4 applied; step 5 deliberately not applied)

## What the extra 42.5 s actually was

Not the assertions, and not directory deletion. It was **native Windows processes that outlived their command and
held the output pipe open**. The Tools runner finished and printed its summary at 22.3 s, and the *invocation* did
not return until 64.2 s, because `scripts/test.ps1` captures the runner's stdout and the last writer of that pipe
was a `sleep` that was still running.

Two independent defects produced it:

1. **Production.** `ManagedProcess.Kill` used `Process.Kill(entireProcessTree: true)`, which walks the Windows
   parent-pid chain. The MSYS runtime does not keep that chain intact: a background `sleep 60 &` is re-parented and
   never appears as a descendant, so it survived a "tree" kill. Measured directly — of a heartbeat subshell, a bare
   loop and two plain `sleep`s, `Kill(entireProcessTree)` killed the subshells and left **3 `sleep.exe` running**.
   Fixed with a Windows job object per launched command (`plugins/NetPI.Tools.Shell/ProcessTree.cs`): it kills by
   membership, not parentage. The job is deliberately *not* kill-on-close, so a command that exits and leaves a
   daemon behind still works.
2. **The tests could not see it.** `ShellTests.ProcessAlive` was handed `bash $!`, which is an **MSYS** pid.
   Measured: MSYS `3031` vs native `14944`, and `Process.GetProcessById(3031)` *throws* — which the helper's
   `catch { return false; }` turned into "dead". So the assertion passed unconditionally while the child lived.
   The tests now assert **captured-output EOF** (`details.outputEof`), which is namespace-independent and is the
   thing a caller actually waits on. The deliberately-orphaned test still needs a pid, so it resolves the real
   Windows pid through `ps -W` and cleans up in `finally`.

Verified in both directions: with the job object disabled the two tests **fail**; with it enabled they pass, and
the focused runs go from 60.3 s / 30.2 s to 1.5 s / 1.0 s with **zero** leftover `sleep.exe`.

## Measured result (three consecutive full runs, all green)

| | before | after |
| --- | ---: | ---: |
| full unit run, serial, no build | 132.2 s | 83.0 s |
| full unit run, 3 workers, incl. build | 151.1 s | 38.8 / 40.6 / 39.5 s (median **39.5 s**) |
| build of the five suites | 18.9 s (5 separate builds) | 3.4-4.5 s (one generated solution) |
| Tools, time outside its own test timers | **+42.5 s** | **+0.5 s** |
| leftover `sleep.exe` after a run | 3+ | 0 |

The plan's benchmark goal of a warm full-unit run in 40-55 s is met, and it is met *without* touching the browser
settling waits, the shell timeouts, or any assertion.

## Also fixed while in the runner

- One build of the selected graph (generated `.slnx` under `artifacts/test-speed`) instead of five.
- Bounded parallel suite processes (`-Parallel`, default 2; `-Serial` to force one), longest suite first.
- Crash detection: a non-zero exit with no FAIL line is reported as a crash, not green. A filter that matched
  nothing is reported too (the runners now say so themselves).
- The re-run command emits one `-Only` with an array, not repeated `-Only` flags.
- Per-run `NETPI_TEST_ROOT` per suite, and the caller's `NETPI_APP_DIR` is restored in `finally`. The root is under
  the system temp, **not** the repo: the git tests create real repositories, and one created inside this repo is a
  nested repo with different behaviour (caught by running it).
- Timings to `artifacts/testlogs/<timestamp>.json`, and each suite's process time printed next to its own reported
  test time, so this class of stall is visible in the future instead of hiding in a total.

## Not done, and why

- **Step 5 (replacing waits, fast/integration split)** is untouched. It is a behaviour change to production
  settling waits (`BrowserTab.cs:196/:203`, `BackgroundStartupWait`, `DrainGrace`) that needs its own correctness
  coverage, and it is worth little next to what was fixed: Aux is now the longest suite at 27.6 s and most of that
  is real browser launches.
- **E2E** was not re-run. It needs a published app and Playwright. The contract change is additive
  (`details.outputEof` plus one appended note line, and every E2E assertion on this output is a `Check.Contains`),
  but that is reasoning, not a green run — it should be run before merge.
- The Aux test that failed in the baseline (`browser: … checkout refused, leave hands the tab back`) now passes.
  The likely reason is that the stray `bash`/`sleep` processes the old code leaked were interfering; the exact
  cause is still unproven.
