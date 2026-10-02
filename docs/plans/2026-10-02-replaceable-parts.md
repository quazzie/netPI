# Replaceable storage: a swap-over that changes nothing you can see

Date: 2026-10-02
Status: proposed (nothing implemented). One swap-over: all the code is written in one session, then a test/fix loop runs until the definition of done is met. Review snapshot: master at 59619d2. Reviewed twice the same day by one Opus agent (read-only); its findings are folded in and marked **(review)**.

## Goal

Exactly today's behaviour, with **storage as a part you can swap**, and **no specific plugin required**. Someone can later write an MSSQL (or any) storage provider by implementing the storage port in that engine's own SQL and passing the conformance suite. Nothing else changes: the wire protocol is frozen, so the UI does not change.

## Ground rules

1. **Contracts may change whatever the design needs.** No compatibility shims, deprecated paths, mirrors or forwarders. A contract change updates every plugin, test and doc in the same change. (`AGENTS.md` and `HANDOFF.md` say so as of this branch.)
2. **Delete what you replace** in the same change.
3. **Only the owner's local setup is migrated**, by one one-off tool, after a snapshot, with the owner's say-so. Migration code never ships in the product.
4. **The wire protocol is frozen.** No RPC renamed, no event changed, no payload shape changed, no new error codes. `web/` and the plugin tab bundles have no diff. Where something the kernel owns today moves into a plugin, the plugin registers or publishes the same names with the same shapes.
5. **No dialect layer.** The port exposes no SQL and no engine-specific type. Each provider writes its own SQL natively, inside itself.

## What is in the core, and why

A thing is core only if every part must speak it, or parts cannot exist without it.

