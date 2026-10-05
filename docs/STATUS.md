# Status (2026-10-03)

## Current review fixes

- **Storage is a port, and the core is only the harness.** `src/NetPI.Abstractions/StoragePort.cs` is the whole
  contract: an `IStorageProvider` chosen by `storage.provider` at startup (default `sqlite`; `--ephemeral` on
  `netpi-server` is the `memory` provider) and the `IStorage` it opens — sessions/messages/projects, the core's
  key-value store, each plugin's own data, one snapshot, one re-entrant lock. The two built-in providers live in
  `src/NetPI.Host/Storage`, and only the `sqlite` one writes SQL. Plugins keep their data through `ctx.Data` (named
  collections of JSON documents with declared index fields, a per-plugin transaction), a session carries what a
  plugin attaches to its `meta`, and a new provider has to pass `tests/NetPI.Storage.Tests`. The wire protocol is
  unchanged apart from the session's lost top-level `workspaceId` (now `meta.workspaceId`), and
  `node scripts/core-size.mjs` checks that the kernel names none of the higher abstractions.
- Guard approvals have unique host-generated approval IDs; provider tool-call IDs can repeat across sessions.
  The UI matches approvals to both session and tool call. Forks discard session-only guard approvals.
- Spending is read atomically from the persistent ledger. Every paid attempt reserves estimated input plus its
  output allowance before dispatch, sharing caps across concurrent calls and plugin reloads. Retry attempts are
  separate rows. Interrupted calls and crash-left reservations remain visible and count against caps. Unpriced
  cloud calls require configured prices under a dollar cap, or an explicit per-chat budget override.
- `netpi.backup`: automatic and manual database/settings snapshots with checksums, automatic-only retention,
  Settings → Data & backups, and an offline restore command that refuses existing homes. See [BACKUPS.md](BACKUPS.md).

## Validation

Windows validation on 2026-10-03: the six unit suites (`scripts/test.ps1`) from fresh Release builds in this repository
were green (Providers 68 passed, 495 checks; Tools 79; Agent 195; Aux 335; Host 170; Storage 104).

The repository is on GitHub (`quazzie/netPI`) and `.github/workflows/ci.yml` runs on every push, on `windows-latest`:
the six unit suites, the mock UI suite and the real-server e2e suite (the Linux build and the suites on Linux are not
run there yet). **The runs on `master` of 2026-10-05 were not green**; the Windows-runner failures are under
investigation and this page says so until they are fixed:

