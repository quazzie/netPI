# E2E feedback: remaining performance work

Status: open follow-ups; no implementation in progress in this cleanup. Reviewed 2026-10-03.
The delivered runner and original evidence are [archived](../archive/2026-09-30-e2e-feedback.md).

## Remaining scope

- Split the broad browser walkthrough in `tests/NetPI.E2E/ui/smoke.mjs` into a small browser-to-backend round trip and independently selectable panel/control/backup checks. It still includes those flows and fixed waits.
- Replace positive-condition `waitForTimeout` pauses in the browser smoke with observable state waits (the other scripts in `tests/NetPI.E2E/ui` wait on gates and elements: [how the mock suite moved there](../archive/2026-10-11-retire-mock-ui-suite.md)). Keep bounded waits that establish negative guarantees or settle screenshots.
- Measure slow overlap scenarios before adding per-request MockLlm hold/observe/release gates. The current `HoldMs` control is a fixed delay; preserve cancellation and transport assertions. MockLlm's gates (`gate=`, `/_release`, docs/TESTING.md) came with the mock UI suite's move: [H3 there](../archive/2026-10-11-retire-mock-ui-suite.md#step-1-the-harness-before-any-port-in-this-order).
- Before moving detailed E2E cases into faster fixtures, record each assertion, its replacement and the retained boundary check in a coverage ledger. No persistence, reload or transport guarantee may silently disappear.

## Boundaries and verification

Selection, shards, failure evidence, incremental builds and repeat/fresh controls are already delivered. Budget-refresh and workspace-protection notes in the old report are historical, not work assigned here. CI (`.github/workflows/ci.yml`) runs the suites on every push; nothing here changes when it runs.

Use existing timings to choose one slow case. Compare its focused before/after results, preserve its assertions, and exercise changed runner behavior with `-SelfTest`. Follow [TESTING.md](../TESTING.md) for scope: merging alone does not require a full gate. Do not run suites just to maintain this document.
