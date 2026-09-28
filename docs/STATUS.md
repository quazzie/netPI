# Status (2026-09-28)

## Current review fixes

- Guard approvals have unique host-generated approval IDs; provider tool-call IDs can repeat across sessions.
  The UI matches approvals to both session and tool call. Forks discard session-only guard approvals.
- Spending is read atomically from the persistent ledger. Every paid attempt reserves estimated input plus its
  output allowance before dispatch, sharing caps across concurrent calls and plugin reloads. Retry attempts are
  separate rows. Interrupted calls and crash-left reservations remain visible and count against caps. Unpriced
  cloud calls require configured prices under a dollar cap, or an explicit per-chat budget override.
- `netpi.backup`: automatic and manual database/settings snapshots with checksums, automatic-only retention,
  Settings → Data & backups, and an offline restore command that refuses existing homes. See [BACKUPS.md](BACKUPS.md).

## Validation

Windows validation on 2026-09-28:

| Check | Result |
|---|---|
| Full .NET solution build | Passed, 0 warnings/errors |
| App and plugin UI builds | Passed (existing Ideas-tab Svelte warnings) |
| Providers | 41 passed; 335 assertions |
| Tools | 55 passed |
| Agent | 122 passed |
| Aux | 129 passed |
| Host | 47 passed; updated backup regression also rerun independently |
| UI mock walkthrough | 233/233 checks passed |
| Full real-server E2E | 61 passed; 800 checks, including creating/verifying a backup in the browser |

Coverage details are in [TESTING.md](TESTING.md). Builds and tests use a separate `artifacts/review/app`
output; these changes have not been installed into the user's running app by this task.

## Remaining limits

- Reservations estimate provider billing; they are not a guaranteed dollar ceiling. Missing final bills retain a
  conservative estimate. The UI identifies those amounts; there is no automatic provider-invoice reconciliation.
- Backups capture the database and settings sequentially. Project files, global ideas/instructions, skills,
  plugins and external credentials need separate backup. Copies on the same disk do not protect against disk loss.
- Guardrails are pattern/path checks, not an OS sandbox. Arbitrary code and trusted plugins retain user privileges.
- Reloading the agents plugin can temporarily exceed configured execution slots for already-running agents;
  persistent budget reservations are still shared across the old and new plugin instances.
- Linux/macOS and paid/live-provider billing behavior were not validated in this change. The Anthropic provider
  still needs verification against the real API. No checked-in CI workflow enforces the test suites yet.
- Historical model experiments and earlier verification claims are preserved in
  [the archived status](archive/2026-09-25-status.md); they are not fresh release verification.

## Useful next capabilities

Language-server diagnostics, PDF/Office reading and scheduled runs remain backlog items. MCP, partial rollback
and worktree automation were deliberately deferred or dropped in the earlier harness plan; they are not accidental
omissions. See [the archived harness decisions](archive/2026-09-26-harness-gaps.md).
