# Replaceable storage: a swap-over that changes nothing you can see

Date: 2026-10-02
Status: proposed (nothing implemented). One swap-over: all the code is written in one session, then a test/fix loop runs until the definition of done is met. Review snapshot: master at 59619d2. Reviewed twice the same day by one Opus agent (read-only); its findings are folded in and marked **(review)**.

## Goal

Exactly today's behaviour, with **storage as a part you can swap**, **no specific plugin required**, and **a core as small as possible**: only what every part must speak. Someone can later write an MSSQL (or any) storage provider by implementing the storage port in that engine's own SQL and passing the conformance suite. Nothing else changes: the wire protocol is frozen, so the UI does not change.

## Ground rules

1. **Contracts may change whatever the design needs.** No compatibility shims, deprecated paths, mirrors or forwarders. A contract change updates every plugin, test and doc in the same change. (`AGENTS.md` and `HANDOFF.md` say so as of this branch.)
2. **Delete what you replace** in the same change.
3. **Only the owner's local setup is migrated**, by one one-off tool, after a snapshot, with the owner's say-so. Migration code never ships in the product.
4. **The wire protocol is frozen.** No RPC renamed, no event changed, no payload shape changed, no new error codes. `web/` and the plugin tab bundles have no diff. Where something the kernel owns today moves into a plugin, the plugin registers or publishes the same names with the same shapes.
5. **No dialect layer.** The port exposes no SQL and no engine-specific type. Each provider writes its own SQL natively, inside itself.
6. **The core is as small as possible: it knows its own abstractions and no plugin's concept.** Budget, tools, projects, workspaces, prompts, agents, leases and decisions are plugin concepts, so none is in the kernel or in the core contract assembly (section 4). It is checked mechanically: the Host project references only the core assembly, and a search of `src/` for those names finds only the opaque `projectId` and `workspaceId` of a session.

## What is in the core, and why

The test for a thing: **could a chat with a model run, and plugins load, without it?** If yes it is not core, however many plugins use it. The core is as small as that test makes it.

