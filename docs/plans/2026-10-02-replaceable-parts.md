# A clean kernel: replaceable storage, features as plugins

Date: 2026-10-02
Status: proposed (nothing implemented). Review snapshot: master at 59619d2. Independently reviewed the same day by one Opus pass (52 tool calls, read-only); its findings are folded in below and marked **(review)**.
Builds on: [plugin independence](2026-10-01-plugin-independence.md) (plugin ↔ plugin, done). This plan covers what that plan did not: the parts the **kernel** owns or enforces.

## Ground rules (decided 2026-10-02)

NetPI runs on one machine and nothing outside this repository depends on it. Therefore:

1. **Contracts may change whatever the design needs.** No additive-only rule, no compatibility shims, deprecated paths, mirrors, dual-writes or forwarders. A contract change updates every plugin, test and doc in the same change. (`AGENTS.md` and `HANDOFF.md` say so as of this branch.)
2. **Delete what you replace, in the same stream.** A stream is not done while the old path still exists. This includes the shims that exist today (`ISessionStore.ForkSession`, `SessionIdsUsingWorkspace`) **(review)**.
3. **Only the owner's local setup is migrated**, by a one-off script run once, after a snapshot, with the owner's say-so (nothing in `%USERPROFILE%\.netpi` is touched without it). Migration code never ships in the product and is deleted once applied.
4. The principle: a part that is replaceable in principle (storage is the example) must be replaceable in practice, and no specific plugin is required. The kernel is a small mechanism; every feature is a part.

## What was measured (2026-10-02)

- **35 real servers** were started in throwaway homes (all plugins, each plugin removed in turn, host only, two minimal sets). Every one boots and answers. With **no plugins** the kernel starts in 0.5 s with 36 core RPCs and the UI loads without console errors; Runtime plus one provider is a working chat path. So plugin ↔ plugin independence is sound.
- Four code reviews looked at what a boot probe cannot see, and the Opus review spot-checked this plan's claims against the code: the cited lines and counts held (`HostKernel.cs:70,84`, `PluginContext.cs:41-42`, 156 `ctx.Sessions` / 21 `ctx.Db` / 2 registry lookups, `ModelCatalog.cs:150`, `RpcRegistry.cs:56`, the hard-coded fork keys `SessionFork.cs:21`). Corrections: raw-SQL call sites are ~102, not 83 (Ideas 72, Agents 12, Context 12, Runtime 5, Backup 1); `Normalize` has test callers too (`FakeCatalog`, `ModelCatalogTests`); the "13 argument readers" is unsettled (11 files read ad hoc plus `ToolArgs` classes in three plugins).

## What does not hold today

| # | Problem | Evidence |
|---|---|---|
| 1 | **Storage cannot be replaced.** Only the native SQLite library can be swapped. | The host builds the concrete `Database` and `SessionStore` before any plugin loads and hands plugins those objects. `IDatabase` is raw SQLite SQL (~102 sites in 5 plugins; `Migrate` takes DDL). Backup runs `VACUUM INTO`; `NetPiPaths.DatabaseFile` and `restore-backup.mjs` (SQLite header check) put SQLite in the contract. A plugin registering its own `ISessionStore` is ignored by almost everything: two stores diverge. |
| 2 | **Paid models need the Agents plugin.** | `ModelCatalog.cs:150` refuses non-local models unless something registers the empty marker `IBudgetGate`; only Agents does. And Decide posts straight to the model server with the caller's model id: no gate, no ledger (`DecidePlugin.cs:84-168`) **(review)**. |
| 3 | **Workspaces are kernel data**: table, `sessions.workspace_id`, RPCs, events; `SessionStore` also implements `IWorkspaceStore`. | Two sources of truth (`meta.workspaceId` vs the column). Without the plugin, `agent_spawn isolated` is silently ignored. |
| 4 | **Policy logic lives in the contract assembly** (`ModelMessages.Normalize`, `WorkspacePaths`, `DecisionHints`, `ToolSelection`, `ToolLists`, …). | Contract changes stage every plugin for a restart; a replacement still gets the old policy. |
| 5 | **Tool arguments are read in many places** and guards match tool names. | A guard can judge a different call than the one that runs (the bypass fixed in 9efaf19); an unknown write/exec tool is unguarded; a throwing before-tool-call hook lets the call run. |
| 6 | **A replacement can lose silently.** RPCs resolve to the last registrant, services by priority. | `RpcRegistry.cs:56` |
| 7 | **The UI shell hard-wires 11 plugins' features** (22% of `web/src`), has no slot extension points, absence handling is accidental. | |
| 8 | Smaller: fork policy hard-codes other plugins' meta keys; the host seeds settings for plugins that may not exist; `IHttpRegistry` is unused in the product; "typed" capabilities are JSON with an undocumented schema. | |