- **The plugin mechanism:** loading, lifecycle, hot reload, isolation; the registries (services, tools, RPC, UI tabs, events).
- **The wire:** transport, auth and the protocol the UI speaks. The UI is a client of it: RPC calls plus event subscriptions. Freeze the protocol and the UI is untouched.
- **Settings** (the bootstrap root), logging, paths and the home lock.
- **The chat domain:** sessions, messages and projects (the types, and the session service that adds events, transient sessions, the context cache, titles and forks), and **models** (the catalog and the provider and middleware contracts). Nearly every part speaks these.
- **The tool contract:** definition, arguments, results.
- **The storage port:** interfaces only. The built-in `sqlite` provider (today's code, moved) and a `memory` provider (tests and `--ephemeral`) are the first two implementations.

**Not core**, though some of it feels like it: the budget and metering (a plugin using the existing middleware pipeline), workspaces (behaviour and data), the agent loop, prompt assembly, compaction, ideas, every tool, every UI feature.

## What the architecture review found, and what this plan does about it

| # | Finding | This plan |
|---|---|---|
| 1 | **Storage cannot be replaced.** The host builds the concrete `Database` and `SessionStore` before any plugin loads (`HostKernel.cs:70,84`); plugins get them (156 `ctx.Sessions`, 21 `ctx.Db` uses, 2 registry lookups); `IDatabase` is SQLite SQL (~102 sites in 5 plugins); Backup runs `VACUUM INTO`. | **In.** |
| 2 | **Paid models need the Agents plugin.** `ModelCatalog.cs:150` refuses non-local models unless something registers the empty marker `IBudgetGate`; only Agents does. | **In:** delete the check and the marker. Budget is not core. |
| 3 | **Workspaces are kernel data** (table, `sessions.workspace_id`, RPCs, events; `SessionStore` implements `IWorkspaceStore`). | **In**, so the provider interface has no workspace methods. |
| 4 | Policy statics in the contract assembly. | Not in this change. |
| 5 | Tool arguments parsed many times; guards match names. | Not in this change (the guard bypass is already fixed in 9efaf19). |
| 6 | RPC registrations have no owner. | Not in this change. |
| 7 | The UI shell hard-wires 11 plugins' features. | Not in this change (the UI does not change). |
| 8 | Fork policy hard-codes meta keys; host-seeded settings; typed decision contracts. | Not in this change. |

Evidence for plugin ↔ plugin independence: 35 real servers were booted with each plugin removed, and host-only and Runtime+provider sets. Every one boots; the kernel alone starts in 0.5 s with 36 core RPCs. Independence is sound; the gaps are what the kernel owns.

## The design

### 1. The storage port

```
IStorageProvider  { Id; OpenAsync(StorageContext) → IStorage }      chosen by `storage.provider` (default sqlite), at startup only
IStorage          { Sessions: ISessionRepository, Kv: IKeyValueStore, Data: IPluginData,
                    Snapshot: IStorageSnapshot, Lock, Info }
```

- **`ISessionRepository`**: primitives plus one `Atomic(Func<tx,T>)` that runs on the provider lock, about 20–25 methods **(review)**. Several operations are atomic across tables (materialising a first message touches the session, the project's `last_used_at` and the message; append assigns seq as `MAX+1` with counters and auto-title; project delete detaches sessions; session-tree delete; fork copies messages and recomputes compaction as of the fork point). The kernel's **session service** keeps the behaviour (events, transient sessions, the context cache, titles, compaction-as-of-fork, id remapping), so a provider implements persistence only. `sessions.meta` stays one JSON field; the protocol keeps its keys.
- **Concurrency contract** **(review)**: every provider has one **in-process re-entrant lock** and one dedicated connection, exposed to the kernel (the transient-session map and the stall watchdog share it today, which is the session deadlock fix); plugin transactions are **exclusive per plugin id across hot-reload generations** (so two Agents generations never double-charge a call); `UpdateSession`'s `mutate` keeps today's semantics (it runs inside the lock), calling a plugin store inside it is forbidden, and the one existing violation (`Ledger.Allow`, idea-7v22l8) is fixed. The conformance suite pins all of it.
- **`IPluginData` (`ctx.Data`)** replaces raw `IDatabase`: typed collections carried as **JSON documents plus declared index fields**, keyed get/put/delete, equality/range/IN filters on indexed fields, ordering and limit, **compare-and-set**, and the per-plugin transactions above. No group-by (the ledger keeps its roll-up by upsert). Plugin types never cross the port, or a provider would pin a plugin's load context and block unloading. Typing is a helper on the plugin side.
- **`IStorageSnapshot`**: the provider snapshots itself; Backup uses it instead of `VACUUM INTO`, and the restore script dispatches on the manifest's provider.
- **Selection fails closed**: a missing or broken provider stops startup with a clear error; `memory` runs only when asked for (`--ephemeral`), because running the owner's NetPI ephemeral after a broken install would lose every chat **(review)**.
- **The sqlite provider** is today's code moved behind these interfaces. It keeps today's core tables (the 200 MB of messages does not move) and writes its own SQL. It has no knowledge of any other dialect.

### 2. Workspaces leave the kernel

The `workspaces` table and its CRUD move into the Workspaces plugin as a collection. The session keeps an **opaque `workspaceId`** that the kernel stores and does not interpret (like `model` or `project`). The `workspaces.*` RPCs and the `workspace.*` and `session.workspace` events keep their names and shapes, now registered and published by the plugin, so the UI sees no difference. Workspaces are extracted from `SessionStore` before the repository is written, so the repository is never designed around them and then stripped **(review)**.

### 3. Budget leaves the kernel

Delete the `IBudgetGate` check in `ModelCatalog` and the marker interface. The ledger, caps and reservations stay in the Agents plugin as a normal model middleware (it already is one). The kernel keeps only descriptive data (`ModelInfo` price and `IsLocal`) and the generic middleware pipeline. Trade-off, stated plainly: if Agents fails to load, paid calls are no longer refused automatically. Instead Diagnostics reports "N paid models available and no metering active" as a visible problem. Paid providers already need an API key you set. Decide's direct calls to the model server stay outside the catalog, as they are today.

### 4. Test infrastructure follows

`TestHost` runs the real session service over `memory`; `FakeSessionStore`, `NullDatabase` and the duplicated catalog fake behaviour are deleted; Host tests build providers through a factory instead of `new Database` (13 sites) **(review)**.

## What gets written

The order of **writing**, so each layer compiles against the one below. There is no UI step.

1. **Contracts:** the storage interfaces and `ctx.Data`; delete `IDatabase`, `Migrate(DDL)`, `IBudgetGate`, `NetPiPaths.DatabaseFile`, the `ISessionStore` shims (`ForkSession`, `SessionIdsUsingWorkspace`). `IPluginContext` loses `Db`, gains `Data`; `Sessions` and `Models` stay.
2. **Providers and the conformance suite:** `sqlite` (today's SQL moved) and `memory`, and the suite, written together with the port. It pins: id and seq allocation (message ids are global), search semantics (`LIKE ... ESCAPE`, ASCII case folding), list order (pinned, `updated_at`, id), cascades, fork-at-seq, the lock rules above, collection semantics (CAS, index filters, ordering), snapshot round trip, a stress test for the deadlocks the old code fixed, and unload of a plugin's load context. This suite is the contract a future MSSQL provider must pass; it is what makes writing one realistic.
3. **Internal checkpoint** (below), right after step 2 and the core of the session service.
4. **Kernel:** provider loading, the session service over the repository, workspaces extracted, the catalog without the budget check, `app.info` keeps its fields (the sqlite provider reports its version, `memory` reports none).
5. **Plugins that change:** Agents (ledger and reservations onto `ctx.Data`; its middleware unchanged; drop the memory-charges fallback), Runtime (agent records), Context (prompts, fork copy), Ideas (its 8 tables; delete the finished legacy cutover, idea-2iz2bu), Workspaces (owns its collection, RPCs and events), Backup (snapshots), Diagnostics (the metering warning, storage info). **Every other plugin is untouched.**
6. **Tests, scripts and docs:** the test infrastructure above; the 35-boot independence matrix as `scripts/independence.mjs` (its paid-call probe now expects a paid provider to work with Agents removed); `restore-backup.mjs`; `PLUGINS`, `BACKUPS`, `SETTINGS` (`storage.provider`), `HANDOFF`, and the independence plan.

### Internal checkpoint (decides the plugin-data port; not a stage)

Run it in its own test project (contracts, providers, Agents, Ideas) before the rest of the kernel and before the other plugins. Nothing else compiles at that point (step 1 deletes `IDatabase`), so a failure costs only steps 1 and 2 **(review)**. Port **Ideas** and the **Agents ledger** onto `ctx.Data` and check, on sqlite and memory:
1. Two Agents generations reserving at once give exactly one reservation per call and never exceed the budget.
2. Reading a session inside a plugin transaction does not deadlock.
3. Ideas' hard flows need no SQL: `ResolveCard` (case-insensitive id or prefix), `ClaimCheck`/`FinishCheck` (conditional upsert with a token compare-and-set), `ord` as MIN−1/MAX+1, the `NOT IN` cleanup; common paths do not scan whole collections.
4. Ledger totals (period, today, per lane) equal today's SQL on a copy of your data.
5. The plugin's load context is collected after unload.
6. The port has about 12 operations or fewer, and exposes no SQL or engine-specific type.
7. Context's fork copy works (range copy, `MAX(version)+1`), and Workspaces' binding on a transient session works through create, materialise and discard.

If 1, 2 or 5 need per-provider special cases, plugin data stays SQL in the SQLite dialect behind a declared optional capability. Say plainly what that costs: Ideas, the ledger, Context and Runtime's store would report unavailable on any other engine, so a future MSSQL provider could host the core but not those plugins.

## Test and fix until done

In this order, repeated until green (every failure fixed in the code it exercises; no "re-run and see"):
1. The solution builds with zero warnings.
2. The conformance suite on sqlite and memory.
3. The five unit suites.
4. The independence matrix: all 35 boots, plus a paid provider working with Agents removed.
5. The whole e2e suite.
6. The mock UI walkthrough and the UI e2e **unmodified** against the new kernel.
7. A cutover rehearsal on a copy of your data.

**Done means:** all green; **`git diff` shows nothing under `web/` and nothing in the wire protocol**; the rehearsal verified (row counts and message checksums equal before and after; per-collection document counts and ledger totals match; the migrated schema equals a fresh install's); zero skipped tests (idea-r4e8rf); and the deletion checklist empty: `IDatabase`, `ctx.Db`, `Migrate(`, `IBudgetGate`, `ForkSession`, `SessionIdsUsingWorkspace`, `NetPiPaths.DatabaseFile`, every `_dbReady`/`_memoryCharges` fallback, `FakeSessionStore`, `NullDatabase`, `new Database` in tests.

## Cutover and rollback (your local setup)

One-off tool `scripts/migrations/001-storage-port`: a C# console referencing the sqlite provider (no script reads SQLite today, and a second copy of the DDL would drift). Dry-run by default; deleted once applied. `--apply` refuses while NetPI is running and refuses if the new collections already exist. It converts the plugin tables (Ideas, ledger, Context, Runtime) from rows into documents, moves the `workspaces` table into the Workspaces collection (`sessions.workspace_id` stays), and drops the dead tables and their `Migrate` version rows. The core tables (sessions, messages, projects, kv) do not move.

1. **Rehearse on a copy** of your data (needs your go): the same tool, the new build booted against the result, a chat, a backup and a restore.
2. **Copy the whole current app folder** aside: the installer deletes `.old` at every start, so the old build is not kept automatically **(review)**.
3. **Close NetPI, then snapshot** (`netpi.db`, `-wal`, `-shm`, `settings.json`, `idea-images/`): a snapshot taken before closing loses what is written in between **(review)**.
4. `build.ps1 -Publish -NextStart` the new build, `--apply`, start it, check a chat, ideas and a backup.
5. **Rollback:** close NetPI, restore the snapshot and the copied app folder. Old backups are restorable only by the old build; `BACKUPS.md` says so.

## What you need to decide

- **Plugin data as typed collections** (recommended; the checkpoint confirms it or switches to the SQLite-dialect fallback described above).
- **Budget out of the kernel**, with the Diagnostics warning instead of an automatic refusal (recommended).
- **Permission** to copy `netpi.db` and `settings.json` read-only for the rehearsal.
- **Freeze** the Host, the contract assembly and the plugins this change touches on master for the duration (web and docs work may continue); land everything else first. Each plugin commit written against the old contracts would otherwise have to be ported again **(review)**.

## Notes for a future MSSQL provider (not built now)

It implements `IStorageProvider` with its own SQL and passes the conformance suite. It would ship as an optional package under `<app>/storage/<id>/`, which needs the installer (`PendingBuild`, `build.ps1`, `build.sh`) to learn to install provider folders (today only plugins and `wwwroot`): build that when the first non-built-in provider exists. It also needs an exclusive database instance lock at startup so two homes cannot share one database (the file home lock does not cover that). Dialect traps found in today's core SQL, so they are written down once **(review)**: Oracle-style `''` = `NULL` hits `sessions.title` and `kv`; `INSERT … SELECT MAX+1 … RETURNING` and `WITH RECURSIVE … DELETE` have no portable form; `LIMIT -1`; `INSERT OR IGNORE`; case-insensitive default collations change key equality and ordering; `kv.key` is a reserved word on SQL Server; DDL commits implicitly on Oracle; large text columns cost on `messages.parts`.

## Not in this change

Policy moves out of the contract assembly; normalised tool arguments and guards by role; one owner per RPC and an error taxonomy; the session `meta` bag and the fork policy; UI slots and the shared Svelte runtime; settings seeds; typed decision contracts; the two small silent-failure fixes (`agent_spawn isolated` with no Workspaces, a throwing before-tool-call hook); SQL Server, Oracle or any other engine; a SQL-translation layer; hot-swapping storage at runtime; touching `ISettings`. Each can be taken later; none is needed for storage to be swappable.