| Job | Result on 2026-10-05 |
|---|---|
| Unit suites | Aux 391/395 (four `workspaces:` tests); Host 205/206 (`server: /api/health…` pinned the version `0.1.0`; the test now reads the host's own version) |
| E2E (real server, mock model) | 63/66: `context.project-switch`, `context.workspace-binding`, `sessions.title-usage-projects` |
| Mock UI suite | 416/417 once (a harness race, being fixed) |

Coverage and verification scope are in [TESTING.md](TESTING.md). Small changes use focused checks; full gates
are for shared contracts or cross-cutting behavior.

## Remaining limits

- Reservations estimate provider billing; they are not a guaranteed dollar ceiling. Missing final bills retain a
  conservative estimate. The UI identifies those amounts; there is no automatic provider-invoice reconciliation.
- Backups capture the provider's files and the settings sequentially. Project files, global instruction files, skills, plugins and
  external credentials need separate backup; the ideas backlog is a plugin-owned collection in the store, so it travels
  inside the provider's files. Copies on the same disk do not protect against disk loss.
- Guardrails are pattern/path checks, not an OS sandbox, and the workspace guard is the same kind of check: it decides which
  paths the native tools may touch and where a shell command is started, not what a command does once it runs. Arbitrary
  code and trusted plugins retain user privileges.
- **Projects, workspaces and git worktrees** (the audit of 2026-09-30 no longer applies; verified 2026-10-01): a chat's working directory is a **workspace** — an actual checkout with a branch, a starting commit and an owner — bound per session, while the **project** stays the shared identity (backlog, defaults, repository). The `netpi.workspaces` plugin owns all of it: it keeps the workspaces as its own collection in the store's plugin data, registers `workspaces.*`, `sessions.setWorkspace` and the `workspace.*` / `session.workspace` events, and writes the binding onto the session as `meta.workspaceId` with `meta.cwd` (the core's `GetCwd` is `meta.cwd`, else the project folder, else `workspace.default`; a session has no workspace column). It registers the one resolver every consumer asks (runtime, file tools, shell default cwd, file mentions, instruction and skill discovery, context notices, Files/Git views), provisions a worktree and branch for a writing worker (`agent_spawn` with `isolated`, decided by the setting when the caller does not say, and following the worker's tools: a reader shares its caller's workspace; the checkout is created at `<project>/.worktrees/<name>`, inside the project rather than beside it, and kept out of the parent's `git status` (this repository lists `/.worktrees/` in `.gitignore`; for a project it does not, the plugin writes a local `.git/info/exclude` line)), reuses a worker's own workspace for its next task, records the commit it started from, attaches an existing checkout, integrates branches one at a time per repository with an ancestry check, and retires only managed worktrees whose work is merged or durable and that nothing is running in. A bound workspace that is missing, gone or of another repository fails loudly; there is no fall back to the project checkout. A fork takes no workspace (a fork is a new writer, and `meta.cwd` is never copied). A commit is attributed to a project's ideas by repository evidence (`--git-common-dir`, `git -C` honoured), not by path containment. **What remains**: the guard is a *path* check — native write/edit tools and a shell call's `cwd` that resolve into another checkout of the same repository are refused (casing, `..`, junctions and symlinks resolved first), but a shell **command** is not parsed and a plugin can write anywhere, so this is not an OS sandbox (see the guardrails limit above). A workspace is only as isolated as its own branch: nothing merges automatically, and one integrator owns the integration branch.
- The budget ledger is the Agents plugin's own data (`ctx.Data`), and the core's model catalog refuses no model:
  while that plugin is absent or its store unavailable, paid calls are neither metered nor limited. The Work tab
  refreshes on `usage.changed`, which the plugin publishes after a recorded call and when `budget.*` settings change.
- The registry of what runs on shared model resources is held by the runtime plugin and adopted by the agents plugin, so a
  reload, or a disable and re-enable, of the agents plugin keeps runs in flight counted (a new run waits for them; the
  runtime admits against the same registry while the plugin is off). A reload of both plugins at once (a publish that swaps
  them together) starts a new registry while the old runs' calls may still be returning, so it can temporarily exceed
  the configured slots; persistent budget reservations are shared across the old and new plugin instances either way.
- Linux/macOS and paid/live-provider billing behavior were not validated in this change. The Anthropic provider
  still needs verification against the real API. CI (`.github/workflows/ci.yml`) runs on every push, on Windows
  only, and its 2026-10-05 runs are not green (see Validation).
- `src/NetPI.Desktop` references the WebView2 SDK as a floating `1.0.*`: every restore may resolve a newer package,
  so two desktop builds days apart are not the same bytes and a package regression would arrive unannounced. Pin it
  to the version a Windows restore resolves (`obj/project.assets.json` names it); the exact version is not known
  offline, so it is not pinned here.
- Historical model experiments and earlier verification claims are preserved in
  [the archived status](archive/2026-09-25-status.md); they are not fresh release verification.

## Useful next capabilities

Language-server diagnostics and PDF/Office reading remain backlog items; scheduled runs are `netpi.schedules` (RPC only for now, no tab). MCP, partial rollback
and worktree automation were deliberately deferred or dropped in the earlier harness plan; they are not accidental
omissions. See [the archived harness decisions](archive/2026-09-26-harness-gaps.md).

## MCP plugin (2026-09-30)

`plugins/NetPI.Mcp` (in the solution) ships all four delivery stages: stdio/deferred dispatch, the disclosure
lifecycle and targeted notices, Streamable HTTP, and the server-management UI. The MCP transport, runtime,
lifecycle and mocked end-to-end cases pass in the suites; the Chrome attachment test now treats a browser that
exits with code 0 (the launcher hands it to another process) as healthy. Native provider adapters, OAuth,
prompts/resources and legacy HTTP+SSE remain follow-ups; see [PLUGIN-MCP.md](PLUGIN-MCP.md).