## Target architecture

### 1. The kernel is mechanism; everything else is a part

**Kernel:** plugin host and lifecycle, the registries (services, tools, RPC, UI, events), transport and auth, settings (the bootstrap root), logging, paths and the home lock, the **model catalog**, and the **session domain service** over a storage port. **Parts:** every feature, including storage providers, model providers, the agent loop, workspaces, budget, ideas, tools. An operation that needs an absent part reports it as unavailable by name.

**The core concepts** (owner, 2026-10-02: "sessions and models are core concepts") are the ones every part speaks in: **sessions, messages and projects** (the chat domain), **models** (the catalog, the provider and middleware contracts, metering), **tools** (definition, arguments, results), **settings**, **events**, and the **storage port** beneath all of them. They are kernel-provided, always present, and reached as `ctx.Sessions` and `ctx.Models`. What you *choose* is what implements them (the storage engine, the model providers), never whether they exist. Considered and left out of the core, on purpose: the agent loop (Runtime: another executor must be possible), workspaces (a feature), the budget ledger (only its *contract*, §6, is core), prompt assembly (Context; only `ISystemPromptBuilder` is core), compaction, ideas, every tool.

### 2. The plugin context: mechanism plus the kernel's own services

`IPluginContext` loses `Db` (when `IDatabase` goes, §3) and `Http` (unused). It **keeps** `Sessions` (typed as the session service) and `Models`: they are kernel-owned and always present, and removing them buys no replaceability, since what is replaceable is *beneath* the session service **(review)**. It **gains** `Data`: a plugin-scoped handle to its own collections, because a shared `Services.Get<IPluginData>()` cannot know which plugin is asking **(review)**. `Track<T>` stays. Everything plugin-provided is a service resolved per use, with `Services.Require<T>()` throwing one uniform `CapabilityUnavailable(contract)` that tools, RPCs and the UI map to the same explicit "unavailable: X". (Decided, D4: `Sessions` and `Models` stay, because they are core concepts. Removing them would have cost ~190 plugin sites, 115 test and 29 `src` uses and five `IPluginContext` test implementations for no replaceability.)

### 3. Storage is a port with providers

```
IStorageProvider  { Id; OpenAsync(StorageContext) → IStorage }       chosen by `storage.provider`, at startup only
IStorage          { Sessions: ISessionRepository, Kv: IKeyValueStore, Data: IPluginData, Blobs: IBlobStore,
                    Snapshot: IStorageSnapshot, Lock, Info }
```

**The concurrency contract is the real contract (review).** Today one re-entrant lock (`Database.Gate`) is both the deadlock fix for sessions (`SessionStore.cs:107-115,142`: the transient-session lock *is* the gate) and the budget ledger's mutex across hot-reload generations (`Reservations.cs:118-147`), and the stall watchdog watches it. The port must state, and the conformance suite must pin:
- one **re-entrant provider lock**, exposed to the kernel (transient map, watchdog);
- plugin transactions are **exclusive per plugin id across hot-reload generations** (so two Agents generations never double-charge a call);
- a rule for calling another store inside a transaction (a plugin-store call inside a session `mutate` is forbidden), and `UpdateSession`'s `mutate` runs on a copy with an optimistic version check, or is declared pure.

