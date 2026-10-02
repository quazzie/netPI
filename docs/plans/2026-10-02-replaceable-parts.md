# Platform v2: a swap-over to replaceable parts

Date: 2026-10-02
Status: proposed (nothing implemented). **One swap-over, not a staged programme**: all the code is written in one session and then a test/fix loop runs until the definition of done is met. Review snapshot: master at 59619d2. Independently reviewed the same day by one Opus pass (52 tool calls, read-only); its findings are folded in below and marked **(review)**.
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
- every provider has one **in-process re-entrant lock** and one dedicated connection (no pooling), exposed to the kernel (transient map, watchdog). On a server engine the engine additionally takes one exclusive **instance lock** at startup (a session-owned applock on SQL Server, `DBMS_LOCK` on Oracle, which needs an EXECUTE grant: fail closed without it), so two homes cannot share one database; the file-based home lock does not cover that;
- plugin transactions are **exclusive per plugin id across hot-reload generations** (so two Agents generations never double-charge a call);
- `UpdateSession`'s `mutate` runs on a copy **outside** the lock and commits against a new `sessions.version` column (added by the cutover), retrying up to three times; plugin calls inside `mutate` are therefore allowed, and the conformance suite tests exactly that.

**`ISessionRepository`** is primitives plus one `Atomic(Func<tx,T>)` that runs on the provider lock, about 20-25 methods, **not** "15 methods and no behaviour" **(review)**: several operations are atomic across tables (materialising the first message touches the session row, the project's `last_used_at` and the message; append assigns seq as `MAX+1` with counters and auto-title; project delete detaches its sessions; session-tree delete is a recursive query; fork copies messages and recomputes compaction as of the fork point). Titles, compaction-as-of-fork and id remapping stay in `SessionService`, with events, transient sessions and the context cache. The conformance suite pins id and seq allocation (message ids are global), search semantics (`LIKE ... ESCAPE`, ASCII case folding), list order (pinned, `updated_at`, id) and cascades.

**Providers and selection.** `sqlite` (today's code, moved into its own assembly inside the app, default) and `memory`. **The kernel never falls back silently:** `memory` runs only when asked for (`--ephemeral` or `storage.provider: memory`); with a missing or broken provider it refuses to start with a clear error, because running the owner's NetPI ephemeral after a broken install would lose every chat at restart **(review)**. Optional providers (SQL Server, Oracle, anything else) load at startup from `<app>/storage/<id>/`. `PendingBuild.cs:19-30` installs only `.pending/plugins` and `.pending/wwwroot` today, so `build.ps1`, `build.sh` and `PendingBuild` learn to install provider folders the same way (W6; closes idea-u29mux's provider half).

**Choosing an engine (sqlite, SQL Server, Oracle, …).** A relational engine is a *provider*, selected by `storage.provider` and configured under `storage.<id>` (connection string, schema). The SQL providers share **one implementation**: a SQL-family base with a small dialect adapter per engine (quoting, upsert syntax, identity or sequence, pagination, text and JSON column types, case-insensitive compare, booleans, and how the provider lock is taken: a plain lock on SQLite, `sp_getapplock` on SQL Server, `DBMS_LOCK` on Oracle). Dialect then lives in **one place** instead of ~100 call sites in five plugins. The core tables (sessions, messages, projects, kv) are mapped relationally; each plugin collection is a table with a key, one opaque JSON document column and one column per declared index field, so no engine-specific JSON feature is needed. Known traps the base must absorb: Oracle treats `''` as `NULL` (empty strings are normalised at the port, never stored in indexed fields), Oracle and SQL Server differ on identity, `MERGE` versus `ON CONFLICT`, `OFFSET … FETCH` versus `LIMIT`, `CLOB`/`NVARCHAR(MAX)` limits, and identifier-length limits.
**Drivers are NuGet packages** (`Microsoft.Data.SqlClient`, `Oracle.ManagedDataAccess.Core`), so each engine ships as an **optional provider package** next to the app (`<app>/storage/<id>/`), never in the core (the project asks before adding a package: this is that ask, once per engine, D9). That makes provider staging in `PendingBuild`/`build.ps1`/`build.sh` a prerequisite for any engine that is not built in. The conformance suite runs against every provider; a server engine runs it only when its connection string is configured (`NETPI_TEST_MSSQL`, …), so the merge gate never needs a server. A remote engine makes the single provider lock and the per-append round trips (an append is ~4 statements) matter: fine against a local server, to be measured before anyone points it at a remote one.

**Plugin data (`IPluginData`, via `ctx.Data`)** replaces raw `IDatabase`/`Migrate(DDL)` (D1). The port takes **JSON documents plus declared index fields** (typing is a helper on the plugin side): a plugin type must never reach the provider, or a non-collectible provider pins the plugin's load context and blocks unloading **(review)**. Operations: keyed get/put/delete, equality/range/IN filters on indexed fields, ordering and limit, **compare-and-set** (on a revision or token), and the exclusive per-plugin transactions above. **No group-by in the port**: the ledger already keeps its roll-up by upsert (`lanes_usage`, `Ledger.cs:504-508`). Overlapping generations may declare different indexes for one collection: the port defines that (additive indexes only, applied by the new generation). Ideas, the ledger, Context, Runtime's agent store and Workspaces are rewritten on it one plugin per commit, **each deleting its SQL and its "database unavailable" fallbacks** (`_dbReady`, `_memoryCharges`); at the end `IDatabase` and `ctx.Db` are gone.

**Backup** becomes `IStorageSnapshot` plus plugin file contributors; the restore script dispatches on the manifest's provider (it hard-requires `netpi.db` and the SQLite header today). Idea images move to `IBlobStore`. `NetPiPaths.DatabaseFile` leaves the contract.

### 4. The contract assembly is interfaces and plain data only

Policy moves to its owner, no forwarders: `ModelMessages.Normalize` → the model catalog (and its test callers); `WorkspacePaths.CheckMutation/Refusal` → the Workspaces plugin as `IWorkspaceGuard`; `DecisionHints` and the JSON `IDecisionService`/`IGitHistory` bodies → typed records owned by the Decide and Files contracts; `ToolSelection` (with the Agents depth rule), `ToolLists`, `DeferredTools` → the Runtime/tool-selection service; `SessionModel` stops reading `agents.<id>.model`; `ResourceLeases` keeps its interface only. Constants shared by two plugins become named contract constants.

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

The shared untyped `meta` JSON and the host's hard-coded fork-exclusion list are replaced by a small typed core (`model`, `title`, `project`, `agentId`, `parentAgentId`, identity, tools-off, prompt revision: the genuinely shared data) and **plugin-owned per-session data** with `ISessionLifecycle` hooks (created, forked, deleted, and **materialized/discarded**, because an abandoned transient session vanishes at restart with no event and its plugin data would be orphaned **(review)**). **Workspaces** become one such plugin; its binding on a transient session (`SessionStore.cs:579-589`) is covered by the same hooks. Workspaces are extracted from `SessionStore` before the repository is written, so the repository is never designed around them and then stripped **(review)**. Cross-plugin readers go through the owner's service, resolved per use: Loops and Ideas read goal and todo from Goal and Todo, never from a shared key. `projects.meta` goes the same way: plugin-owned project data (Profiles' default profile lives in Profiles' own collection).

### 9. The UI shell is a slot host (the target; not part of this swap-over, D6)

The shell owns layout, the chat transcript and slots (`composer.dock`, `composer.bar`, `topbar`, `settings.page`, `chat.row:<kind>`), plus a renderer registry for tool results and notices (`tools.list` carries `summaryArg`, `icon`, `view`; `details.blocked` replaces the `Blocked:` text prefix). It also keeps **one generic renderer for unknown row kinds and tool results**: stored transcripts outlive plugins, so this is not a feature fallback **(review)**. Features are delivered by their plugins and the built-in copy is deleted in the same change. Prerequisite: plugin bundles share the host's Svelte runtime (existing idea). `hello` carries a protocol version.

### 10. Settings: plugins own their defaults

The host seeds nothing for plugins; every plugin declares its schema and defaults. Keys may be renamed freely (the local file is migrated).


## Decisions to fix before the session starts

There are no stages and no spike phase, so these are settled up front. Recommendations are what the review and the code support.

| | Question | Decision / recommendation |
|---|---|---|
| D1 | Plugin data: typed portable collections or SQL kept as a declared capability? | **Collections** (JSON documents + index fields + keyed ops + compare-and-set + per-plugin transactions exclusive across generations; no group-by). One **internal checkpoint** guards it (below): if it fails, the session switches plugin data to SQL-as-capability and carries on; "replaceable" then covers the core (sessions, messages, projects, kv) only, and plugin SQL (SQLite dialect) would report unavailable on every other engine. |
| D2 | Session domain service in the kernel over a repository port? | **Yes**, the repository exposing an atomic unit and the provider lock. |
| D3 | Money guard in the kernel with an explicit opt-out? | **Yes**: `MetersPaidCalls`, `models.allowUnmetered`, Decide included. |
| D4 | `ctx.Sessions` and `ctx.Models` stay? | **Decided: yes.** Core concepts. |
| D5 | Normalised tool arguments with roles and `Effect`? | **Yes** (§5). |
| D6 | The UI slot host and plugin bundles sharing the host's Svelte runtime: in this swap-over? | **Out.** The principle does not need it, it couples every plugin bundle to the host's Svelte version, and UI work is the slowest and flakiest part of the loop. The swap-over keeps the UI changes it needs (W5). The slot host follows on a stable platform. |
| D7 | A throwing before-tool-call hook blocks the call? | **Yes**, that hook type only. |
| D8 | In-flight work from the other agents. | **Freeze plugins, Abstractions and Host on master for the duration** (web and docs work may continue); land everything else first and branch from that master. No end-of-session merge: every plugin commit written against the old contracts would have to be ported again. |
| D9 | Engines in this swap-over. | **SQLite and memory for certain.** **SQL Server and Oracle** are in only if the loop has a server for each (a container, Oracle Free, or SQL Server LocalDB is enough); the SQL-family base is then extracted together with the first server engine. Without a server the swap-over ends with SQLite + memory, keeps all dialect-sensitive SQL inside one class of the sqlite provider and checks checkpoint criterion 7 on paper; each server engine is then a pure addition. One NuGet per engine, only inside that engine's optional package (`Microsoft.Data.SqlClient`, `Oracle.ManagedDataAccess.Core`); you approve each. |
| D10 | Permission to copy `%USERPROFILE%\.netpi\netpi.db` and `settings.json` to a scratch folder for the cutover rehearsal (read-only copy; the live home is never written). | **Needed**, per the standing rule about that folder. |

## The swap-over: what is written

One branch, one landing. The order below is the order of **writing**, so each layer compiles against the one beneath it; it is not a release sequence. A compile check at each layer boundary is cheap and catches cascades early.

### W1. Contracts (`src/NetPI.Abstractions`)

Create: `IStorageProvider`, `IStorage`, `ISessionRepository` (+ the atomic unit), `IKeyValueStore`, `IPluginData`/`ICollection` (documents, index declarations, CAS), `IBlobStore`, `IStorageSnapshot`, `ISnapshotContributor`, `ISessionLifecycle`, `CapabilityUnavailable` and `Services.Require<T>()`, `IWorkspaceGuard`, typed decision and git-history records, `ToolParameter` (name, aliases, type, required, role), `ToolEffect`, `ToolArguments`, `IModelMiddleware.MetersPaidCalls`, the RPC owner/priority registration, the error codes, named constants (prompt section ids, cancel reasons, notice provenance).
Change: `IPluginContext` (drop `Db`, `Http`; add `Data`), `ISessionStore` (no shims), `SessionInfo` (no `WorkspaceId`; typed core only), `ToolDefinition`, `IAgentHook` contexts (receive `ToolArguments`), `NetPiPaths` (no `DatabaseFile`).
Delete: `IDatabase`, `Migrate(DDL)`, `IBudgetGate`, `IHttpRegistry`, `ISessionStore.ForkSession`, `SessionIdsUsingWorkspace`, and the policy statics (`ModelMessages.Normalize`, `WorkspacePaths.Check*`, `DecisionHints`, `ToolSelection`, `ToolLists`, `DeferredTools` logic, `SessionModel`'s agent settings read) moved to their owners.

### W2. Storage providers and the conformance suite

- `NetPI.Storage.Sql`: the SQL-family base (extracted with the first server engine, see D9; until then its logic lives in one class of the sqlite provider). The core tables keep today's layout, **expressed per engine** (equivalent, not byte-identical; on SQLite the 200 MB of messages never moves) and gain `sessions.version`. Plugin collections are key + JSON document + index columns. Traps it must absorb, confirmed in today's core SQL unless marked: Oracle `''` = `NULL` hits core columns (`sessions.title NOT NULL DEFAULT ''`, `kv` with an empty value); `INSERT … SELECT MAX+1 … RETURNING id, seq` (SQL Server needs `OUTPUT`, Oracle's `RETURNING INTO` is single-row); `WITH RECURSIVE … DELETE` (Oracle has no `WITH` before `DELETE`); `LIMIT -1`; `INSERT OR IGNORE`; the Ideas upsert whose `CASE` reads the existing row; SQL Server's default case-insensitive collation changes key equality and `ORDER BY id` (binary collation for keys, and search case folding defined explicitly: SQLite folds ASCII only, SQL Server folds Unicode); `kv.key` is a reserved word on SQL Server; indexed fields need declared types and maximum lengths; Oracle DDL commits implicitly, so declaring a collection or an index never runs inside a transaction; `CLOB` fetch cost on `messages.parts` (plausible).
- `NetPI.Storage.Sqlite` (adapter over the existing P/Invoke code, moved) and `NetPI.Storage.Memory`, both inside the app; `SqlServer` and `Oracle` as optional packages if D9 includes them.
- The **conformance suite** is written with the port, not after it: session/message/project semantics (global message ids, seq allocation, `LIKE ... ESCAPE` and case folding, list order, cascades, fork-at-seq), the lock rules (re-entrant, per-plugin exclusive across generations, nested-store rule, `mutate` on a copy with a version check), collection semantics (CAS, index filters, ordering), snapshot round trip, a stress test for the deadlocks the old code fixed, and unload of the plugin's load context. It also covers: an empty-string round trip per field, documents over 4 KB and over 1 MB, Unicode, binary key comparison, DDL outside transactions, millisecond timestamps, `sessions.version` conflict retry, and a second process being refused by the instance lock. Server engines run it when their connection string is set.

### W3. Kernel (`src/NetPI.Host`, `NetPI.Server`, `NetPI.Desktop`)

`HostKernel` loads the storage provider first (fail closed; `memory` only for `--ephemeral`; one in-process re-entrant lock and one dedicated connection; an exclusive instance lock on server engines) and builds the **session service** (events, transient sessions, context cache, titles, fork and compaction-as-of-fork) over `ISessionRepository`; workspaces leave `SessionStore`. `ModelCatalog` refuses paid models unless a middleware meters. Registries: one owner per RPC with priority, the error taxonomy, `Require<T>`, the lifecycle dispatcher (created, forked, deleted, materialized, discarded). `SessionFork`'s hard-coded plugin keys go. The stall watchdog watches the provider lock. Settings: no host seeds. `tools.list` carries `summaryArg`, `icon`, `view`; `hello` carries a protocol version; `app.info` reports `IStorage.Info` instead of the SQLite version. `--ephemeral` through `NetPiServerOptions`, `Server/Program.cs` and the Desktop's arguments. Delete `HttpRegistry` and the `/api/p/` route.

### Internal checkpoint (decides D1; not a stage)

Run it **right after W2 and the core of the session service**, in its own test project (Abstractions, the storage projects, Agents, Ideas), before the rest of W3 and before W4. Nothing else compiles yet at that point (W1 deletes `IDatabase` and `IBudgetGate`, and the Agent and Aux test projects reference 17 and 20 projects), so a failure costs only W1 and W2, not the kernel and the plugins. Write the plugin-data implementation on SQLite and memory, port **Ideas** and the **Agents ledger** onto it, and run: (1) two Agents generations reserving at once give exactly one reservation per call and never exceed the budget; (2) reading a session inside a plugin transaction and plugin data inside `mutate` never deadlocks; (3) Ideas' hard flows need no SQL (`ResolveCard` case-insensitive id or prefix, `ClaimCheck`/`FinishCheck` as a conditional upsert with a token compare-and-set, `ord` as MIN−1/MAX+1, the `NOT IN` cleanup) and common paths do not scan whole collections; (4) ledger totals (period, today, per lane) equal today's SQL on a copy of your data; (5) the plugin's load context is collected after unload; (6) the port has about 12 operations or fewer; (7) every operation is expressible without an engine-specific feature on SQLite, SQL Server and Oracle (write the three dialect forms of the ledger reserve, Ideas' conditional upsert and the indexed filter, and check them against the §3 trap list). (8) Context's fork copy works (range copy on fork, `MAX(version)+1`) and (9) Workspaces' binding on a transient session works through materialized/discarded: W3 freezes the lifecycle hooks before any plugin uses them, so they are exercised here. If 1, 2 or 5 need per-provider special cases, switch plugin data to SQL-as-capability and continue, and say plainly what that costs: plugin SQL is then in the SQLite dialect, so on any other engine Ideas, the ledger, Context and Runtime's agent store report unavailable, and "replaceable" covers the core only.

### W4. Plugins (all 31)

| Plugin | Change |
|---|---|
| Agents | ledger and reservations onto `ctx.Data` (usage as documents with index fields, roll-up by upsert, reserve = per-plugin exclusive transaction); implements metering; its session data (`budgetAllowedFrom`) through the lifecycle; delete `IBudgetGate` use and the memory-charges fallback. Agent config stays in settings. |
| Runtime | agent records onto `ctx.Data`; consumes `ToolArguments`; a throwing before-tool-call hook blocks; resolves optional services per use; notice-provenance constant in the fallback prompt; its session data (`runtimeEnvironment`) through the lifecycle. |
| Context | `context_*` onto `ctx.Data`; fork copy through the lifecycle hook; section-id constants. |
| Ideas | `ideas_*` (8 tables) onto `ctx.Data`; images to `IBlobStore`; delete the legacy cutover (~930 lines) and dead helpers; session provenance via lifecycle. |
| Workspaces | owns its table, bindings, RPCs and events through `ctx.Data` and the lifecycle; provides `IWorkspaceGuard` and the resolver; nothing left in the kernel. |
| Backup | `IStorageSnapshot` + contributors; the manifest records the provider; no `VACUUM INTO`, no fixed file names. |
| Guardrails | rules act on `Effect` and roles; no tool-name or argument-alias sets; `guardrails.unknownTools`; typed second-opinion contract; its session data (`guardrailsAllowed`) through the lifecycle. |
| Decide | typed decision contract; refuses non-local models or is metered; admission through the leases. |
| Loops, Todo | typed decision contract; session state through `ctx.Data` and the lifecycle. |
| Goal, Profiles, Skills, AgentsMd | session-scoped data through `ctx.Data` and the lifecycle (no `meta` keys, no host fork list); the typed core for identity; Loops and Ideas read goal and todo through their owners' services, resolved per use; Profiles keeps the default profile in its own project-scoped collection (`projects.meta` is gone too). |
| Compaction | repository primitive for the compaction flag; consistent lease use. |
| Tools.Files, Shell, Ssh, Web, Media, Agents | definitions declare parameters with roles and `Effect` (ssh and process: per action, with local vs remote locality); delete the private argument readers; Files and Ssh consult `IWorkspaceGuard`; Tools.Agents registers only with an executor and errors explicitly on isolation without Workspaces. |
| Mcp | map tool annotations (`readOnlyHint`, …) into `Effect`; definitions with roles. |
| Diagnostics, Work | `diag.capabilities` reports metering and storage (provider, durable); views use the unavailable error; no storage reads other than logs. |
| Providers (3) | declare paid or local per model; nothing else. |
| Retry, Nudge, ToolRepair, Ask | mechanical: services per use, the new errors, settings defaults owned by the plugin. |

### W5. UI (`web/`, plugin `ui/`)

Only what the swap-over itself needs: the generic renderer for unknown kinds and tool results; the error taxonomy replacing message matching (`openFile.js`, `IdeaCards.svelte`, `hasRpc`); absent-plugin handling (the dead Ideas absence check is fixed); the `hello` protocol version; `tools.list` metadata; and `web/mock` modelling absent plugins and the new errors. **The slot host, moving feature code out of `web/src` and the shared Svelte runtime are out of this swap-over (D6)**: they follow on a stable platform.

### W6. Tests, scripts, docs

`TestHost` runs the real session service over `memory`; delete `FakeSessionStore`, `NullDatabase` and the duplicate `FakeCatalog` behaviour; the conformance suite and the 35-boot independence matrix (with probes for a paid call with no metering and for isolation without Workspaces) become repository scripts; `build.ps1`, `build.sh` and `PendingBuild` install `<app>/storage/<id>/`; `restore-backup.mjs` dispatches on the manifest's provider; docs: `PLUGINS` (core concepts, capability table, the concurrency contract), `PROTOCOL`, `SETTINGS`, `BACKUPS`, `TOOLS`, `UI`, `HANDOFF`, and the independence plan (Workspaces is the 31st plugin).

## Test and fix, until done

Order, repeated until every step is green (every failure is fixed in the code it exercises; no "re-run and see", per `AGENTS.md`):
1. The whole solution builds with zero warnings.
2. The conformance suite on every available provider (SQLite, memory, and any server engine with a connection string).
3. The five unit suites.
4. The independence matrix: all 35 boots plus the two extra probes.
5. The whole e2e suite.
6. The mock UI walkthrough, and a real-host UI run with Runtime and one provider only.
7. **Cutover rehearsal on a copy of your data** (D10): the migration dry-run, the real run on the copy, the new build booted against it, a chat, a backup and a restore.

**Done means:** all of the above green; the rehearsal verified (row counts and message checksums equal before and after; per-table row-to-document counts and ledger totals match; the migrated schema equals a fresh install's, with no dead `workspace_id`, `workspaces` or old plugin tables or their version rows); **zero skipped tests** (idea-r4e8rf); and the **deletion checklist empty**, i.e. a search for the removed symbols finds nothing: `IBudgetGate`, `IDatabase`, `ctx.Db`, `Migrate(`, `ForkSession`, `SessionIdsUsingWorkspace`, `IHttpRegistry`, `WorkspacePaths.Check`, `ModelMessages.Normalize` in Abstractions, `DecisionHints`, the private argument readers, `FakeSessionStore`, `NullDatabase`, host-seeded plugin settings, every `_dbReady`/`_memoryCharges` fallback, the SQLite version in `app.info` (replaced by `IStorage.Info`), and `new Database` in the tests (a provider factory instead).

## Cutover and rollback (your local setup)

One-off migration tool, `scripts/migrations/001-platform-v2`: a C# console that references `NetPI.Storage.Sqlite` (no script reads SQLite today, and a second copy of the DDL would drift), dry-run by default, deleted once applied. `--apply` refuses while NetPI is running and refuses if the new collections already exist (the new build may have started first). It adds `sessions.version`; renames and removes settings keys and drops the host seeds; moves the `sessions.meta` and `projects.meta` plugin keys into the owning plugins' collections; moves the `workspaces` table and `sessions.workspace_id` into the Workspaces collection; converts the plugin tables (Ideas, ledger, Context, Runtime) from rows into documents; moves idea images into the blob store; and drops the dead tables and their `Migrate` version rows. The core tables (sessions, messages, projects, kv) do not move.

1. **Rehearse on the copy** (D10): the same tool, the new build booted against the result, a chat, a backup and a restore.
2. **Copy the whole current app folder** (`artifacts\app`) aside: the installer deletes `.old` at every start, so the old build is not kept automatically.
3. **Close NetPI, then snapshot** (`netpi.db`, `-wal`, `-shm`, `settings.json`, `idea-images/`). A snapshot taken before closing loses whatever is written in between.
4. `build.ps1 -Publish -NextStart` the new build, run the tool with `--apply`, start the new build, check a chat, ideas and a backup.
5. **Rollback:** close NetPI, restore the snapshot files and the copied app folder. Old backups are restorable only by the old build; `BACKUPS.md` says so.

## Risks of doing it in one swap-over

| Risk | Mitigation |
|---|---|
| A large surface fails together and failures cascade. | Write in layer order with a compile check at each boundary; the conformance suite and the Ideas + ledger checkpoint come before the other plugins. |
| The concurrency contract is violated and deadlocks or double charges return. | The contract is written into the conformance suite with the port; the stress tests are written first. |
| Live data. | Snapshot, rehearsal on a copy, a verified migration, a kept rollback build. Nothing touches the live home without you. |
| Branch drift while it is written. | D8: plugins, Abstractions and Host are frozen on master for the duration; nothing written against the old contracts lands meanwhile. |
| A server engine is wrong where SQLite is not. | The same conformance suite on every engine; written only if it can be tested (D9). |

## Not doing

- A SQL-translation layer or an ORM.
- Hot-swapping storage at runtime, or a storage provider on the plugin hot-reload lifecycle.
- A remote database before the provider lock is measured against one.
- Touching `ISettings`: it stays the bootstrap root (plugin directories, logging and the storage provider are read from it first).
- Compatibility of any kind with the shapes this plan removes.
