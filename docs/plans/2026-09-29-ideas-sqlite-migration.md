# Ideas: migrate authoritative storage to SQLite

Status: implementation handoff; migration is not implemented or authorized for execution on live data by this document.
Date: 2026-09-29. Documentation baseline: 0ff5edc. Post-fix review baseline: 9bcb968.

## Outcome

Move Ideas persistence into plugin-owned tables in the existing netpi.db through ctx.Db. Keep the Ideas UI, agent tool, project scoping and automatic-check behavior. Resolve a suggestion and create/update its idea in one transaction. Remove the multi-file journal, mutable parse cache and JSON write coordination from the active backend.

SQLite becomes the single authoritative store. JSON remains an explicit import/export format. Editing an exported JSON file does not change the live backlog. No writable mirroring, second database, new database package or general-purpose memory framework is included.

Read AGENTS.md and the parent instructions, then docs/PLUGIN-IDEAS.md, BACKUPS.md, PROTOCOL.md, SETTINGS.md, TESTING.md and PLUGINS.md. Read the implemented Ideas-flow handoff for behavior to preserve. The review and three failing reproductions are on codex/ideas-review at b29a868: IDEAS-REVIEW.md, ReviewStorageTests.cs and ReviewBackupTests.cs. Reuse the cases deliberately; do not blindly merge the review branch's runner edits.

## Why this migration

The merged fixes provide recovery and better checks, but the review reproduced:

- A patch changes a cached title, then fails validation; a later unrelated update persists the rejected title.
- Two stores take the OS lock, but one with a stale parse cache overwrites the other's update when a watcher event is missed.
- Backup verification accepts missing manifest files because it filters them through File.Exists.

57 existing Ideas tests and two backup tests passed in that review; the three additional regressions failed. One collectible-load test could not run because it expects artifacts/app while development output is artifacts/dev/app. Fix its fixture/path as part of validation; this was not proof of a plugin-unload defect.

The benefit sought is transactional correctness and less custom persistence code. Do not claim performance gains without measurements. Database transactions do not fix stale application objects: the new repository must read current data and validate/write fresh values inside the transaction.

## Design decisions

### Repository boundary and data model

Use a plugin-local repository abstraction and ctx.Db.Migrate with a unique scope such as netpi.ideas. Keep database access out of UI/tool/check code. Avoid new host contracts unless an existing boundary cannot support the requirement; any contract additions must be additive.

Suggested minimal tables (coordinator owns final schema):

| Table | Purpose |
| --- | --- |
| ideas_items | Stable idea ID, integer revision, manual order, query projections and complete idea JSON |
| ideas_suggestions | Stable suggestion ID, kind, source session/idea, source revision and complete card JSON |
| ideas_resolutions | Unique suggestion ID, action and resulting idea ID; prevents duplicate effects on retry |
| ideas_checks | Session/conversation revision, state, attempt count, due time, claim token/expiry and last error |
| ideas_repos | Repository identity, project attribution, cursor and retry/error state |
| ideas_imports | Import identity, source checksums, schema version, counts and completion receipt |
| ideas_metadata | Preserved root-level extension data and backend metadata |

Keep sections, tags, session links, commit links and unknown extension fields in each idea's JSON initially. Separate tables for them require a demonstrated query or integrity need. All projected columns and document JSON update together through one repository path; prevent divergence. Persist whole unknown fields at root, idea, section, card and imported state levels. Preserve array/manual ordering and legacy IDs, including nonstandard IDs already present in user data.

Index actual queries, initially project/status/order and due check jobs. Use parameterized SQL. Do not require SQLite JSON extensions or FTS unless the shipped runtime is verified to provide them. Avoid cascading deletion from a removed project/session to an idea; preserve orphan references and display them gracefully.

### Mutation and revision rules

- Use a monotonically increasing integer revision, not timestamps, for optimistic concurrency. Validate the patch completely or apply it to a detached copy before any write. On failure, neither persistent nor subsequently visible in-memory state changes.
- Expose expectedRevision additively. Update UI editors to submit the revision they read and preserve unsaved edits on conflict. Document how legacy callers without a revision behave; do not silently claim they have conflict protection. Preserve expectedUpdatedAt temporarily as a deprecated compatibility input, with its limitations stated.
- In a single short transaction, resolve a card: validate kind/action, check the resolution identity, create/update the idea, record the resolution and remove the suggestion. Concurrent answers and retries must never create a second idea. Define the compatible response for an already-resolved card and test it.
- Emit events only after commit. Reconnecting clients obtain a canonical snapshot; do not claim exactly-once event delivery. Preserve ideas.resolved/ideas.suggested behavior or change it additively with documentation.
- Check jobs claim work transactionally, release the database, then call models. Complete with the claim token and source revision so old workers cannot overwrite newer outcomes. Recover expired claims after restart; retain bounded retries.
- Repository cursors advance only with a durable handled outcome. Keep the merged offline-commit, pagination and worktree fixes.
- Do not await model/network/file work inside ctx.Db.Transaction. The host connection is shared with chat/session work: transactions and imports must be bounded, with large-source parsing/validation outside the transaction.

