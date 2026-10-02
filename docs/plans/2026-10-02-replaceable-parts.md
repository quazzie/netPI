# Replaceable storage: a swap-over that changes nothing you can see

Date: 2026-10-02
Status: implemented on branch `netpi/arch-plan` (2026-10-02), not yet merged or installed; see "Outcome" at the end. One swap-over: all the code is written in one session, then a test/fix loop runs until the definition of done is met. Review snapshot: master at 59619d2. Reviewed three times the same day by one Opus agent (read-only, the third after the core was redefined); its findings are folded in and marked **(review)**.

## Goal

Exactly today's behaviour, with **storage as a part you can swap**, **no specific plugin required**, and **a core as small as a harness allows**: the concepts a harness is made of, and none of the higher abstractions built on them. Someone can later write an MSSQL (or any) storage provider by implementing the storage port in that engine's own SQL and passing the conformance suite. Nothing else changes: the wire protocol is frozen, so the UI does not change.

## Ground rules

1. **Contracts may change whatever the design needs.** No compatibility shims, deprecated paths, mirrors or forwarders. A contract change updates every plugin, test and doc in the same change. (`AGENTS.md` and `HANDOFF.md` say so as of this branch.)
2. **Delete what you replace** in the same change.
3. **Only the owner's local setup is migrated**, by one one-off tool, after a snapshot, with the owner's say-so. Migration code never ships in the product.
4. **The wire protocol is frozen, with one forced exception.** No RPC renamed, no event changed, no new error codes, no payload shape changed except that a session no longer carries a top-level `workspaceId` (it is in `meta`, section 2). The UI changes only for that: the composer's `ProjectPicker.svelte` reads `session.meta.workspaceId` (line 19, both uses, the only ones in `web/`), and its bundle is rebuilt; the plugin tab bundles have no diff. Where something the kernel owns today moves into a plugin, the plugin registers or publishes the same names with the same shapes.
5. **No dialect layer.** The port exposes no SQL and no engine-specific type. Each provider writes its own SQL natively, inside itself.
6. **The core holds the concepts of a harness and no higher abstraction.** A harness is made of sessions, messages, models, tools, context, the loop and its hooks, storage and the plugin mechanism. Agents (a model bundled with instances, slots and a budget), budget, workspaces, resource leases and decisions are built on those, so they are plugins, and neither the kernel nor the core contract assembly knows them (section 4). It is checked mechanically: the Host project references only the core assembly, and a search of `src/` for those names finds only the `workspace.default` setting (a plain default folder, its key kept for the wire).

## What is in the core, and why

The test for a thing: **is it a concept every harness is made of, or a higher abstraction built from those?** The first is core, the second is a plugin, however many plugins use it. (An earlier draft of this plan used "could a chat run without it?", which also cut tools and context; that was wrong for a harness.)

