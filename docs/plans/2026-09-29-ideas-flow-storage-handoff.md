# Ideas plugin: reliable capture, storage and completion tracking

Status: implementation handoff; fixes are proposed, not implemented or deployed.
Date: 2026-09-29. Reviewed source baseline: 12ed776.
Scope: the Ideas plugin and its UI, persistence, backup and background-work integrations. This is separate from the agent-capacity handoff.

## Goal and constraints

Make an idea reliable from capture through discussion, implementation and completion: no lost cards, skipped work or misleading UI state. Preserve project scoping and the existing agent tool while repairing failure paths.

Read repository and parent AGENTS.md first, then docs/PLUGIN-IDEAS.md, docs/SETTINGS.md, docs/PROTOCOL.md, docs/TOOLS.md, docs/PLUGINS.md and docs/TESTING.md. Historical intent is in docs/plans/2026-09-27-ideas-follow-the-session.md. The findings below come from source review, not newly executed reproductions; add regression tests before fixing each one and recheck against current HEAD.

- One branch/worktree per assignment; explicitly stage only owned files. No dependencies between plugin assemblies. Contracts change additively; rebuild every plugin if abstractions change.
- Tests/builds use artifacts/dev/app. Do not publish, restart the app, alter live settings/backlog, or run test decisions on the user's occupied GPU.
- Preserve unknown JSON fields, project identity, idea/section IDs and existing tool/RPC behavior unless a change is explicitly documented.
- Keep automatic matching advisory: model conclusions may link evidence or offer a card; marking done through a card requires the user's action. Preserve the existing explicit agent-tool status update capability.

## Current flow and storage

| Entry point | Current behavior |
| --- | --- |
| Ideas tab, /idea, ideas tool | Writes the global backlog; new ideas carry the session's project or an explicit project override |
| Send to chat | Stages an ID/title pointer; the agent reads current content using the ideas tool |
| First-message recall | Matches project/global ideas; accepting attaches a full-text notice |
| Tab close | Attempts conversation attachment, then drafts a save suggestion for an unsaved plan |
| Repository commit | Links evidence and may offer a mark-done card |
| Card resolution | Saves an idea, marks one done, or discards the suggestion |

Authoritative backlog: <NetPI home>/<ideas.fileName>, default ideas.json. Separate ideas-pending.json contains suggestions, checked-session marks and repository cursors. Sessions/projects live in the host database. The README still describes per-project .netpi/ideas.json; correct this stale description.

## Storage direction

Immediate implementation preserves JSON as the documented source of truth, including external edits. Fix its failure modes before considering migration. A switch to SQLite is a separate architectural proposal, not an assumed part of this handoff.

Longer-term recommendation: plugin-owned tables in the existing SQLite database for ideas, sections, links, suggestions and check jobs; JSON import/export for portability. Such a proposal must explicitly address losing direct file-edit semantics, stable IDs/extra fields/order, idempotent migration, backups, rollback and old plugin versions. Do not create a second writable source of truth.

## Verified source paths and regression targets

| Priority | Finding | Trigger and consequence | Entry point |
| --- | --- | --- | --- |
| P1 | Card removed before backlog save | Invalid backlog, write failure or crash after removal loses the user's pending card | IdeaSaveCheck.Resolve |
| P1 | Backups omit Ideas data | Restore database/settings and neither backlog nor pending cards return | NetPI.Backup/BackupPlugin.CreateAsync |
| P1 | Close-chat attachment lacks conversation evidence | digest is accepted but omitted from the decision request; matching cannot inspect the chat | IdeaSaveCheck.AttachAsync |
| P1 | Selecting none indexes past candidates | The none option wins; attachment throws and the subsequent save check never runs | IdeaSaveCheck.AttachAsync/DecideAsync |
| P1 | Write coordination does not survive overlapping instances | Plugin swap starts a second store with separate locks; read-modify-write updates can overwrite each other | IdeasStore._locks; IdeaSaveCheck._gate |
| P1 | Atomic-save fallback overwrites in place | Rename fails; a cancelled/interrupted fallback can truncate the original | IdeasStore.SaveAsync |
| P2 | Checks are marked complete before running | Outage, timeout or reload suppresses retry at the same user-turn count | IdeaSaveCheck.Closed |
| P2 | Digest retains the beginning, loses the ending | Long planning chat later implements/cancels work, but the check sees only early discussion | IdeaSaveCheck.Digest |
| P2 | Commit cursor reset on startup | Previously stored cursor is overwritten with HEAD; offline commits are skipped | IdeaCommitCheck.WatchProjectAsync |
| P2 | Commit pagination skips older unseen commits | More than 20 unseen commits; newest 20 processed, cursor advances past the rest | SweepAsync; Tools.Files/GitStatus.CommitsAsync |
| P2 | Worktree watcher fails entirely | .git is a file; watcher creation fails before periodic sweep registration | WatchProjectAsync |
| P2 | Suggestion removal is not broadcast | Resolve in one window/tab; other surfaces retain an unanswerable card | Resolve; ideaSuggestions.svelte.js; IdeasTab.svelte |
| P2 | Matching silently ignores later candidates | More than 51 open ideas; only the first 51 are eligible for model matching | IdeaRecall, IdeaSaveCheck, IdeaCommitCheck |
| P2 | Decision calls bypass agent admission | Recall/commit checks use direct decision HTTP while local workers occupy both GPU slots | NetPI.Decide/DecidePlugin |