**`ISessionRepository`** is primitives plus one `Atomic(Func<tx,T>)` that runs on the provider lock, about 20-25 methods, **not** "15 methods and no behaviour" **(review)**: several operations are atomic across tables (materialising the first message touches the session row, the project's `last_used_at` and the message; append assigns seq as `MAX+1` with counters and auto-title; project delete detaches its sessions; session-tree delete is a recursive query; fork copies messages and recomputes compaction as of the fork point). Titles, compaction-as-of-fork and id remapping stay in `SessionService`, with events, transient sessions and the context cache. The conformance suite pins id and seq allocation (message ids are global), search semantics (`LIKE ... ESCAPE`, ASCII case folding), list order (pinned, `updated_at`, id) and cascades.

**Providers and selection.** `sqlite` (today's code, moved into its own assembly inside the app, default) and `memory`. **The kernel never falls back silently:** `memory` runs only when asked for (`--ephemeral` or `storage.provider: memory`); with a missing or broken provider it refuses to start with a clear error, because running the owner's NetPI ephemeral after a broken install would lose every chat at restart **(review)**. Out-of-tree providers are deferred: `PendingBuild.cs:19-30` installs only `.pending/plugins` and `.pending/wwwroot`, so a provider loaded at startup needs staging support in `build.ps1`, `build.sh` and `PendingBuild` first (idea-u29mux).

**Choosing an engine (sqlite, SQL Server, Oracle, …).** A relational engine is a *provider*, selected by `storage.provider` and configured under `storage.<id>` (connection string, schema). The SQL providers share **one implementation**: a SQL-family base with a small dialect adapter per engine (quoting, upsert syntax, identity or sequence, pagination, text and JSON column types, case-insensitive compare, booleans, and how the provider lock is taken: a plain lock on SQLite, `sp_getapplock` on SQL Server, `DBMS_LOCK` on Oracle). Dialect then lives in **one place** instead of ~100 call sites in five plugins. The core tables (sessions, messages, projects, kv) are mapped relationally; each plugin collection is a table with a key, one opaque JSON document column and one column per declared index field, so no engine-specific JSON feature is needed. Known traps the base must absorb: Oracle treats `''` as `NULL` (empty strings are normalised at the port, never stored in indexed fields), Oracle and SQL Server differ on identity, `MERGE` versus `ON CONFLICT`, `OFFSET … FETCH` versus `LIMIT`, `CLOB`/`NVARCHAR(MAX)` limits, and identifier-length limits.
**Drivers are NuGet packages** (`Microsoft.Data.SqlClient`, `Oracle.ManagedDataAccess.Core`), so each engine ships as an **optional provider package** next to the app (`<app>/storage/<id>/`), never in the core (the project asks before adding a package: this is that ask, once per engine, D9). That makes provider staging in `PendingBuild`/`build.ps1`/`build.sh` a prerequisite for any engine that is not built in. The conformance suite runs against every provider; a server engine runs it only when its connection string is configured (`NETPI_TEST_MSSQL`, …), so the merge gate never needs a server. A remote engine makes the single provider lock and the per-append round trips (an append is ~4 statements) matter: fine against a local server, to be measured before anyone points it at a remote one.

**Plugin data (`IPluginData`, via `ctx.Data`)** replaces raw `IDatabase`/`Migrate(DDL)` (D1). The port takes **JSON documents plus declared index fields** (typing is a helper on the plugin side): a plugin type must never reach the provider, or a non-collectible provider pins the plugin's load context and blocks unloading **(review)**. Operations: keyed get/put/delete, equality/range/IN filters on indexed fields, ordering and limit, **compare-and-set** (on a revision or token), and the exclusive per-plugin transactions above. **No group-by in the port**: the ledger already keeps its roll-up by upsert (`lanes_usage`, `Ledger.cs:504-508`). Overlapping generations may declare different indexes for one collection: the port defines that (additive indexes only, applied by the new generation). Ideas, the ledger, Context, Runtime's agent store and Workspaces are rewritten on it one plugin per commit, **each deleting its SQL and its "database unavailable" fallbacks** (`_dbReady`, `_memoryCharges`); at the end `IDatabase` and `ctx.Db` are gone.

**Backup** becomes `IStorageSnapshot` plus plugin file contributors; the restore script dispatches on the manifest's provider (it hard-requires `netpi.db` and the SQLite header today). Idea images move to `IBlobStore`. `NetPiPaths.DatabaseFile` leaves the contract.

### 4. The contract assembly is interfaces and plain data only

Policy moves to its owner, no forwarders, one commit each: `ModelMessages.Normalize` → the model catalog (and its test callers); `WorkspacePaths.CheckMutation/Refusal` → the Workspaces plugin as `IWorkspaceGuard`; `DecisionHints` and the JSON `IDecisionService`/`IGitHistory` bodies → typed records owned by the Decide and Files contracts; `ToolSelection` (with the Agents depth rule), `ToolLists`, `DeferredTools` → the Runtime/tool-selection service; `SessionModel` stops reading `agents.<id>.model`; `ResourceLeases` keeps its interface only. Constants shared by two plugins become named contract constants.

### 5. Tool arguments are normalised once; guards read roles, not names

`ToolDefinition.Parameters` is the single source of truth: name, aliases, type, required, a **role** (`path`, `command`, `cwd`, `url`, …) and `Effect` (`Read|Write|Exec|Net`). Details the review found **(review)**:
- **One point:** after gateway resolution and before hooks (`AgentRunner.cs:881-895`). Hooks see the resolved call.
- **`ToolArguments` is a derived view.** Hooks may rewrite arguments today and the runner writes the rewrite into the stored assistant message (`UpdateMessage`, dropping the context cache), so the normalised view must never be persisted and never set the "arguments changed" flag (the "never rewrite what was sent" cache rule).
- **Action-union tools** (`ssh`: hosts/run/read/write/edit/copy + `direction`, aliases `exec→run`, `scp→copy`; `process`; `agent`): `Effect` depends on the action, and roles carry **locality** (local vs remote path), so an ssh download's destination is a local write (idea-8e2rpt).
- **MCP:** map the tool annotations (`readOnlyHint`, …) into `Effect`, or `guardrails.unknownTools: ask` would ask for every MCP call.
- A throwing **before-tool-call** hook blocks the call (D7). This applies to `OnBeforeToolCall` only: `SafeAsync` is shared by every hook.
Guards act on `Effect` and roles; a tool that declares nothing and is not read-only follows `guardrails.unknownTools` (`allow` | `ask`). Roles do not fix idea-wc0kuu (command decoration) or idea-6k4v5e (path canonicalisation); those stay separate.

### 6. Money: every paid call is metered, as a real contract

Delete the empty `IBudgetGate` marker. `IModelMiddleware` gains `MetersPaidCalls`; the catalog refuses a paid model unless a registered middleware meters, or `models.allowUnmetered` is set (explicit, and shown in diagnostics). **Every paid call goes through `IModelCatalog`**: Decide's direct HTTP either refuses non-local models or is metered **(review; whether AiProxy forwards paid models is unverified)**.

### 7. One owner per RPC, one error taxonomy

Each RPC method has one owning plugin; a second registration needs a priority and the loser is logged; `rpc.list` shows owner and priority. Error codes: `unknown_method`, `unavailable` (naming the capability), `not_found` (an entity). Code that matches today's `not_found` or message text changes with it: `web/mock` (`agent.mjs`, `ideas.mjs`, `diag.mjs`), `openFile.js:13`, `IdeaCards.svelte:55`, `hasRpc` in `pluginCtx.js` **(review)**.

### 8. Session-scoped plugin data replaces the meta bag

The shared untyped `meta` JSON and the host's hard-coded fork-exclusion list are replaced by a small typed core (`model`, `title`, `project`, identity, tools-off, prompt revision: the genuinely shared data) and **plugin-owned per-session data** with `ISessionLifecycle` hooks (created, forked, deleted, and **materialized/discarded**, because an abandoned transient session vanishes at restart with no event and its plugin data would be orphaned **(review)**). **Workspaces** become one such plugin; its binding on a transient session (`SessionStore.cs:579-589`) is covered by the same hooks. Workspaces are extracted from `SessionStore` into a separate kernel class *first*, so the repository is never designed around them and then stripped **(review)**.

### 9. The UI shell is a slot host

The shell owns layout, the chat transcript and slots (`composer.dock`, `composer.bar`, `topbar`, `settings.page`, `chat.row:<kind>`), plus a renderer registry for tool results and notices (`tools.list` carries `summaryArg`, `icon`, `view`; `details.blocked` replaces the `Blocked:` text prefix). It also keeps **one generic renderer for unknown row kinds and tool results**: stored transcripts outlive plugins, so this is not a feature fallback **(review)**. Features are delivered by their plugins and the built-in copy is deleted in the same change. Prerequisite: plugin bundles share the host's Svelte runtime (existing idea). `hello` carries a protocol version.

### 10. Settings: plugins own their defaults

The host seeds nothing for plugins; every plugin declares its schema and defaults. Keys may be renamed freely (the local file is migrated).

## Decisions

| | Question | Recommendation |
|---|---|---|
| D1 | Plugin data: typed portable collections, or SQL kept as a declared optional capability? | **Collections as JSON documents + index fields + keyed ops + compare-and-set + per-plugin transactions exclusive across generations; no group-by.** Gated by SP1 with the criteria below. If SP1 fails, plugin data stays SQL-as-capability and "replaceable" covers the core (sessions, messages, projects, kv) only. |
| D2 | Session domain service in the kernel over a repository port? | **Yes**, with the repository exposing an atomic unit and the lock. |
| D3 | Money guard in the kernel with an explicit opt-out? | **Yes**, including the Decide path; `allowUnmetered` visible in diagnostics. |
| D4 | Remove `Sessions` and `Models` from the context? | **Decided: no.** Sessions and models are core concepts (§1). The context keeps them, loses `Db` and `Http`, and gains `ctx.Data`. |
| D5 | Normalised arguments with roles and `Effect`? | **Yes**, with the four details in §5. Idea-cy75a1 is this stream. |
| D6 | UI as a slot host with no feature fallbacks? | **Yes**, after SP2, plus the generic unknown-kind renderer. |
| D7 | A throwing before-tool-call hook blocks the call? | **Yes**, `OnBeforeToolCall` only. |
| D8 | Freeze windows? | **Only for a change to `IPluginContext`.** With the old "flag day" split up, nothing else needs one. |
| D9 | Which engines, in which order, and may each ship as an optional provider package with its own NuGet driver? | **Order: sqlite (exists) → memory → the SQL-family base → SQL Server → Oracle.** One NuGet per engine, only inside that engine's optional package (`Microsoft.Data.SqlClient`, `Oracle.ManagedDataAccess.Core`), never in the core. Needs from you: a go per engine for the package, and a test server for each (a local SQL Server and Oracle XE in containers are enough). |

## How: order

**No big-bang change.** Each contract change lands alone, touching only the plugins it affects, with the whole e2e suite (a contract change stages every plugin for a restart). Only plugin-internal work runs in parallel.

**Stage A: each lands alone.**
- P1 `scripts/land.ps1 <branch>`: refuse if the main tree is dirty or the branch is not a fast-forward, then `git -C <main> merge --ff-only`. Ends the stale-main-checkout problem (idea-kcyxh2).
- P2 **Independence gate v2**: the 35-boot matrix as `scripts/independence.mjs` (on demand) plus a ~30 s subset in the merge gate (host only, Runtime+provider, −Runtime, −Agents, −Workspaces, −Context, −Ideas), with probes the startup check lacks: a paid call with no metering, a spawn with isolation and no Workspaces, one real-host UI run with Runtime + one provider only. Note idea-r4e8rf: skipped tests count as passes today, which weakens every "tests pass" gate below.
- P3 **One-off migration convention**: `scripts/migrations/NNN-name.mjs`, dry-run by default, `--apply` takes a snapshot first, verifies after, refuses while NetPI is running; deleted once applied.
- P4 The silent failures: `agent_spawn isolated` with no provisioner returns an explicit error; a throwing before-tool-call hook blocks.
- P5 **§6 metering** including Decide, so "no specific plugin is required" is true for money.
- P6 Do **idea-2iz2bu** (delete Ideas' ~930 lines of finished legacy cutover) before Ideas is ported.

**Stage B: SP1** (below). Decides D1.

**Stage C: storage core. The principle is true at the end of this stage.**
- C1 `SessionService` + `ISessionRepository` inside the host, workspaces kept aside in their own kernel class; the existing session and workspace tests as characterisation (this class has a deadlock history: one extraction per commit, no logic changes, the whole Host and Agent suites per commit); delete the `ISessionStore` shims.
- C2 The `memory` provider and the conformance suite; the Agent test harness moves onto the real `SessionService` over `memory`, deleting `FakeSessionStore` (`Fakes.cs:253-433`) and `NullDatabase`; `storage.provider` at startup, fail-closed; `--ephemeral` through `NetPiServerOptions`, `Server/Program.cs` and the Desktop's argument parsing.
- C3 Plugin data, one plugin per commit: Runtime store, Context, the ledger, Ideas, then Workspaces (with its lifecycle hooks, §8), each deleting its SQL and fallbacks.
- C4 Delete `IDatabase`, `Migrate` and `ctx.Db`.
- C5 `IStorageSnapshot`, `IBlobStore`, Backup and the restore script.

**Stage D: independent streams, any order, one contract landing at a time.** D1 tools and guards (§5); D2 error taxonomy (§7); D3 UI slot host (§9, after SP2); D4 §8 beyond workspaces; D5 the §4 moves, one per commit; D6 settings defaults (§10).

**Stage E: engines you can choose (needs stage C; E1 can start as soon as C2 is in).**
- E1 **Provider staging:** `PendingBuild`, `build.ps1` and `build.sh` install `<app>/storage/<id>/` the way they install plugins (today only `.pending/plugins` and `.pending/wwwroot`); the loader reads `storage.provider` and loads that provider's assemblies at startup. Closes idea-u29mux's provider half.
- E2 **The SQL-family base** (§3): the `sqlite` provider is rebuilt on it with a dialect adapter, with the conformance suite unchanged and green. This proves the base before a second engine depends on it.
- E3 **SQL Server** provider: one optional package, one NuGet (D9), conformance run when `NETPI_TEST_MSSQL` is set, `docs/SETTINGS.md` and `BACKUPS.md` for `storage.mssql`.
- E4 **Oracle** provider, same shape (`NETPI_TEST_ORACLE`), with the `''`-is-`NULL` handling tested explicitly. Postgres or others later are one dialect adapter each.
- Each engine is chosen by `storage.provider`; switching engines on existing data is the offline export/import step in the migration table, never an in-product path.

## Spikes

**SP1: plugin-data port.** Prototype on SQLite and in memory and port the hardest flows. **Succeeds if, on both providers:** (1) two Agents generations reserving at once produce exactly one reservation per call and never exceed the budget; (2) a stress test that reads a session inside a plugin transaction and plugin data inside `mutate` does not deadlock; (3) Ideas' hard flows need no SQL (`ResolveCard` case-insensitive id or prefix, `ClaimCheck`/`FinishCheck` as a conditional upsert with a token compare-and-set, `ord` as MIN−1/MAX+1, the `NOT IN` cleanup) and the common paths do not scan whole collections on SQLite; (4) ledger totals (period, today, per lane) match today's SQL on a *copy* of the owner's data, and `Reserve`'s p95 stays within an agreed margin at 100k calls; (5) the plugin's load context is collected after unload; (6) the port has about 12 operations or fewer; (7) every operation is expressible without an engine-specific feature on SQLite, SQL Server and Oracle: write out the three dialect forms of the ledger's atomic reserve, Ideas' conditional upsert with a token compare-and-set, and the indexed filter with ordering and limit, and check them against the dialect list in §3. **Fails if** 1, 2 or 5 need per-provider special cases: then D1 falls back to SQL-as-capability.

**SP2: slot host.** **Succeeds if** the Goal strip mounts from the plugin's own bundle using the host's Svelte runtime with shared reactive state; a plugin reload remounts it with no page reload; with the plugin absent the slot is empty and the console is clean; a bundle with a mismatched protocol version is refused cleanly; and the bundle contains no Svelte runtime of its own. **Fails if** it needs a duplicated runtime or a host rebuild per plugin: then D6 is deferred.

## Also in scope (found by the review)

Provider installation (`PendingBuild`/`build.ps1`/`build.sh`); `restore-backup.mjs` and `NetPiPaths.DatabaseFile`; test infrastructure (`TestHost`, `FakePluginContext` and the Providers/Tools/Aux harnesses, `FakeSessionStore`, `NullDatabase`, `FakeCatalog`); plugins' own "database unavailable" paths; `--ephemeral` plumbing; UI code matching error codes and message text; schema evolution across overlapping generations. Backlog overlaps: idea-cy75a1 is §5; idea-7v22l8 and idea-hc8s8l belong inside SP1; idea-anhwjh is §3's snapshot; idea-c3hihl needs compare-and-set on revision; idea-yvcy8b and idea-aqfryg overlap §7; idea-g26991 overlaps §9; idea-l1o09d overlaps §8; idea-t6odez shapes the repository's message queries; idea-u29mux gates provider staging; idea-2iz2bu precedes porting Ideas.

## Local migration (the owner's setup only)

One-off scripts under `scripts/migrations/`, applied by the owner after a snapshot, deleted afterwards. Product code never reads the old shapes.

| Script | When | What |
|---|---|---|
| 001 settings | with D6 | rename moved keys, drop seeded blocks for plugins that now own their defaults. |
| 002 session meta | stage D4 | move plugin keys (goal, todo, budgetAllowedFrom, guardrailsAllowed, agent*, …) from `sessions.meta` into the owning plugins' collections. |
| 003 workspaces | C3 | the `workspaces` table and `sessions.workspace_id` into the Workspaces plugin's collection. |
| 004 plugin tables | C3 | Ideas, ledger, Context and Runtime tables into their collections (a no-op where the SQLite provider keeps the same tables). |
| 005 idea images | C5 | files under `idea-images/` into the blob store. |

The SQLite provider keeps the existing core tables unless there is a reason to change them. Old backups stay restorable only by checking out the version that wrote them; say so in `BACKUPS.md` when the manifest changes.

## Working with other agents

About 25 worktrees exist and review fixes land on master continuously (46 commits since 943011b). Only `netpi/fix-contracts-ui` (contracts + diag UI) and `netpi/fix-tools` carried unmerged work when this was written.
- Land contract changes one at a time by fast-forward and tell the other branches to rebase. A freeze is needed only for a change to `IPluginContext`.
- Inform the owners of `fix-workspaces`/`diag-work-workspaces` before C3's Workspaces step and of `fix-contracts-ui` before any contract change.
- Re-run the independence gate after every landing: another agent's merge can reintroduce an assumption.

## Risks

| Risk | Mitigation |
|---|---|
| The lock/transaction model is under-specified and a provider reintroduces the deadlocks or the double charge. | The concurrency contract in §3, pinned by the conformance suite and SP1 criteria 1-2. |
| `SessionService` extraction (C1) drops an event or deadlocks. | Characterise first; one extraction per commit; no logic changes; whole Host and Agent suites and the whole e2e per commit. |
| A broken install runs the owner's NetPI ephemeral. | `memory` only on request; otherwise refuse to start. |
| The live 200 MB database. | The sqlite provider keeps the core schema; a snapshot precedes every migration; tests only use temp homes; nothing runs without the owner. |
| The plugin-data port cannot express the ledger or Ideas cleanly. | SP1 decides before anything depends on it. |
| Rebase churn from many Abstractions landings. | One contract change at a time; only plugin-internal work in parallel. |
| A server engine's behaviour differs where SQLite's does not (Oracle's `''` is `NULL`, identity and upsert syntax, `CLOB` limits, identifier length, isolation levels). | Dialect lives only in the SQL-family adapters; empty strings are normalised at the port; the same conformance suite runs on every engine (opt-in connection strings); SP1 criterion 7 checks the port on paper first. |
| The provider lock over a server engine (`sp_getapplock`, `DBMS_LOCK`) or a remote link makes each append slow. | The lock contract (§3) is implemented per engine and pinned by conformance; measure appends against a local server before anyone uses a remote one. |
| Each engine's driver is a new dependency and its tests need a server. | Optional provider packages, one NuGet each with the owner's go (D9); server-engine tests run only when their connection string is set, so the merge gate needs no server. |

## Not doing

- A portable-SQL translation layer, an ORM or LINQ over a raw SQL interface.
- A storage provider on the normal hot-reload lifecycle, or swapping storage at runtime.
- A remote database before the single global lock is lifted.
- Touching `ISettings`: it stays the bootstrap root (plugin dirs, logging and the storage provider are read from it before anything else).
- Compatibility of any kind with the shapes this plan removes.