- **The plugin mechanism:** loading, lifecycle, hot reload, isolation; the registries for services, RPC, HTTP routes, UI tabs and **tools**; the event bus.
- **The wire:** transport, auth and the protocol the UI speaks. The UI is a client of it: RPC calls plus event subscriptions. Freeze the protocol and the UI is untouched.
- **Settings** (the bootstrap root), logging, paths and the home lock.
- **Sessions, messages and projects:** the types and the session service that adds events, transient sessions, the context cache, titles and forks. A project is a name and a folder, the basic "where a session works"; a session may also carry its own **`meta.cwd`**, the folder it runs in when that is not its project's, and `GetCwd` is `meta.cwd`, else the project folder, else `workspace.default` (a session needs a place to run, so the core understands this one). **The core stores no field it cannot interpret:** anything else a plugin wants on a session it **attaches to `meta`**, the bag the core stores, copies on fork and reports changes of (`session.changed { keys }`), and finds sessions by (`SessionQuery.Attached`, an equality filter on one meta key). The attach mechanism already exists; this plan makes it the only one.
- **Models:** the catalog and the provider and middleware contracts.
- **Tools:** the tool contract (definition, arguments, context, results), the registry, and per-session and global switching. How tools are shown to the model (deferred and indirect tools, `ToolSelection`) is the loop's policy and embeds the agents depth rule, so it lives in `NetPI.Contracts`. `ToolContext` and the loop's run context carry a neutral typed bag, `Feature<T>()`, that Runtime fills (the workspace binding, the admission slot), so the core types name none of those.
- **Context:** the system-prompt contracts (sections, builder, the revision and fork rule, the session identity). Context the plugin implements them; the contracts are the harness's.
- **The loop's contract:** the runtime, its hooks and observers, and the neutral refusal (`CallRefusedException`). The Runtime plugin implements the loop; the extension points are the harness's.
- **The storage port:** interfaces only. The built-in `sqlite` provider (today's code, moved) and a `memory` provider (tests and `--ephemeral`) are the first two implementations.

**Higher abstractions, so plugins:** agents (named agents, slots, scheduling), budget and metering, workspaces (checkouts, bindings, provisioning), resource leases, decisions, profiles, goals, todo, ideas, and everything else that already is one. Their vocabularies live in a second assembly, `NetPI.Contracts` (section 4), not in the core.

## What the architecture review found, and what this plan does about it

| # | Finding | This plan |
|---|---|---|
| 1 | **Storage cannot be replaced.** The host builds the concrete `Database` and `SessionStore` before any plugin loads (`HostKernel.cs:70,84`); plugins get them (156 `ctx.Sessions`, 21 `ctx.Db` uses, 2 registry lookups); `IDatabase` is SQLite SQL (~102 sites in 5 plugins); Backup runs `VACUUM INTO`. | **In.** |
| 2 | **Paid models need the Agents plugin, and the core talks about budgets.** `ModelCatalog.cs:150` refuses non-local models unless something registers the empty marker `IBudgetGate`; only Agents does. The contract assembly carries `BudgetExceededException`, and the kernel's fork list (`SessionFork.RunState`) names the plugin-owned meta key `budgetAllowedFrom`. | **In:** all three go (see 3). Budget is not core. |
| 3 | **Workspaces are kernel data** (table, `sessions.workspace_id`, RPCs, events; `SessionStore` implements `IWorkspaceStore`). | **In**, so the provider interface has no workspace methods. |
| 4 | Policy statics in the contract assembly. | The ones that are higher abstractions (workspace paths, agent slots) **move** to `NetPI.Contracts` (section 4); turning statics into services is not in this change. |
| 5 | Tool arguments parsed many times; guards match names. | Not in this change (the guard bypass is already fixed in 9efaf19). |
| 6 | RPC registrations have no owner. | Not in this change. |
| 7 | The UI shell hard-wires 11 plugins' features. | Not in this change (the UI does not change). |
| 8 | Fork policy hard-codes meta keys; host-seeded settings; typed decision contracts. | **In** for the fork keys (plugins declare them, section 4) and the seeds; typed decision contracts are not in this change. |
| 9 | **The core knows higher abstractions**: the resource-lease registry, agent-slot, workspace and decision vocabularies in the one contract assembly, the model-resolution helper that reads `agents.<id>.model`, plugin settings seeded by the host, `NetPiPaths.GlobalAgentsMd`. | **In** (section 4): the core is cut to the harness concepts in "What is in the core". |

Evidence for plugin ↔ plugin independence: 35 real servers were booted with each plugin removed, and host-only and Runtime+provider sets. Every one boots; the kernel alone starts in 0.5 s. Independence is sound; the gaps are what the kernel owns.

## The design

### Layering: who knows what

```
plugins (Ideas, Agents, Context, Runtime, Workspaces, Backup, …)
    use only:  ctx.Sessions (projects, sessions, messages)   ctx.Data (collections)   the snapshot
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

- **`ISessionRepository`**: primitives plus one `Atomic(Func<tx,T>)` that runs on the provider lock, about 20–25 methods **(review)**. Several operations are atomic across tables (materialising a first message touches the session, the project's `last_used_at` and the message; append assigns seq as `MAX+1` with counters and auto-title; project delete detaches sessions; session-tree delete; fork copies messages and recomputes compaction as of the fork point). Workspaces are not in it (section 2). The kernel's **session service** keeps the behaviour (events, transient sessions, the context cache, titles, compaction-as-of-fork, id remapping), so a provider implements persistence only. `sessions.meta` stays one JSON field; the protocol keeps its keys.
- **Concurrency contract** **(review)**: every provider has one **in-process re-entrant lock** and one dedicated connection, exposed to the kernel (the transient-session map and the stall watchdog share it today, which is the session deadlock fix); plugin transactions are **exclusive per plugin id across hot-reload generations** (so two Agents generations never double-charge a call); `UpdateSession`'s `mutate` keeps today's semantics (it runs inside the lock), calling a plugin store inside it is forbidden (the one old violation, `Ledger.Allow`, no longer does that: `Ledger.cs:364-375` computes the period outside and `mutate` only writes meta), and `GetCwd` reads session data only and is never called under the provider lock. The conformance suite pins all of it.
- **`IPluginData` (`ctx.Data`)** replaces raw `IDatabase`: typed collections carried as **JSON documents plus declared index fields**, keyed get/put/delete, equality/range/IN filters on indexed fields, ordering and limit, **compare-and-set**, and the per-plugin transactions above. No group-by (the ledger keeps its roll-up by upsert). Plugin types never cross the port, or a provider would pin a plugin's load context and block unloading. Typing is a helper on the plugin side.
- **`IStorageSnapshot`**: the provider snapshots itself; Backup uses it instead of `VACUUM INTO`, and the restore script dispatches on the manifest's provider.
- **Selection fails closed**: a missing or broken provider stops startup with a clear error; `memory` runs only when asked for (`--ephemeral`), because running the owner's NetPI ephemeral after a broken install would lose every chat **(review)**.
- **The sqlite provider** is today's code moved behind these interfaces. It keeps today's core tables (the 200 MB of messages does not move) and writes its own SQL. It has no knowledge of any other dialect.

### 2. Workspaces leave the kernel (projects stay)

**Why there are both.** A **project** is the logical identity of what you work on: a name, a folder, and what hangs off it (the ideas backlog, default profile, the grouping in the session list). A **workspace** is where one worker actually works: a checkout with a root, a branch, a base commit and an owner, so several workers of one project can each have their own git worktree of the same repository. A workspace belongs to a project (`WorkspaceInfo.ProjectId`); a project does not need a workspace, and a session without one simply works in its project's folder. So it is not the other way round: the project is the basic concept and the workspace is machinery on top of it (git, ownership, provisioning, cleanup), which makes the workspace the higher abstraction.

A project is the harness's basic "where a session works". It stays in the core with `GetCwd`, `projects.*`, `sessions.setProject`, `fs.dirs`, `workspace.default` and the `project.*` and `session.project` events, all as today, and `projectId` stays a column of a session because the core understands it (it lists, filters and deletes by it). A **workspace** is a higher abstraction, so the `workspaces` table and its CRUD move into the Workspaces plugin as a collection, and **the session loses its `workspace_id` column and `SessionInfo.WorkspaceId`**: the binding is attached to the session's `meta` by the plugin, through `UpdateSession`: **`meta.workspaceId`** (which `SessionWorkspace.MetaKey` documents as the contract; nothing writes it today, so the cutover tool is its first writer) and **`meta.cwd`**, the bound root. It is announced by `session.changed { keys }` and by the plugin's own `session.workspace` event, which is also new for a message-less session (that session only gets `session.changed` today, nothing). The `workspaces.*` and `sessions.setWorkspace` RPCs and the `workspace.*` and `session.workspace` events keep their names and shapes, registered and published by the plugin. The one UI consequence is in rule 4.

**The working directory needs no plugin hook (review).** An earlier draft had `GetCwd` ask plugins for a directory. That fails open: with the plugin absent, disabled or returning null for a broken binding, a bound worker would run in the shared project checkout, which the contract forbids (`Workspaces.cs:126`), and a call into a plugin from `GetCwd` invites a lock-order inversion with plugin transactions. Instead the core simply understands `meta.cwd` (above). The plugin writes it when it binds and clears it when it unbinds, so a bound session keeps its root with the plugin absent and `GetCwd` reads only session data. The 10 `GetCwd` call sites in 6 plugins do not change. A **fork never copies `meta.cwd`** (a fork is a new writer and starts in its project folder; the core rule), and the plugin's resolver treats a `workspaceId` whose root differs from `meta.cwd` as a broken binding and refuses it, as it refuses a missing one.

**Meta is the only truth about a binding (review).** There is no plugin-side index to go stale (`sessions.update { meta }` replaces the whole bag, transient sessions announce nothing, archived sessions do not count as "in use"). "Which sessions use workspace X", `canRetire` and unbinding on delete ask the session service with **`SessionQuery.Attached`** (key `workspaceId`, value, archived flags as today), which matches transient sessions in the service and persisted ones in the provider (an equality filter on one JSON key; the sessions table is small, so a scan is fine). The conformance suite pins it, including transient and archived sessions.

### 3. Budget leaves the kernel

The core stops knowing about money. Three things go, and nothing replaces them in the core:

- **The check.** Delete the `IBudgetGate` test in `ModelCatalog` and the marker interface. The ledger, caps and reservations stay in the Agents plugin as a normal model middleware (it already is one). The kernel keeps `IsLocal` (a fact about a provider, used to pick a default model and by the resource leases) and the generic middleware pipeline; `Usage.CostUsd` stays too, as a figure a provider reports, like token counts. Trade-off, stated plainly: if Agents is absent or fails to load, paid calls are not metered or refused by anything. That is the decision: the core does not police it, and there is no Diagnostics warning either. Paid providers already need an API key you set. Decide's direct calls to the model server stay outside the catalog, as they are today.
- **The exception.** `BudgetExceededException` becomes a neutral contract type, `CallRefusedException { Kind, CanOverride }`: a scheduler or a middleware refused a run or a call. It also replaces `AgentUnavailableException` (`Kind = "unavailable"`; Runtime and Ideas catch that one today). Runtime and the agent tools catch it without knowing why; the Agents plugin sets `Kind = "budget"`, and Runtime keeps both of today's behaviours keyed on `Kind`: an error notice at the slot step for either kind, and at the model call a notice of the same kind with `canOverride`, so the notice the UI receives is the same ("budget", `canOverride`). The agent-slot snapshot (`AgentSlots`: price, spent today, daily limit) is the optional agent-scheduler contract, which the Host does not use; its fields are what `agents.list` sends, so it keeps them.
- **The fork key.** The "let this chat go over" allowance stays in the session's meta as `budgetAllowedFrom` (the UI e2e and its mock assert it through `sessions.get`, so moving it would change the wire). The kernel stops naming it: the Agents plugin **declares it as a fork-reset key** (section 4), like every plugin-owned run-state key.

**What the Agents plugin is, and why it does not move into a "models" plugin.** It is two features in one: a **scheduler** (named agents = a model plus instances and config, and capacity slots: `agents.*`, `AgentScheduler.cs`) and a **budget ledger** (`budget.*`, `usage.*`, `Ledger.cs`, `Reservations.cs`). Talking to a model needs only a **provider** (the "model plugin": one per backend), the catalog and **Runtime** (the loop). Runtime already looks the scheduler up optionally, so chat works without Agents today for local models; with the kernel check gone it works for paid models too. Nobody needs a second kind of models plugin. Splitting Agents into a scheduler plugin and a budget plugin (so you could have either alone) is a sensible later cleanup and is not needed here.

### 4. The core diet: harness concepts only

Audit of `src/NetPI.Host` and `src/NetPI.Abstractions` against the test in "What is in the core". Tools, context, projects and the loop's contract pass it and stay. What the Host holds that fails it is the budget check, workspaces, the lease registry, seeded plugin settings and the plugin keys in the fork; the contract assembly carries a few more higher-abstraction vocabularies. All of it goes out the same way: the plugin that owns the concept registers the same RPCs, events and settings, so the wire is unchanged.

| Today | After |
|---|---|
| The contract assembly holds the vocabularies of higher abstractions next to the core ones: `AgentSlots.cs` (scheduler, slots, `SessionAgent`), `SessionModel` (resolves an agent's model and reads `agents.<id>.model`), `SessionProfile`, `Workspaces.cs`, `Decisions` and `DecisionHints`, `ResourceLeases.cs`, `DeferredTools.cs` with `ToolSelection`, the shell's `process.*` event names; `BackgroundWork.cs` (only Ideas uses it) | A second assembly, **`NetPI.Contracts`**; `BackgroundWork` goes into the Ideas plugin itself. The types keep `namespace NetPI`, so almost no source line changes: project references, plus the strands below, the moved `EventTypes` constants and `SessionProfile` (today in `Sessions.cs`). The **Host project cannot reference it**, so the compiler enforces the small core. A plugin that speaks none of these references only Abstractions. |
| **Strands the review found** (core types that name moved types) | `ToolContext.AdmissionLease : IAgentSlot` and `ToolContext.Workspace : WorkspaceBinding` (`Tools.cs:85,95`), `AgentRunContext.AdmissionLease` and `.Workspace` (`Agents.cs:153,159`): replaced by the typed bag `Feature<T>()`, which Runtime fills; readers call `ctx.Feature<WorkspaceBinding>()`. `SpawnRequest`'s five workspace fields (`Agents.cs:74-83`): replaced by an opaque `Extensions` object whose `workspace` key Runtime reads as a `WorkspaceRequest` (Runtime references Contracts; the core type does not). Affected: Decide, Guardrails, Ideas, Loops, Runtime, Tools.Files, Tools.Shell, Tools.Ssh, Tools.Agents, Workspaces. |
| **Loading `NetPI.Contracts` (review)** | `NetPI.Contracts.dll` ships in the app root as a host file, staged through `.old` like `NetPI.Abstractions.dll`. The host preloads it by path into the default context before any plugin loads (the Host does not reference it, so it is not on the TPA list), and `SharedAssemblies.IsShared` names it. Plugins reference it like Abstractions (`Private=false`, `ExcludeAssets=runtime`), so none ships its own copy (a private copy makes `Services.Get<IAgentScheduler>()` return null with no error). `build.ps1` and `build.sh` detect a contract change by hashing **both** assemblies, `PendingBuild` installs it, and a test checks that two plugins see the same `Type` for a Contracts interface. |
| `ResourceLeases` registry in the kernel (`HostKernel.cs:152`) | The implementation becomes a plain class in `NetPI.Contracts` (so it lives in the default context); the Agents plugin registers one instance as a service, and a new generation adopts the registered instance, so two generations during a hot swap share one count. With Agents absent nothing limits admission to a local model, which is what Agents is for; consumers (Runtime, Work, Diagnostics, Decide) treat "no registry" as unlimited, and Decide's fallback to the host registry goes. |
| `IBudgetGate` check, `BudgetExceededException`, `budgetAllowedFrom` | Section 3. |
| Workspace methods, events and the `workspace_id` handling in `SessionStore` and `ISessionStore` | Section 2. |
| Fork: `SessionFork.RunState` names `goal`, `todo`, `guardrailsAllowed`, `agentId`, `parentAgentId`, `agentInstructions`, `runtimeEnvironment` | Each plugin **declares its own run-state keys** with `ctx.Sessions.DeclareForkReset(keys…)` when it starts (Goal, Todo, Guardrails, Agents: `agentId`, `parentAgentId`, `agentInstructions`, `budgetAllowedFrom`; Runtime: `runtimeEnvironment`). The session service drops every declared key from a fork's meta and **remembers every key ever declared in the key-value store**, so a plugin that is absent, failed or throwing at fork time still has its keys dropped (an earlier draft used per-plugin filter callbacks, which fail open in exactly those cases). The cutover seeds the remembered set with today's list. The kernel keeps what is the harness's own: the context rule (`SessionPrompt.Fork`, `promptForkReset`), `meta.cwd` (never copied), title, `forkedFrom`, the message copy, compaction as of the fork point. |
| Host-seeded defaults for plugin settings (`providers.aiproxy`, `providers.anthropic`, `compaction`, `nudge`, `retry`) | The owning plugin's schema defaults (AiProxy's must default `transport` to `"responses"`, or a fresh install changes transport). The core keeps `plugins`, `server`, `ui`, `tools.disabled`; `logging`, `defaultModel`, `models.refreshSeconds` and `workspace.default` are core keys that are not seeded today. |
| `NetPiPaths.GlobalAgentsMd` | AgentsMd computes its own path. |
| `EventTypes`: `workspace.*`, `session.workspace`, `agents.changed`, `process.*` (the shell's) | Constants live with their contract; the core keeps session, project, message, stream, tool, agent (the loop's), models, plugins, settings, usage and ui. |

`IPluginContext` after: it loses `Db` and gains `Data`; everything else stays, including `Tools`. The core's RPC list shrinks only by `workspaces.list`, `workspaces.get` and `sessions.setWorkspace`, which the Workspaces plugin registers.

What this deliberately keeps in the core, so a reader does not re-litigate it: the tool registry and `ToolDefinition`/`ToolContext`/`ToolResult`; the context contracts (`Context.cs`, `SessionPrompt`, `SessionIdentity`); `SessionTools` (tools switched off per session); the loop's runtime, hooks and observers (`Agents.cs`) and its `session.kind` of `chat` or `subagent`; projects and `meta.cwd`.

What the UI sees: the Workspaces plugin registers workspace RPCs and events under the same names, so nothing it shows moves, with the one edit in rule 4. The UI mock and the UI e2e are updated for exactly that: the mock sets `meta.workspaceId` and `meta.cwd` on a bound session, the composer assertion reads them, and a real-host e2e binds a session to a workspace and checks the picker (the mock alone never sets it, so it cannot prove the edit).

### 5. Test infrastructure follows

`TestHost` runs the real session service over `memory`; `FakeSessionStore`, `NullDatabase` and the duplicated catalog fake behaviour are deleted; Host tests build providers through a factory instead of `new Database` (13 sites) **(review)**.

## What gets written

The order of **writing**, so each layer compiles against the one below. There is no UI step.

1. **Contracts:** first the split of section 4: the higher-abstraction vocabularies move into the new `NetPI.Contracts` assembly (a move, no namespace change), leaving Abstractions as the harness only; then the storage interfaces and `ctx.Data`, `ctx.Sessions.DeclareForkReset`, `SessionQuery.Attached`, `meta.cwd` in `GetCwd`, the `Feature<T>()` bag on `ToolContext` and `AgentRunContext`, and `SpawnRequest.Extensions`. Delete `IDatabase`, `Migrate(DDL)`, `IBudgetGate`, `NetPiPaths.DatabaseFile`, `GlobalAgentsMd`, `IWorkspaceStore` and the workspace methods of `ISessionStore`, and the `ISessionStore` shims (`ForkSession`, `SessionIdsUsingWorkspace`); replace `BudgetExceededException` and `AgentUnavailableException` with `CallRefusedException`. `IPluginContext` loses `Db`, gains `Data`; `Tools`, `Sessions` and `Models` stay.
2. **Providers and the conformance suite:** `sqlite` (today's SQL moved) and `memory`, and the suite, written together with the port. It pins: id and seq allocation (message ids are global), search semantics (`LIKE ... ESCAPE`, ASCII case folding), list order (pinned, `updated_at`, id), cascades, fork-at-seq, fork-reset keys (declared and remembered), `meta.cwd` never copied, `Attached` queries over persisted, transient and archived sessions, the lock rules above (including `GetCwd` reading session data only), collection semantics (CAS, index filters, ordering), snapshot round trip, a stress test for the deadlocks the old code fixed, and unload of a plugin's load context. This suite is the contract a future MSSQL provider must pass; it is what makes writing one realistic.
3. **Internal checkpoint** (below), right after step 2 and the core of the session service.
4. **Kernel:** provider loading, the session service over the repository (no `workspace_id` column or `SessionInfo.WorkspaceId`), the remembered fork-reset keys in the fork, `meta.cwd` in `GetCwd`, the preload of `NetPI.Contracts.dll` and its entry in `SharedAssemblies`, the catalog without the budget check; the workspace code, the resource-lease registry and the seeded plugin settings removed from the Host; `app.info` keeps its fields (the sqlite provider reports its version, `memory` reports none).
5. **Plugins that change:**
   - **Storage:** Agents (ledger and reservations onto `ctx.Data`, the allowance stays in meta; its middleware unchanged; drop the memory-charges fallback), Runtime (agent records), Context (prompts), Ideas (its 8 tables; delete the finished legacy cutover, idea-2iz2bu; takes `BackgroundWork`), Backup (snapshots), Diagnostics (storage info).
   - **Workspaces:** its collection; RPCs and events; writes and clears `meta.workspaceId` and `meta.cwd`; asks `SessionQuery.Attached`; refuses a binding whose `workspaceId` and `meta.cwd` disagree.
   - **The `Feature<T>()` and `Extensions` strands:** Decide, Guardrails, Ideas, Loops, Runtime, Tools.Files, Tools.Shell, Tools.Ssh, Tools.Agents. Plugins that read `session.WorkspaceId` today (AgentsMd, Skills, Tools.Files, Tools.Shell, Tools.Agents, Runtime, Workspaces) use `SessionWorkspace.Of(session)` or `GetCwd` instead.
   - **Refusals:** Runtime and Ideas (`AgentUnavailableException` becomes `CallRefusedException` with `Kind = "unavailable"`), Tools.Agents; Agents sets `Kind = "budget"`.
   - **Declared fork-reset keys:** Goal, Todo, Guardrails, Agents, Runtime.
   - **Leases:** Agents registers the shared registry and adopts a previous generation's; Runtime, Work, Diagnostics and Decide treat "none" as unlimited.
   - **Settings defaults:** the providers (AiProxy keeps `transport: "responses"`), Compaction, Nudge and Retry; AgentsMd computes its own path.
   - **Every other plugin changes only its project references** (to `NetPI.Contracts` where it speaks a higher-abstraction vocabulary): mechanical, and the compiler finds every one.
6. **Tests, scripts and docs:** the test infrastructure above; the 35-boot independence matrix as `scripts/independence.mjs` (its paid-call probe now expects a paid provider to work with Agents removed, plus a bound session with Workspaces removed that must **not** run in the project folder); `scripts/core-size.mjs` (the done-check below); the workspace suites rewritten as plugin suites (`WorkspaceStoreTests`, Aux `WorkspaceTests`, `SubagentWorkspaceTests`, `WorkspaceToolTests`), the allowance and fork tests (`SessionStoreTests`, `GuardrailsTests`, `ReservationTests`) moved to declared keys; `restore-backup.mjs`; `PLUGINS`, `PROTOCOL`, `BACKUPS`, `SETTINGS` (`storage.provider`), `HANDOFF`, and the independence plan.

### Internal checkpoint (decides the plugin-data port; not a stage)

Run it in its own test project (contracts, providers, Agents, Ideas, Context) before the rest of the kernel and before the other plugins. Nothing else compiles at that point (step 1 deletes `IDatabase`), so a failure costs only steps 1 and 2 **(review)**. Port **Ideas** and the **Agents ledger** onto `ctx.Data` and check, on sqlite and memory:
1. Two Agents generations reserving at once give exactly one reservation per call and never exceed the budget.
2. Reading a session inside a plugin transaction does not deadlock.
3. Ideas' hard flows need no SQL: `ResolveCard` (case-insensitive id or prefix), `ClaimCheck`/`FinishCheck` (conditional upsert with a token compare-and-set), `ord` as MIN−1/MAX+1, the `NOT IN` cleanup; common paths do not scan whole collections.
4. Ledger totals (period, today, per lane) equal today's SQL on a copy of your data.
5. The plugin's load context is collected after unload.
6. The port has about 12 operations or fewer, and exposes no SQL or engine-specific type.
7. Context's fork copy works (range copy, `MAX(version)+1`).

If 1, 2 or 5 need per-provider special cases, plugin data stays SQL in the SQLite dialect behind a declared optional capability. Say plainly what that costs: Ideas, the ledger, Context and Runtime's store would report unavailable on any other engine, so a future MSSQL provider could host the core but not those plugins.

## Test and fix until done

In this order, repeated until green (every failure fixed in the code it exercises; no "re-run and see"):
1. The solution builds with zero warnings, and `NetPI.Host` builds with a reference to `NetPI.Abstractions` only.
2. The conformance suite on sqlite and memory.
3. The five unit suites.
4. The independence matrix: all 35 boots, plus a paid provider working with Agents removed, plus a session working in its project folder with Workspaces removed.
5. The whole e2e suite.
6. The mock UI walkthrough and the UI e2e against the new kernel, unmodified except the bound-session case in rule 4 (the mock sets `meta.workspaceId` and `meta.cwd`), plus the real-host e2e that binds a session and checks the picker.
7. A cutover rehearsal on a copy of your data.

**Done means:** all green; **`git diff` shows under `web/` only the one-line `session.meta.workspaceId` edit in the composer's `ProjectPicker.svelte` (line 19, both uses; and its rebuilt bundle), and in the wire protocol only the session's lost top-level `workspaceId`**; the rehearsal verified (row counts and message checksums equal before and after; per-collection document counts and ledger totals match; the migrated schema equals a fresh install's); zero skipped tests (idea-r4e8rf); **`scripts/core-size.mjs` passes** (rule 6): `NetPI.Host` references only `NetPI.Abstractions`, and neither `src/NetPI.Host` nor the public surface of `NetPI.Abstractions` contains an identifier from an explicit forbidden list (`IBudgetGate`, `BudgetExceededException`, `IAgentScheduler`, `AgentSlot*`, `SessionAgent`, `SessionModel`, `SessionProfile`, `Workspace*` types, `IWorkspace*`, `IResourceLease*`, `IDecisionService`, `DecisionHints`, `ToolSelection`, `IBackgroundWork`…); a word search cannot do this, because `ToolCallDecision` and `Release` match "decision" and "lease" and `app.info.defaultWorkspace` is a frozen wire field; and the deletion checklist empty: `IDatabase`, `ctx.Db`, `Migrate(`, `sessions.workspace_id`, `SessionInfo.WorkspaceId`, the `AGENTS.md` mentions in `Context.cs` and `SessionStore.cs`, `IBudgetGate`, `BudgetExceededException`, `AgentUnavailableException`, `SessionFork.RunState`, the Host's `ResourceLeases`, the seeded plugin settings, `IWorkspaceStore`, the workspace methods of `ISessionStore`, `ForkSession`, `SessionIdsUsingWorkspace`, `NetPiPaths.DatabaseFile`, `NetPiPaths.GlobalAgentsMd`, every `_dbReady`/`_memoryCharges` fallback, `FakeSessionStore`, `NullDatabase`, `new Database` in tests.

## Cutover and rollback (your local setup)

*Done on 2026-10-02: rehearsed, applied to the owner's home and verified, and the tool deleted afterwards (it is in git history at `c6480ff`, under `scripts/migrations/001-storage-port`). The text below is the plan as written.*

One-off tool `scripts/migrations/001-storage-port`: a C# console referencing the sqlite provider (no script reads SQLite today, and a second copy of the DDL would drift). Dry-run by default; deleted once applied. `--apply` refuses while NetPI is running and refuses if the new collections already exist. It converts the plugin tables (Ideas, ledger, Context, Runtime) from rows into documents, moves the `workspaces` table into the Workspaces collection, writes `meta.workspaceId` and `meta.cwd` (the workspace's path) for each session whose `workspace_id` is set and rebuilds `sessions` without the column, seeds the remembered fork-reset keys with today's list (`goal`, `todo`, `budgetAllowedFrom`, `guardrailsAllowed`, `agentId`, `parentAgentId`, `agentInstructions`, `runtimeEnvironment`), and drops the dead tables and their `Migrate` version rows. The core tables (sessions, messages, projects, kv) do not move. `settings.json` keeps every key: only who declares a default changes.

1. **Rehearse on a copy** of your data (needs your go): the same tool, the new build booted against the result, a chat, a backup and a restore.
2. **Copy the whole current app folder** aside: the installer deletes `.old` at every start, so the old build is not kept automatically **(review)**.
3. **Close NetPI, then snapshot** (`netpi.db`, `-wal`, `-shm`, `settings.json`, `idea-images/`): a snapshot taken before closing loses what is written in between **(review)**.
4. `build.ps1 -Publish -NextStart` the new build, `--apply`, start it, check a chat, ideas and a backup.
5. **Rollback:** close NetPI, restore the snapshot and the copied app folder. Old backups are restorable only by the old build; `BACKUPS.md` says so.

## Decisions

Decided by the owner (2026-10-02):
- **Plugin data as documents in named collections** (`ctx.Data`, above). The checkpoint still proves it on Ideas and the ledger before the rest is built; SQL in the plugins is the fallback only if it fails.
- **The core holds the concepts of a harness and no higher abstraction** (rule 6). Tools, context, projects and the loop's contract are core; agents, budget, workspaces, leases and decisions are plugins. Budget leaves with nothing replacing the refusal, not even a Diagnostics warning (section 3).
- **The core stores no field it cannot interpret.** Plugins attach their data to a session's `meta`; the core has no `workspaceId` column (section 2). The owner accepts the price: the composer's `ProjectPicker.svelte` reads `session.meta.workspaceId` (one line, the only use in `web/`).
- **Projects are core** ("a session needs a place to run"), and **workspaces are a plugin** (a checkout is machinery on top of a project); the core understands `meta.cwd`, the folder a session runs in, so no plugin hook is needed (section 2).
- **Permission granted** to copy `netpi.db` and `settings.json` read-only for the rehearsal. Nothing else under `%USERPROFILE%\.netpi` is touched, and the running NetPI is not stopped without asking.
- **Freeze** the Host, the contract assembly and the plugins this change touches on master for the duration: nothing else is working on them.

Plain-language version of the first: the four plugins that keep their own data (Ideas, Agents' ledger, Context, Runtime) stop writing SQL and instead save, load and look up records by name and a few indexed fields, so any storage engine can hold them.

## Notes for a future MSSQL provider (not built now)

It implements `IStorageProvider` with its own SQL and passes the conformance suite. It would ship as an optional package under `<app>/storage/<id>/`, which needs the installer (`PendingBuild`, `build.ps1`, `build.sh`) to learn to install provider folders (today only plugins and `wwwroot`): build that when the first non-built-in provider exists. It also needs an exclusive database instance lock at startup so two homes cannot share one database (the file home lock does not cover that). Dialect traps found in today's core SQL, so they are written down once **(review)**: Oracle-style `''` = `NULL` hits `sessions.title` and `kv`; `INSERT … SELECT MAX+1 … RETURNING` and `WITH RECURSIVE … DELETE` have no portable form; `LIMIT -1`; `INSERT OR IGNORE`; case-insensitive default collations change key equality and ordering; `kv.key` is a reserved word on SQL Server; DDL commits implicitly on Oracle; large text columns cost on `messages.parts`.

## Not in this change

Splitting the Agents plugin into a scheduler plugin and a budget plugin; turning the contract statics into services (the statics only move to `NetPI.Contracts`); splitting `NetPI.Contracts` per plugin; normalised tool arguments and guards by role; one owner per RPC and an error taxonomy; the session `meta` bag as typed data; UI slots and the shared Svelte runtime; typed decision contracts; the two small silent-failure fixes (`agent_spawn isolated` with no Workspaces, a throwing before-tool-call hook); SQL Server, Oracle or any other engine; a SQL-translation layer; hot-swapping storage at runtime; touching `ISettings`. Each can be taken later; none is needed for storage to be swappable.

## Outcome (2026-10-02)

Built in one pass, then a test and fix loop; the revert point is the tag `pre-swapover-2026-10-02`.

**As planned:** the storage port (`StoragePort.cs`) with the `sqlite` and `memory` providers; plugin data on `ctx.Data` (Ideas, the Agents ledger and reservations, Context, Runtime, Workspaces); `NetPI.Contracts` as the shared assembly of the higher abstractions; budget out of the core (`CallRefusedException`, no gate); workspaces out of the kernel (`meta.workspaceId` + `meta.cwd`); plugin-owned settings no longer seeded; `scripts/core-size.mjs`; the one-off migration `scripts/migrations/001-storage-port` (rehearsed on a read-only copy of the owner's database: every count, the messages' SHA-256 and the ledger totals equal before and after); the Ideas legacy cutover deleted.

**Where it differs from the text above:**
- A working-directory plugin hook and a fork filter were not built: the core understands `meta.cwd`, and plugins declare run-state keys with `ISessionStore.DeclareForkReset` (remembered by the session service).
- The Host does not reference the sqlite provider as a separate project: the built-in providers live in `src/NetPI.Host/Storage/` and the Host still references only `NetPI.Abstractions`.
- `IStorageAccess` (info and snapshot, a service) is how Backup and Diagnostics reach the store; `FeatureSet` is the core's typed bag on the tool and run contexts.
- The sqlite schema has no foreign key from a session to its project and message ids are never reused (both pinned by the conformance suite); a document key is a non-empty string; collection and field names are checked alike by both providers.
- Every plugin references `NetPI.Contracts` through `plugins/Directory.Build.props`, not only the ones that speak it.
- `BackgroundWork` lives in the Ideas plugin (its only user).
- The Host tests run the real session service over `memory`, and `NullDatabase` is gone; the Aux suite keeps `FakeSessionStore` (in `tests/NetPI.Aux.Tests/Harness.cs`), now only as a recording double for plugin tests that assert on what a plugin writes to a session, over the same `ISessionStore` interface.

**Port gaps the converters hit, left as they are:** no OR across conditions (two Ideas queries and one ledger query run several finds or an inclusion-exclusion), no group-by (the ledger keeps roll-up documents), no declared order by key beyond the default. A second provider has to implement exactly what the doc comments in `StoragePort.cs` say; the conformance suite is the check.

**Verified:** a solution build with zero warnings, the whole end-to-end suite (71 tests, with the browser UI tests), the Host (141), Agent (178), Aux (314), Tools (74) and Providers (58) unit suites, the storage conformance suite on both providers (104), `scripts/core-size.mjs`, 35 server boots with each plugin removed, a boot on the migrated copy of the owner's data (projects, sessions, workspaces, ideas, ledger, a fork of a 5,000-message chat and a backup all work), and one independent code review of the implementation whose defects are fixed.