- **The plugin mechanism:** loading, lifecycle, hot reload, isolation; the registries for services, RPC, HTTP routes and UI tabs; the event bus.
- **The wire:** transport, auth and the protocol the UI speaks. The UI is a client of it: RPC calls plus event subscriptions. Freeze the protocol and the UI is untouched.
- **Settings** (the bootstrap root), logging, paths and the home lock.
- **Sessions and messages:** the types and the session service that adds events, transient sessions, the context cache, titles and forks. A session carries its `projectId` and `workspaceId` as **opaque strings** the core stores and never interprets, like `model`.
- **Models:** the catalog and the provider and middleware contracts, plus `ToolDefinition` (what a model is told it may call; the model contract needs it).
- **The storage port:** interfaces only. The built-in `sqlite` provider (today's code, moved) and a `memory` provider (tests and `--ephemeral`) are the first two implementations.

**Not core**, though some of it feels like it, and it all leaves (section 4): money and metering, tools (the executable contract and the registry), projects, workspaces, prompt assembly and its revision logic, the agent loop and its hooks, agent slots, resource leases, decisions, deferred tools, background work, the session-meta key helpers for profile, tools-off, identity and agent, and the plugin-owned settings the host seeds today. Nothing else is core.

## What the architecture review found, and what this plan does about it

| # | Finding | This plan |
|---|---|---|
| 1 | **Storage cannot be replaced.** The host builds the concrete `Database` and `SessionStore` before any plugin loads (`HostKernel.cs:70,84`); plugins get them (156 `ctx.Sessions`, 21 `ctx.Db` uses, 2 registry lookups); `IDatabase` is SQLite SQL (~102 sites in 5 plugins); Backup runs `VACUUM INTO`. | **In.** |
| 2 | **Paid models need the Agents plugin, and the core talks about budgets.** `ModelCatalog.cs:150` refuses non-local models unless something registers the empty marker `IBudgetGate`; only Agents does. The contract assembly carries `BudgetExceededException`, and the kernel's fork list (`SessionFork.RunState`) names the plugin-owned meta key `budgetAllowedFrom`. | **In:** all three go (see 3). Budget is not core. |
| 3 | **Workspaces are kernel data** (table, `sessions.workspace_id`, RPCs, events; `SessionStore` implements `IWorkspaceStore`). | **In**, so the provider interface has no workspace methods. |
| 4 | Policy statics in the contract assembly. | **Moved** to `NetPI.Contracts` with the rest of the plugin vocabularies (section 4); turning them into services is not in this change. |
| 5 | Tool arguments parsed many times; guards match names. | Not in this change (the guard bypass is already fixed in 9efaf19). |
| 6 | RPC registrations have no owner. | Not in this change. |
| 7 | The UI shell hard-wires 11 plugins' features. | Not in this change (the UI does not change). |
| 8 | Fork policy hard-codes meta keys; host-seeded settings; typed decision contracts. | **In** for the fork keys (`ISessionForkFilter`) and the seeds (section 4); typed decision contracts are not in this change. |
| 9 | **The core knows plugin concepts**: the tool registry, projects, the resource-lease registry, prompt revision logic in the fork, seeded plugin settings, agent and decision vocabularies in the one contract assembly, `NetPiPaths.GlobalAgentsMd`. | **In** (section 4): the core is cut to the test in "What is in the core". |

Evidence for plugin ↔ plugin independence: 35 real servers were booted with each plugin removed, and host-only and Runtime+provider sets. Every one boots; the kernel alone starts in 0.5 s with 36 core RPCs. Independence is sound; the gaps are what the kernel owns.

## The design

### Layering: who knows what

```
plugins (Ideas, Agents, Context, Runtime, Workspaces, Backup, …)
    use only:  ctx.Sessions (sessions, messages)   ctx.Data (collections)   the snapshot
        ▼
the ports: interfaces in the contract assembly. No SQL, no engine types, no hint that a database exists.
        ▼
a storage provider (sqlite today, memory, a future mssql): the ONLY layer that knows its engine and writes its SQL
```

A plugin never sees SQL and never needs to know the store is a database: it asks for a collection by name and puts, gets and queries documents. Each plugin keeps one small repository class of its own, the only code that touches `ctx.Data`. **Today is the opposite and is what this plan removes:** five plugins (~102 sites) write SQLite SQL through `ctx.Db`. The **conformance suite** is not about SQL: it is one set of scenarios (ids allocate in order, a query returns the same rows, a transaction is exclusive, a snapshot restores) run against *every* provider to prove each behaves identically behind the interfaces, so a second provider can be trusted. It contains no SQL.

### 1. The storage port

```
IStorageProvider  { Id; OpenAsync(StorageContext) → IStorage }      chosen by `storage.provider` (default sqlite), at startup only
IStorage          { Sessions: ISessionRepository, Kv: IKeyValueStore, Data: IPluginData,
                    Snapshot: IStorageSnapshot, Lock, Info }
```

- **`ISessionRepository`**: primitives plus one `Atomic(Func<tx,T>)` that runs on the provider lock, about 20–25 methods **(review)**. Several operations are atomic across tables (materialising a first message touches the session and the message; append assigns seq as `MAX+1` with counters and auto-title; session-tree delete; fork copies messages and recomputes compaction as of the fork point). Projects and workspaces are not in it (section 2). The kernel's **session service** keeps the behaviour (events, transient sessions, the context cache, titles, compaction-as-of-fork, id remapping), so a provider implements persistence only. `sessions.meta` stays one JSON field; the protocol keeps its keys.
- **Concurrency contract** **(review)**: every provider has one **in-process re-entrant lock** and one dedicated connection, exposed to the kernel (the transient-session map and the stall watchdog share it today, which is the session deadlock fix); plugin transactions are **exclusive per plugin id across hot-reload generations** (so two Agents generations never double-charge a call); `UpdateSession`'s `mutate` keeps today's semantics (it runs inside the lock), calling a plugin store inside it is forbidden, and the one existing violation (`Ledger.Allow`, idea-7v22l8) is fixed. The conformance suite pins all of it.
- **`IPluginData` (`ctx.Data`)** replaces raw `IDatabase`: typed collections carried as **JSON documents plus declared index fields**, keyed get/put/delete, equality/range/IN filters on indexed fields, ordering and limit, **compare-and-set**, and the per-plugin transactions above. No group-by (the ledger keeps its roll-up by upsert). Plugin types never cross the port, or a provider would pin a plugin's load context and block unloading. Typing is a helper on the plugin side.
- **`IStorageSnapshot`**: the provider snapshots itself; Backup uses it instead of `VACUUM INTO`, and the restore script dispatches on the manifest's provider.
- **Selection fails closed**: a missing or broken provider stops startup with a clear error; `memory` runs only when asked for (`--ephemeral`), because running the owner's NetPI ephemeral after a broken install would lose every chat **(review)**.
- **The sqlite provider** is today's code moved behind these interfaces. It keeps today's core tables (the 200 MB of messages does not move) and writes its own SQL. It has no knowledge of any other dialect.

### 2. Projects and workspaces leave the kernel

One plugin, Workspaces, owns **where a session works**: the `projects` and `workspaces` tables and their CRUD move into it as collections. A session keeps an **opaque `projectId` and `workspaceId`** that the kernel stores and does not interpret (like `model`); a session list can filter on `projectId` as plain equality. The `projects.*`, `workspaces.*`, `sessions.setProject`, `sessions.setWorkspace` and `fs.dirs` RPCs and the `project.*`, `workspace.*`, `session.project` and `session.workspace` events keep their names and shapes, now registered and published by the plugin, so the UI sees no difference. What the kernel did on the way, the plugin does on the events it already publishes: a project's last-used time follows `session.created`, deleting a project clears `projectId` on its sessions, and a session's working directory (`GetCwd`, 10 sites in 6 plugins) is answered by `IWorkspaceResolver`: workspace root, else project folder, else the default folder (`workspace.default`, now the plugin's setting, in the same settings group). The tool plugins that need a folder fall back to the home folder's `workspace` when no resolver is registered, as the default does today. Both are extracted from `SessionStore` before the repository is written, so the repository is never designed around them and then stripped **(review)**.

### 3. Budget leaves the kernel

The core stops knowing about money. Three things go, and nothing replaces them in the core:

- **The check.** Delete the `IBudgetGate` test in `ModelCatalog` and the marker interface. The ledger, caps and reservations stay in the Agents plugin as a normal model middleware (it already is one). The kernel keeps `IsLocal` (a fact about a provider, used to pick a default model and by the resource leases) and the generic middleware pipeline; `Usage.CostUsd` stays too, as a figure a provider reports, like token counts. Trade-off, stated plainly: if Agents is absent or fails to load, paid calls are not metered or refused by anything. That is the decision: the core does not police it, and there is no Diagnostics warning either. Paid providers already need an API key you set. Decide's direct calls to the model server stay outside the catalog, as they are today.
- **The exception.** `BudgetExceededException` becomes a neutral contract type, `CallRefusedException { Kind, CanOverride }`: a scheduler or a middleware refused a run or a call. Runtime and the agent tools catch it without knowing why; the Agents plugin sets `Kind = "budget"`, and Runtime puts `Kind` in the notice it appends, so the notice the UI receives is the same ("budget", `canOverride`). The agent-slot snapshot (`AgentSlots`: price, spent today, daily limit) is the optional agent-scheduler contract, which the Host does not use; its fields are what `agents.list` sends, so it keeps them.
- **The fork key.** The "let this chat go over" allowance lives in the session's meta as `budgetAllowedFrom`, and the kernel's fork code has to know to drop it. Move it into the Agents plugin's own collection (keyed by session id, removed with the session), so the kernel's list no longer names it, a fork starts without an allowance by construction, and the one place that calls a plugin store inside `UpdateSession`'s `mutate` (`Ledger.Allow`, idea-7v22l8) is gone rather than patched. No key in the UI reads it (checked). Allowances already in meta are harmless leftovers: each is valid only for the period whose start it names.

**What the Agents plugin is, and why it does not move into a "models" plugin.** It is two features in one: a **scheduler** (named agents = a model plus instances and config, and capacity slots: `agents.*`, `AgentScheduler.cs`) and a **budget ledger** (`budget.*`, `usage.*`, `Ledger.cs`, `Reservations.cs`). Talking to a model needs only a **provider** (the "model plugin": one per backend), the catalog and **Runtime** (the loop). Runtime already looks the scheduler up optionally, so chat works without Agents today for local models; with the kernel check gone it works for paid models too. Nobody needs a second kind of models plugin. Splitting Agents into a scheduler plugin and a budget plugin (so you could have either alone) is a sensible later cleanup and is not needed here.

### 4. The core diet: nothing but core concepts

Audit of `src/NetPI.Host` and `src/NetPI.Abstractions` against the test in "What is in the core". The Host itself holds few plugin concepts (the tool registry, projects and workspaces, the lease registry, the prompt rule in the fork, seeded settings, and the budget check); it uses none of the agent-runtime, decision, deferred-tool or background-work contracts, which the contract assembly carries all the same. All of it goes out the same way: the plugin that owns the concept registers the same RPCs, events and settings, so the wire is unchanged.

| Today | After |
|---|---|
| The contract assembly holds every plugin-to-plugin vocabulary: `Agents.cs` (runtime, hooks), `AgentSlots.cs`, `Workspaces.cs`, `Context.cs`, `SessionPrompt`, `SessionModel` (it resolves an agent's model and reads `agents.<id>.model`), `SessionProfile`/`SessionTools`/`SessionIdentity`/`SessionAgent`, `Decisions`, `DecisionHints`, `DeferredTools`, `BackgroundWork`, `ResourceLeases`, the executable tool contract | A second assembly, **`NetPI.Contracts`**, shared like Abstractions (loaded once into the default context from `<app>/contracts/`; changing it needs a restart, as Abstractions does). The types keep `namespace NetPI`, so no source line changes, only project references. The **Host project cannot reference it**, so the core staying small is enforced by the compiler. A plugin that speaks none of these references only Abstractions. |
| `ToolRegistry`, `tools.list`, the `tools.disabled` setting, `ctx.Tools` (29 sites, 19 plugins) | New plugin **`NetPI.Tools`**. A tool is a contribution: its plugin registers `ToolRegistration` (tool, plugin id, priority) as a service, and the Tools plugin derives the registry, the sort by name, the disable list and `tools.changed` from `GetAll`, so a reload of the registry plugin loses nothing. `ctx.Tools` leaves `IPluginContext`. With it absent, chat works without tools. |
| `ResourceLeases` registry in the kernel (`HostKernel.cs:152`) | The Agents plugin, next to the scheduler that uses it. Its other consumers already treat it as optional. |
| `IBudgetGate` check, `BudgetExceededException`, `budgetAllowedFrom` | Section 3. |
| Fork: `SessionFork.RunState` names `goal`, `todo`, `guardrailsAllowed`, `agentId`… and calls `SessionPrompt.Fork` | The kernel clones the meta and asks every registered **`ISessionForkFilter`** (a service, `GetAll`) to adjust the copy; each owner drops its own keys and Context applies the prompt-fork rule. The kernel keeps what is its own: title, `forkedFrom`, the message copy, compaction as of the fork point. Guardrails stamps its allowance with its session id so a fork made while Guardrails is not loaded cannot inherit approvals (the UI does not read that key). |
| Host-seeded defaults for plugin settings (`providers.aiproxy`, `providers.anthropic`, `compaction`, `nudge`, `retry`, `tools.disabled`) | The owning plugin's schema defaults. The core keeps `plugins`, `server`, `ui`, `logging`, `defaultModel`, `models.refreshSeconds`. |
| `NetPiPaths.GlobalAgentsMd`, `NetPiPaths.DefaultWorkspace` | AgentsMd computes its own path; Workspaces owns the default folder. |
| `EventTypes`: workspace, project, agent, tool, process and usage names | Constants live with their contract; the core keeps session, message, stream, models, plugins, settings and ui. |
| `SessionInfo.Kind` "chat \| subagent" and `IncludeSubagents` | The kernel stores `kind` as an opaque string and lists `chat` unless asked (the parameter name is unchanged on the wire). |

`IPluginContext` after: `PluginId`, `PluginDirectory`, `Paths`, `Logger`, `Events`, `Services`, `Rpc`, `Ui`, `Http`, `Settings`, `Data`, `Sessions`, `Models`, `Stopping`, `Track`. The core's RPC list shrinks to `app.info`, `sessions.*` (list, create, fork, get, update, delete, messages), `models.list`, `ui.*`, `plugins.*`, `settings.*`, `rpc.list`, `services.list`, `events.*`, `logs.recent`.

One visible edge, said plainly: "Default working folder" is registered by the Workspaces plugin into the same General group, so it should not move in the settings dialog; the UI mock walkthrough checks it.

### 5. Test infrastructure follows

`TestHost` runs the real session service over `memory`; `FakeSessionStore`, `NullDatabase` and the duplicated catalog fake behaviour are deleted; Host tests build providers through a factory instead of `new Database` (13 sites) **(review)**.

## What gets written

The order of **writing**, so each layer compiles against the one below. There is no UI step.

1. **Contracts:** first the split of section 4: the plugin vocabularies move into the new `NetPI.Contracts` assembly (a move, no namespace change), leaving Abstractions as the core only; then the storage interfaces and `ctx.Data`, and `ISessionForkFilter`. Delete `IDatabase`, `Migrate(DDL)`, `IBudgetGate`, `NetPiPaths.DatabaseFile`, `GlobalAgentsMd`, `DefaultWorkspace`, the project, workspace and cwd methods of `ISessionStore` and `IWorkspaceStore`, and the `ISessionStore` shims (`ForkSession`, `SessionIdsUsingWorkspace`); replace `BudgetExceededException` with `CallRefusedException`. `IPluginContext` loses `Db` and `Tools`, gains `Data`.
2. **Providers and the conformance suite:** `sqlite` (today's SQL moved) and `memory`, and the suite, written together with the port. It pins: id and seq allocation (message ids are global), search semantics (`LIKE ... ESCAPE`, ASCII case folding), list order (pinned, `updated_at`, id), cascades, fork-at-seq, the lock rules above, collection semantics (CAS, index filters, ordering), snapshot round trip, a stress test for the deadlocks the old code fixed, and unload of a plugin's load context. This suite is the contract a future MSSQL provider must pass; it is what makes writing one realistic.
3. **Internal checkpoint** (below), right after step 2 and the core of the session service.
4. **Kernel:** provider loading, the session service over the repository with opaque `projectId` and `workspaceId`, `ISessionForkFilter` in the fork, the shared-contracts loader (`<app>/contracts/`), the catalog without the budget check; the tool registry, the resource-lease registry, the seeded plugin settings and the plugin events removed from the Host; `app.info` keeps its fields (the sqlite provider reports its version, `memory` reports none).
5. **Plugins that change:** Agents (ledger, reservations and the allowance onto `ctx.Data`; its middleware unchanged; drop the memory-charges fallback; takes the resource-lease registry), Runtime (agent records), Context (prompts; a fork filter that applies the prompt-fork rule), Ideas (its 8 tables; delete the finished legacy cutover, idea-2iz2bu), **Workspaces** (projects and workspaces as collections; their RPCs, events, `fs.dirs`, `workspace.default` and the cwd resolver), Backup (snapshots), Diagnostics (storage info), Tools.Agents (catches the neutral refusal), Guardrails (fork filter, session-stamped allowance), AgentsMd (its own path), the providers, Compaction, Nudge and Retry (their settings defaults), and the new **Tools** plugin (the registry). **Every other plugin changes only its project references and `ctx.Tools.Register` → registering a `ToolRegistration` service**: mechanical, and the compiler finds every one.
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
1. The solution builds with zero warnings, and `NetPI.Host` builds with a reference to `NetPI.Abstractions` only.
2. The conformance suite on sqlite and memory.
3. The five unit suites.
4. The independence matrix: all 35 boots plus the new Tools plugin removed (chat without tools) and Workspaces removed (file and shell tools fall back to the home folder), plus a paid provider working with Agents removed.
5. The whole e2e suite.
6. The mock UI walkthrough and the UI e2e **unmodified** against the new kernel.
7. A cutover rehearsal on a copy of your data.

**Done means:** all green; **`git diff` shows nothing under `web/` and nothing in the wire protocol**; the rehearsal verified (row counts and message checksums equal before and after; per-collection document counts and ledger totals match; the migrated schema equals a fresh install's); zero skipped tests (idea-r4e8rf); the core-size checks pass (rule 6: the Host references only Abstractions, and a search of `src/` for budget, tool registry, project, workspace, prompt, agent, lease and decision names finds only a session's opaque `projectId` and `workspaceId`); and the deletion checklist empty: `IDatabase`, `ctx.Db`, `ctx.Tools`, `Migrate(`, `IBudgetGate`, `BudgetExceededException`, `SessionFork.RunState`, the Host's `ToolRegistry` and `ResourceLeases`, the seeded plugin settings, `IWorkspaceStore`, the project and cwd methods of `ISessionStore`, `ForkSession`, `SessionIdsUsingWorkspace`, `NetPiPaths.DatabaseFile`, every `_dbReady`/`_memoryCharges` fallback, `FakeSessionStore`, `NullDatabase`, `new Database` in tests.

## Cutover and rollback (your local setup)

One-off tool `scripts/migrations/001-storage-port`: a C# console referencing the sqlite provider (no script reads SQLite today, and a second copy of the DDL would drift). Dry-run by default; deleted once applied. `--apply` refuses while NetPI is running and refuses if the new collections already exist. It converts the plugin tables (Ideas, ledger, Context, Runtime) from rows into documents, moves the `projects` and `workspaces` tables into the Workspaces collections (`sessions.project_id` and `sessions.workspace_id` stay as opaque columns), and drops the dead tables and their `Migrate` version rows. The core tables (sessions, messages, kv) do not move. `settings.json` keeps every key: only who declares a default changes.

1. **Rehearse on a copy** of your data (needs your go): the same tool, the new build booted against the result, a chat, a backup and a restore.
2. **Copy the whole current app folder** aside: the installer deletes `.old` at every start, so the old build is not kept automatically **(review)**.
3. **Close NetPI, then snapshot** (`netpi.db`, `-wal`, `-shm`, `settings.json`, `idea-images/`): a snapshot taken before closing loses what is written in between **(review)**.
4. `build.ps1 -Publish -NextStart` the new build, `--apply`, start it, check a chat, ideas and a backup.
5. **Rollback:** close NetPI, restore the snapshot and the copied app folder. Old backups are restorable only by the old build; `BACKUPS.md` says so.

## Decisions

Decided by the owner (2026-10-02):
- **Plugin data as documents in named collections** (`ctx.Data`, above). The checkpoint still proves it on Ideas and the ledger before the rest is built; SQL in the plugins is the fallback only if it fails.
- **The core is as small as possible** (rule 6): it knows its abstractions and no plugin's concept. Budget leaves with nothing replacing the refusal, not even a Diagnostics warning (section 3).
- Applied by me under that rule, for you to veto: **projects** and **the tool registry** are plugin concepts too (a chat runs without them), so they leave with workspaces (sections 2 and 4). If you would rather keep either in the core, say so; it changes only those two rows and their moves.
- **Permission granted** to copy `netpi.db` and `settings.json` read-only for the rehearsal. Nothing else under `%USERPROFILE%\.netpi` is touched, and the running NetPI is not stopped without asking.
- **Freeze** the Host, the contract assembly and the plugins this change touches on master for the duration: nothing else is working on them.

Plain-language version of the first: the four plugins that keep their own data (Ideas, Agents' ledger, Context, Runtime) stop writing SQL and instead save, load and look up records by name and a few indexed fields, so any storage engine can hold them.

## Notes for a future MSSQL provider (not built now)

It implements `IStorageProvider` with its own SQL and passes the conformance suite. It would ship as an optional package under `<app>/storage/<id>/`, which needs the installer (`PendingBuild`, `build.ps1`, `build.sh`) to learn to install provider folders (today only plugins and `wwwroot`): build that when the first non-built-in provider exists. It also needs an exclusive database instance lock at startup so two homes cannot share one database (the file home lock does not cover that). Dialect traps found in today's core SQL, so they are written down once **(review)**: Oracle-style `''` = `NULL` hits `sessions.title` and `kv`; `INSERT … SELECT MAX+1 … RETURNING` and `WITH RECURSIVE … DELETE` have no portable form; `LIMIT -1`; `INSERT OR IGNORE`; case-insensitive default collations change key equality and ordering; `kv.key` is a reserved word on SQL Server; DDL commits implicitly on Oracle; large text columns cost on `messages.parts`.

## Not in this change

Splitting the Agents plugin into a scheduler plugin and a budget plugin; turning the contract statics into services (the statics only move to `NetPI.Contracts`); splitting `NetPI.Contracts` per plugin; normalised tool arguments and guards by role; one owner per RPC and an error taxonomy; the session `meta` bag as typed data; UI slots and the shared Svelte runtime; typed decision contracts; the two small silent-failure fixes (`agent_spawn isolated` with no Workspaces, a throwing before-tool-call hook); SQL Server, Oracle or any other engine; a SQL-translation layer; hot-swapping storage at runtime; touching `ISettings`. Each can be taken later; none is needed for storage to be swappable.