## Cutover, migration and downgrade

The first JSON-to-SQLite cutover requires a controlled restart/offline phase. Do not perform it during the current plugin swap lifecycle: the old JSON plugin can still be serving requests while the replacement starts.

1. Produce a preflight report from an isolated copy: resolved source paths, schema support, counts, malformed records, duplicate IDs and any pending operations. Read the configured global backlog, ideas-pending.json and ideas-migration.json. Handle remaining legacy per-project files using durable receipts; do not invoke destructive legacy cleanup as a prerequisite.
2. Validate all sources and preserve byte-for-byte originals plus checksums in a migration archive. An invalid or ambiguous source fails the import with a precise diagnostic; never substitute an empty backlog. Missing optional files are distinct from malformed files.
3. Require exclusive lifecycle ownership with the app stopped for the first cutover. Parse and stage outside the database transaction; recheck source identity/checksums before committing. Import ideas, cards, checks, cursors, extension fields and legacy receipts together, with an import-complete receipt in the same transaction.
4. Reconcile legacy ops using stable operation/card/idea identities. Cover unapplied and already-applied entries, consumed cards, pending done actions and ID collisions. Do not replay through the old live JSON writer or regenerate IDs on each retry. Ambiguous state requires a diagnostic, not a guessed duplicate.
5. Before the transaction commits, a failure leaves the old backend authoritative. After commit, the durable backend marker makes SQLite authoritative even if later archive/housekeeping fails. Restarting the importer must detect completion and not import old JSON again.
6. Verify counts, IDs, ordering, fields and pending-state outcomes against staged data. Retain originals; any later cleanup is separate from successful migration. Do not have both backends writable after cutover.
7. Define an enforceable compatibility gate before implementation. The supported host/plugin combination must refuse an incompatible storage version or downgrade. A new marker alone cannot make an already-shipped old binary refuse stale JSON. If protection requires a bridge release or guarded installation path, document and test that sequence; do not claim arbitrary old binaries are safe.
8. Provide explicit downgrade export or full pre-migration restore instructions with the app stopped. Restoring the old snapshot loses post-migration changes unless those are exported. Restoring the shared netpi.db also rolls back chats/settings-related database state: never replace the whole database merely to roll back Ideas without explaining the effect. Prefer Ideas-only export/import for deliberate data transfer.

After migration, ideas.fileName is only a legacy import/default-export hint, not a live backing file. Update UI descriptions and file-path assumptions. Add a storage descriptor to ideas.list rather than pretending netpi.db is an editable ideas.json. Document deprecation of legacy file/exists fields; coordinate all in-repo callers and mocks.

Explicit import/export is separate from automatic migration. Export a versioned, portable snapshot with stable IDs, order and unknown fields. Import supports validation/preview and a documented conflict policy; do not silently replace or duplicate existing records. No automatic reimport on file changes.

## Assignments and integration order

Each agent uses its own branch/worktree. The coordinator owns schema/API decisions, shared test harness registration and integration. Commit explicit paths only. No two workers edit the same files simultaneously.

### A — Repository and transaction semantics

Own new repository/schema code under NetPI.Ideas and dedicated persistence tests. Agree repository methods and schema before B/C integrate. Implement CRUD, ordering, revisions, atomic resolution, check claims and cursor storage. Use real temporary SQLite databases for persistence tests; an in-memory fake cannot prove rollback, uniqueness or cross-instance correctness.

Acceptance: rejected mutations leave no trace; independent instances preserve both updates; stale revisions conflict; concurrent resolutions produce one effect; transaction failure rolls everything back; restart preserves state; deterministic ordering/unknown fields survive; no long-running work holds the shared database transaction.

### B — Import, export and controlled cutover