Also verify: migration retries after source deletion failure can duplicate imported ideas; enabling/disabling commit checks after watchers exist; missing configured save-check model silently falls back to the first catalog model. Do not claim these are exercised live failures until reproduced.

## Assignments and dependencies

The coordinator owns contract decisions, shared test registration, integration and docs. A and B touch IdeaSaveCheck.cs: perform them sequentially or assign both to the same worker. C can proceed independently against the existing pending-store interface, then rebase onto A. D starts after A's backup/event contracts are agreed. E coordinates with any ongoing agent-scheduler work. Each worker adds dedicated test files where practical; only the coordinator edits shared test registration/harness files.

### A. Durable storage, resolution and migration

Own: IdeasStore.cs, the persistence/resolution portions of IdeaSaveCheck.cs, migration in IdeasPlugin.cs, and dedicated storage tests.

- Introduce a persistence boundary shared across overlapping plugin instances. A static lock inside a reloadable assembly is insufficient. Use existing stable services or an appropriate file-lock protocol; do not pin plugin types in host-wide state. Define what is supported for multiple app processes and external editors.
- Keep failed writes from damaging the last valid backlog. Remove destructive in-place fallback, use bounded retry for transient sharing violations, and surface errors. Reject malformed pending-store shapes rather than replacing them with empty structures.
- Make suggestion resolution recoverable and idempotent across the two JSON files. Simply moving deletion after insertion is insufficient: a crash can then create duplicate ideas. Persist an operation identity/journal or equivalent and recover deterministically. Validate action/card kind before consuming a card.
- Coordinate reads as well as writes. The suggestions-list RPC should not rewrite the pending file merely to read it. Replace the shared fixed temporary filename where concurrent instances could collide.
- Protect edits with revision/conflict detection where stale UI or external writes could overwrite newer content. State the external-editor race limitations honestly; file locks alone do not coordinate an editor that ignores them.
- Make legacy import repeatable after interruption or failed source cleanup, with preserved IDs/mapping and a durable import receipt. Do not delete the only recoverable copy before verifying the import.

Acceptance: injected failures before/after each save step preserve recoverable cards and ideas; retries create one idea; competing resolvers do not duplicate; two store instances do not lose independent updates; corrupted files remain untouched; cancellation preserves originals; restart replays unfinished operations; external edits produce a conflict instead of silent replacement where detectable; interrupted migration does not duplicate imports.

### B. Closed-chat and recall correctness

Own: matching/check lifecycle portions of IdeaSaveCheck.cs and IdeaRecall.cs, after A; dedicated matching tests.

- Include the conversation digest in attachment decisions. Validate response shape and none/index bounds before selecting a candidate. A valid none result must continue to the independent save check.
- Use explicit check states such as pending/running/completed/failed, keyed to a meaningful conversation revision. A completion mark is written only after a successful outcome; retry transient failures with bounded policy and recover interrupted jobs after restart. Distinguish in-flight deduplication from permanent completion.
- Check current run/final state before evaluating a closed tab. An old aborted turn followed by successful work must not permanently exclude a chat. Closing an actively running tab should defer the check until the relevant run finishes.
- Build bounded evidence that retains the latest outcome plus enough original intent. Include relevant save/implementation evidence instead of inferring completion only from the beginning of a chat. Do not rewrite chat history.
- Prefer explicit idea links already attached to the session over re-guessing them. Preserve task summaries as evidence and avoid duplicate save suggestions across checks of the same unresolved plan.
- Remove silent model fallback; unavailable configured models produce a visible/deferred outcome. Use an explicit ranked effort list if low is unavailable; current descending ranking can select a high effort.
- Define candidate retrieval for backlogs larger than 51 (bounded shortlist or batches), including later ideas. Keep deterministic ID matches and project/global scope.

Acceptance: assert actual model request contains chat evidence; none/invalid/empty responses do not crash; a timeout can retry; reload resumes pending work; repeated close does not duplicate; recovered aborted chats are checked; long chats ending implemented/cancelled are classified using that ending; later backlog ideas remain discoverable; no unsolicited model switch.

### C. Commit tracking and repository lifecycle

Own: IdeaCommitCheck.cs, additive Files RPC/GitStatus changes as necessary, and dedicated commit tests.

