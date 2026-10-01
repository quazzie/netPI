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
- Backups capture the database and settings sequentially. Project files, global instruction files, skills, plugins and
  external credentials need separate backup; the ideas backlog is plugin-owned tables in that database, so it travels
  inside the database copy. Copies on the same disk do not protect against disk loss.
- Guardrails are pattern/path checks, not an OS sandbox, and the workspace guard is the same kind of check: it decides which
  paths the native tools may touch and where a shell command is started, not what a command does once it runs. Arbitrary
  code and trusted plugins retain user privileges.
- **Projects, workspaces and git worktrees** (the audit of 2026-09-30 no longer applies; verified 2026-10-01): a chat's working directory is a **workspace** — an actual checkout with a branch, a starting commit and an owner — bound per session (`sessions.workspace_id`, `workspaces` table, `sessions.setWorkspace`), while the **project** stays the shared identity (backlog, defaults, repository). The `netpi.workspaces` plugin owns it: it registers the one resolver every consumer asks (runtime, file tools, shell default cwd, file mentions, instruction and skill discovery, context notices, Files/Git views), provisions a worktree and branch for a writing worker (`agent_spawn` with `isolated`, decided by the setting when the caller does not say, and following the worker's tools: a reader shares its caller's workspace; the checkout is created at `<project>/.worktrees/<name>`, inside the project rather than beside it, and kept out of the parent's `git status` through a local `.git/info/exclude`), reuses a worker's own workspace for its next task, records the commit it started from, attaches an existing checkout, integrates branches one at a time per repository with an ancestry check, and retires only managed worktrees whose work is merged or durable and that nothing is running in. A bound workspace that is missing, gone or of another repository fails loudly; there is no fall back to the project checkout. A fork takes no workspace (a fork is a new writer). A commit is attributed to a project's ideas by repository evidence (`--git-common-dir`, `git -C` honoured), not by path containment. **What remains**: the guard is a *path* check — native write/edit tools and a shell call's `cwd` that resolve into another checkout of the same repository are refused (casing, `..`, junctions and symlinks resolved first), but a shell **command** is not parsed and a plugin can write anywhere, so this is not an OS sandbox (see the guardrails limit above). A workspace is only as isolated as its own branch: nothing merges automatically, and one integrator owns the integration branch.
- The Work tab shows a changed budget limit only on its next refresh (30 s, or when a call is recorded): the host publishes
  `usage.changed` after a recorded call, not when `budget.*` settings change.
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

## MCP implementation (2026-09-30)

All four delivery stages are implemented in the MCP task worktree: stdio/deferred dispatch, disclosure lifecycle and targeted notices, Streamable HTTP, and server-management UI. The solution and UI build pass, as do all MCP transport, runtime, lifecycle, mocked end-to-end and narrow-panel UI cases. The broader gate has one existing failure in the unchanged Chrome attachment test (`user.HasExited`); the agent regressions exposed during implementation were fixed. Native provider adapters, OAuth, prompts/resources and legacy HTTP+SSE remain follow-ups; see [PLUGIN-MCP.md](PLUGIN-MCP.md).
