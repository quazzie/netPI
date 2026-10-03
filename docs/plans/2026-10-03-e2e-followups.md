# E2E feedback: remaining performance work

Status: open follow-ups; no implementation in progress in this cleanup. Reviewed 2026-10-03.
The delivered runner and original evidence are [archived](../archive/2026-09-30-e2e-feedback.md).

## Remaining scope

- Split the broad browser walkthrough in `tests/NetPI.E2E/ui/smoke.mjs` into a small browser-to-backend round trip and independently selectable panel/control/backup checks. It still includes those flows and fixed waits.
- Replace positive-condition `waitForTimeout` pauses in `web/mock/e2e.mjs` and the browser smoke with observable state waits. Keep bounded waits that establish negative guarantees or settle screenshots.
- Measure slow overlap scenarios before adding per-request MockLlm hold/observe/release gates. The current `HoldMs` control is a fixed delay; preserve cancellation and transport assertions.
- Before moving detailed E2E cases into faster fixtures, record each assertion, its replacement and the retained boundary check in a coverage ledger. No persistence, reload or transport guarantee may silently disappear.

## Boundaries and verification

Selection, shards, failure evidence, incremental builds and repeat/fresh controls are already delivered. Budget-refresh and workspace-protection notes in the old report are historical, not work assigned here. CI activation or scheduling is excluded by the owner's decision to retain manual local checks.

Use existing timings to choose one slow case. Compare its focused before/after results, preserve its assertions, and exercise changed runner behavior with `-SelfTest`. Follow [TESTING.md](../TESTING.md) for scope: merging alone does not require a full gate. Do not run suites just to maintain this document.