- Initialize a repository cursor at HEAD only when no cursor exists. Resume from stored progress after restart. Advance only when processing outcome is durably handled; distinguish no-match from temporary decision failure.
- Process unseen history in bounded pages from oldest eligible to newest without gaps. Define recovery for rewritten/missing history and branch switches; do not permanently stall on an unreachable cursor or silently discard work.
- Resolve Git directory/common-directory locations through the Files plugin. Watch worktrees correctly and register periodic polling even if watcher setup fails. Keep repository/project attribution explicit when projects share a repository.
- Use a retained, disposed debounce timer per watch; drain/cancel work safely at stop. Recheck closeOnCommit inside scheduled sweeps and remove stale watches for deleted/changed projects.
- Do not claim multiple-idea matching from a mutually exclusive pick-one distribution at a 0.7 threshold. Support explicit multiple IDs, then use independently scored candidates if semantic multi-linking is needed. Reuse B's candidate policy.

Acceptance: ordinary repo, worktree, watcher failure, offline commits, 45+ unseen commits, reload mid-sweep, decision outage, branch rewrite, toggling off after startup and project removal. Each eligible commit is durably processed without duplicate links, gaps or permanently lost retry opportunities.

### D. Backups, events and UI evidence

Own: NetPI.Backup/BackupPlugin.cs, composer suggestion state/cards, IdeasTab/IdeaCard and relevant mocks/UI tests. Depends on A's snapshot/event boundary.

- Include the configured backlog, pending state and any recovery journal in backup manifests/checksums. Capture a coordinated snapshot so a cross-file resolution cannot be backed up halfway through. Preserve verification of older backup formats; update retention's allowed-file logic too.
- Define and test restoring Ideas with database-backed projects/sessions. A successful checksum check is not a restore test; use an isolated home to restore and inspect the recovered state.
- Broadcast suggestion changes/removals after durable commits. Both composer and Ideas tab reconcile a canonical snapshot on reconnect/visibility as needed. Concurrent answers handle already-resolved cards without leaving a permanent error card.
- Expose linked sessions/commits as inspectable evidence with usable links where supported, not just counts. Keep project scope visible and narrow panels usable. Present failed/deferred checks with a concise reason and retry route.
- Keep Send to chat as a current-content pointer; avoid implicit execution or status changes merely from clicking Send.

Acceptance: backup with custom ideas.fileName and pending cards restores correctly; older snapshots still verify; resolution during snapshot remains recoverable; two windows plus composer/tab stay consistent; hidden/reconnected tabs catch up; broken/deleted evidence links degrade cleanly.

### E. Background model admission and observability

Own: decision transport/admission integration and Ideas background-call metadata; coordinate with scheduler owner, not a parallel scheduler rewrite.

- Route Ideas generative and decision work through a shared admission contract for the actual backend resource, with background priority, cancellation and coalescing. A separate decision-plugin semaphore alone does not enforce the GPU's total capacity.
- Preserve responsiveness of foreground work. When calls originate inside an agent already holding a compatible lease, define reuse/yield semantics to avoid deadlocking both local slots.
- Debounce typing checks, cancel superseded requests and bound per-repository work. Do not send backlog work to a paid model automatically.
- Record purpose, originating project/session or repository, queue reason, duration, outcome and failures in diagnostics. Verify actual backend behavior before promising cache residency or latency improvements.

Acceptance: two active local workers plus recall/close/commit checks obey admission policy; foreground calls are not starved; cancellation removes queued checks; nested calls do not deadlock; diagnostics account for background work. Use mocks; any real 5090 measurements require an idle test window.

## Coordinator validation and delivery

1. Integrate A, then B; integrate C against the agreed persistence interface. Integrate D after snapshot/events are stable. E must agree with the separate agent-capacity plan.
2. Run owning unit suites and focused UI checks, then the normal build/test gate. Rebuild all plugins for contract changes. Commit only relevant generated UI output. Add isolated failure-injection and restart tests, not only success-path model mocks.
3. Update docs/PLUGIN-IDEAS.md, SETTINGS.md, PROTOCOL.md, TOOLS.md, TESTING.md and backup documentation for actual changes; correct README storage location. Record migration and compatibility behavior. Update this plan with completed work and evidence, not unverified claims.
4. Deliver commits, changed paths, test commands/results, remaining limitations and deployment requirements. Verify merged branch ancestry. No live deployment is included in this handoff.

Definition of done: pending cards and ideas survive interrupted operations; backups restore all Ideas state; matching uses real conversation evidence; checks retry recoverably; commit tracking covers restart/bursts/worktrees; UI surfaces agree; local background work is observable and respects the chosen admission policy. SQLite migration remains a separate decision.

## Dispatch prompt

Read docs/plans/2026-09-29-ideas-flow-storage-handoff.md and repository instructions. Implement assignment <A/B/C/D/E> in your own branch/worktree, respecting dependencies and owned files. Reproduce each relevant finding in a test before fixing it. Coordinate shared contracts and test registration with the parent. Preserve documented behavior and unknown fields; do not deploy or touch live data/settings. Return your commit, changed paths, tests/results, compatibility notes and outstanding integration risks. The coordinator owns final validation and docs.