Own importer/exporter and migration tooling, after A's schema is agreed. Coordinate IdeasPlugin startup and host compatibility boundaries with the coordinator. Build isolated fixtures for all legacy shapes, pending journal stages and import receipts. Produce preflight/migration diagnostics and rollback instructions.

Acceptance: interrupted import before/after commit is repeatable; no duplicate ideas or lost cards; custom fileName, bare arrays, extra fields and orphan references survive; malformed input leaves sources untouched; live JSON-writer overlap is refused; supported downgrade attempts fail closed; export/import round-trip preserves semantic data. Never run migration against the user's live home as part of implementation.

### C — Workflow, tool and UI integration

Own IdeasPlugin/IdeaTools/IdeaRecall/IdeaSaveCheck/IdeaCommitCheck integration and Ideas/composer UI, coordinated to avoid B's startup edits. Replace file operations with repository methods while preserving the merged workflow fixes. Convert pending operations and check/cursor access, wire revisions, update storage indicators and canonical refresh behavior. Remove parse caches/watchers/file gates/journal replay from active persistence after parity is proven; keep legacy readers only where import needs them.

Acceptance: existing idea CRUD/project/tool behaviors hold; none/invalid matching responses remain safe; long-conversation endings remain in evidence; retries and restart recovery work; offline/45+ commit/worktree scenarios pass; two windows reconcile cards and handle conflicts; existing Send-to-chat pointer behavior remains.

### D — Backup, restore and verification

Own BackupPlugin, restore tooling, backup tests and relevant documentation. Can start the verifier fix independently of A–C.

- Require a valid, nonempty manifest with required database/settings entries. Verify every listed file exists, is an allowed path within the snapshot and has the expected checksum. Reject missing files, path traversal and unsupported manifest shapes. Retain support for valid older snapshots.
- New SQLite-backed Ideas data travels inside the consistent netpi.db snapshot. Do not require live Ideas plugin availability to back it up. Stop silently issuing an apparently complete backup with required Ideas data omitted.
- Preserve restore support for old JSON-backed snapshots; restore into an isolated home and run the appropriate importer. Keep archive/export retention separate from required database data.

Acceptance: reproduce then fix the missing-file verifier regression; restore a new snapshot into a fresh home and query actual ideas/cards/jobs/cursors; restore an old JSON snapshot and migrate once; snapshot during resolution contains a consistent before/after state; retention does not retain or remove files accidentally because of custom legacy names.

### Coordinator — final acceptance and documentation

Integrate A's contract first; B and C may then proceed against it in parallel on disjoint files. D's verifier fix is independent, with final backup integration after cutover is stable. Run targeted tests per change, then owning suites and the normal build/test gate. Rebuild all plugins if abstractions change. Fix the collectible-load test fixture to use the intended development output, and test reload between SQLite-aware plugin versions.

Use separate TEMP/TMP homes for parallel test runs; never point tests at the live app. Run UI tests/build for changed sources and commit only relevant generated bundles. Existing changes in the user's main checkout belong to other work.

Update docs/PLUGIN-IDEAS.md, SETTINGS.md, PROTOCOL.md, TOOLS.md, BACKUPS.md, TESTING.md and README/HANDOFF where relevant. Include supported upgrade/downgrade combinations, retirement of live JSON editing, import/export examples and recovery diagnostics. Deliver commits, test results, schema/migration version and remaining limitations.

## Separate required follow-up

Ideas model admission still runs without a slot after a two-second wait. SQLite does not fix this. Track it with the agent-capacity work: bounded waits must defer/cancel background work or reuse/yield a compatible owning lease, not create unaccounted extra local calls. Do not fold a scheduler redesign into this migration.

## Definition of done

SQLite is the only active authoritative store; card resolution is atomic; revisions prevent stale UI overwrites; legacy data and pending work migrate once without loss; interrupted cutover is recoverable; new and legacy backups restore correctly; post-commit events/UI remain coherent; no live-file watcher or custom multi-file transaction journal is required for normal operation. Deployment and migration of the user's live home remain a separate controlled step.

## Dispatch prompt

Read docs/plans/2026-09-29-ideas-sqlite-migration.md and repository instructions. Implement assignment <A/B/C/D> in an isolated worktree, respecting dependencies and owned files. Reuse the post-fix review regressions from b29a868 as acceptance cases. Preserve IDs, ordering, unknown fields and workflow behavior; use real temporary SQLite databases for persistence tests. Coordinate schema/startup/shared-test changes with the parent. Do not deploy or access live data/settings. Return your commit, changed paths, tests/results, migration compatibility notes and unresolved risks.
