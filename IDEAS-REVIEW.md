# Ideas post-merge review — 2026-09-29

Reviewed 9bcb968. Review-only branch codex/ideas-review; no implementation fixes or live-data changes.

Builds: dotnet build tests/NetPI.Aux.Tests -v:q; dotnet build tests/NetPI.Host.Tests -v:q. Both passed with existing warnings.
Tests use review-run.ps1 to isolate TEMP/TMP under this worktree's artifacts/review-temp.

- .\review-run.ps1 -Suite Aux -Filters 'ideas','review:'
  57 existing Ideas tests passed. Two added review regressions failed as expected (bugs below).
  One existing collectible-load test could not run: it expects artifacts/app/plugins/NetPI.Ideas, while the development build uses artifacts/dev/app. This is not evidence of a plugin-unload regression.
- .\review-run.ps1 -Suite Host -Filters 'backup:','review:'
  Both existing backup tests passed. Added missing-file review regression failed as expected.

Reproduced:
1. IdeasStore.UpdateAsync mutates the cached JsonObject before invalidation. A patch that sets a title and then fails status validation leaves that title in memory; a later unrelated successful update writes the rejected title to disk.
2. A second store with a warmed cache can overwrite an intervening committed update even though both take the OS lock. Fault injection disables the second watcher to simulate delayed/lost OS notification. Its write uses the old cached tree: two committed additions leave only one on disk.
3. BackupPlugin.Verify filters manifest entries through File.Exists. A manifest naming three missing data files is accepted. Verification must reject missing required/listed files and validate manifest structure.

Other source-reviewed concerns:
- Backup skips Ideas after lock timeout yet publishes a successful snapshot; this is incomplete, not a copy of older Ideas state. Retention can eventually remove older complete snapshots.
- SnapshotIdeas holds only the first existing file's lock; pending-state writers lock their own file. This is not a transaction across backlog and pending state.
- IdeaAdmission executes without a slot after two seconds, so the two-slot limit is not a hard bound for Ideas work. SQLite will not fix admission.
- Optional expectedUpdatedAt compares timestamps with second precision. Prefer an integer revision and ensure mutation callers supply it where conflict protection is needed.

Recommendation: move Ideas authoritative storage to plugin-owned tables in existing netpi.db through ctx.Db. Keep current UI/tools and JSON import/export. Resolve a suggestion and update/create its idea in one short transaction; retain durable check/cursor records and use explicit revisions. Preserve JSON unknown fields and stable IDs in migration. Import backlog, pending operations, checks and cursors together; commit an import receipt; retain original files and enforce a backend/schema compatibility gate so an older plugin cannot resume writes to stale JSON. Do not implement writable JSON+SQLite mirroring. Keep backup verification and scheduler fixes as separate required work.
