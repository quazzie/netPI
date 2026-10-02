# A clean kernel: replaceable storage, features as plugins

Date: 2026-10-02
Status: proposed (nothing implemented). Review snapshot: master at 59619d2.
Builds on: [plugin independence](2026-10-01-plugin-independence.md) (plugin ↔ plugin, done). This plan covers what that plan did not: the parts the **kernel** owns or enforces.

## Ground rules (decided 2026-10-02)

NetPI runs on one machine and nothing outside this repository depends on it. Therefore:

1. **Contracts may change whatever the design needs.** No additive-only rule, no compatibility shims, deprecated paths, mirrors, dual-writes or forwarders. A contract change updates every plugin, test and doc in the same change. (`AGENTS.md` and `HANDOFF.md` say so as of this branch.)
2. **Delete what you replace, in the same stream.** A stream is not done while the old path still exists.
3. **Only the owner's local setup is migrated**, by a one-off script run once, after a snapshot, with the owner's say-so (nothing in `%USERPROFILE%\.netpi` is touched without it). Migration code never ships in the product and is deleted once applied.
4. The principle behind the plan: a part that is replaceable in principle (storage is the example) must be replaceable in practice, and no specific plugin is required. The kernel is a small mechanism; every feature is a part.

## What was measured (2026-10-02)

- **35 real servers** were started in throwaway homes (all plugins, each plugin removed in turn, host only, two minimal sets). Every one boots and answers. With **no plugins** the kernel starts in 0.5 s with 36 core RPCs and the UI loads without console errors; Runtime plus one provider is a working chat path. So plugin ↔ plugin independence is sound.
- Four code reviews looked at what a boot probe cannot see. The failures are in what the kernel owns or enforces, and in the UI shell.

## What does not hold today

| # | Problem | Evidence |
|---|---|---|
| 1 | **Storage cannot be replaced.** Only the native SQLite library can be swapped. | `HostKernel.cs:70,84` builds the concrete `Database` and `SessionStore` before any plugin loads; plugins get those objects (156 `ctx.Sessions` and 21 `ctx.Db` uses, 2 registry lookups). `IDatabase` is raw SQLite SQL (83 call sites, 5 plugins; `Migrate` takes DDL). Backup runs `VACUUM INTO`. A plugin registering its own `ISessionStore` is ignored by almost everything: two stores diverge. |
| 2 | **Paid models need the Agents plugin.** | `ModelCatalog.cs:150` refuses non-local models unless something registers the empty marker `IBudgetGate`; only Agents does. |
| 3 | **Workspaces are kernel data**: table, `sessions.workspace_id`, RPCs, events. | `SessionStore.cs`; two sources of truth (`meta.workspaceId` vs the column). Without the plugin, `agent_spawn isolated` is silently ignored. |
| 4 | **Policy logic lives in the contract assembly** (`ModelMessages.Normalize`, `WorkspacePaths`, `DecisionHints`, `ToolSelection`, `ToolLists`, …). | Contract changes stage every plugin for a restart; a replacement still gets the old policy. |
| 5 | **Tool arguments are parsed 13 times** and guards match tool names. | A guard can judge a different call than the one that runs (the bypass fixed in 9efaf19); an unknown write/exec tool is unguarded; a throwing before-tool-call hook lets the call run. |
| 6 | **A replacement can lose silently.** RPCs resolve to the last registrant, services by priority. | `RpcRegistry.cs:56` |
| 7 | **The UI shell hard-wires 11 plugins' features** (22% of `web/src`), has no slot extension points, absence handling is accidental. | |
| 8 | Smaller: fork policy hard-codes other plugins' meta keys; the host seeds settings for plugins that may not exist; `IPluginContext` carries domain handles; `IHttpRegistry` is unused in the product; "typed" capabilities are JSON with an undocumented schema. | |

## Target architecture

### 1. The kernel is mechanism; everything else is a part

**Kernel:** plugin host and lifecycle, the registries (services, tools, RPC, UI, events), transport and auth, settings (the bootstrap root), logging, paths and the home lock, the **model catalog** (aggregator plus middleware pipeline), and the **session domain service** over a storage port (below). Nothing else.
**Parts:** every feature, including storage providers, model providers, the agent loop, workspaces, budget, ideas, tools. A part is present or absent; an operation that needs one reports it as unavailable by name.

### 2. The plugin context carries only the mechanism

`IPluginContext` shrinks to: `PluginId`, `PluginDirectory`, `Paths`, `Logger`, `Stopping`, `Events`, `Services`, `Rpc`, `Tools`, `Ui`, `Settings`. `Db`, `Sessions`, `Models`, `Http` are removed. Every capability, kernel-provided or plugin-provided, is a service resolved per use: `Services.Get<T>()` or `Services.Require<T>()`, which throws one uniform `CapabilityUnavailable(contract)` that tools, RPCs and the UI map to the same explicit "unavailable: X". This also lets the independence gate enforce "no hidden coupling" mechanically.

### 3. Storage is a port with providers

```
IStorageProvider  { Id; OpenAsync(StorageContext) → IStorage }          chosen by `storage.provider`, at startup only
IStorage          { Sessions: ISessionRepository, Kv: IKeyValueStore, Data: IPluginData, Blobs: IBlobStore,
                    Snapshot: IStorageSnapshot, Info }                  Info = provider, durable, location (for diag)
```

- `ISessionRepository` is the small, portable persistence contract for sessions, messages (append-only per session with a sequence) and projects. The kernel's `SessionService` (events, transient sessions, the context cache, titles, the 16 event types) sits on top of it, so a provider implements ~15 persistence methods and none of the behaviour.
- Providers: `sqlite` (today's code, moved into its own assembly inside the app, default) and `memory` (ephemeral: `--ephemeral`, tests, demos). Out-of-tree providers load from `<app>/storage/<id>/`. Not hot-reloadable by design (a swap conflicts with a single-writer store and the home lock); none present means the kernel starts `memory` and says so loudly.
- **Plugin-owned data (`IPluginData`)** replaces raw `IDatabase`/`Migrate(DDL)` (decision D1): typed collections with declared indexed fields, keyed get/put/delete, equality/range/IN filters, ordering and limit, group-by sum/count, an atomic increment/compare-and-set, and transactions scoped to one plugin. SQLite implements it natively; `memory` trivially. Ideas, the ledger, Context, Runtime's agent store and Workspaces are rewritten on it, one plugin per commit, **each deleting its SQL**; at the end `IDatabase` is gone from the contract.
- Backup becomes `IStorageSnapshot` (the provider snapshots itself) plus plugin contributors for files; the restore script dispatches on the manifest's provider. Idea images move to `IBlobStore` (closes the "backups omit images" half of idea-3m2h1g structurally).

### 4. The contract assembly is interfaces and plain data only

Policy moves to its owner, no forwarders: `ModelMessages.Normalize` → the model catalog (its only caller); `WorkspacePaths.CheckMutation/Refusal` → the Workspaces plugin as `IWorkspaceGuard`; `DecisionHints` and the JSON `IDecisionService`/`IGitHistory` bodies → typed records owned by the Decide and Files contracts; `ToolSelection` (with the Agents depth rule), `ToolLists`, `DeferredTools` → the Runtime/tool-selection service; `SessionModel` stops reading `agents.<id>.model`; `ResourceLeases` keeps its interface only. Constants shared by two plugins (prompt section ids, cancel reasons, the notice-provenance sentence) become named contract constants.

### 5. Tool arguments are normalised once; guards read roles, not names

`ToolDefinition.Parameters` is the single source of truth: name, aliases, type, required, and a **role** (`path`, `command`, `cwd`, `url`, …), plus `Effect` (`Read|Write|Exec|Net`). The runner resolves every call into a typed `ToolArguments` before any hook runs; hooks and the tool receive the same object. The 13 readers, the guards' name sets and the guard-vs-tool mismatch disappear; the model-facing JSON schema is generated from the same definition. A throwing before-tool-call hook **blocks** the call (D7). Guards act on `Effect` and roles; a tool that declares nothing and is not read-only follows `guardrails.unknownTools` (`allow` | `ask`).

### 6. Money: the kernel requires metering for paid models, as a real contract

Delete the empty `IBudgetGate` marker. `IModelMiddleware` gains `MetersPaidCalls`; the catalog refuses a paid model unless some registered middleware meters, or `models.allowUnmetered` is set (an explicit, visible opt-out; D3). Agents' ledger middleware is the built-in implementation, but any plugin can be one.

### 7. One owner per RPC, one error taxonomy

Each RPC method has one owning plugin; a second registration needs a priority and the loser is logged; `rpc.list` shows owner and priority. Error codes: `unknown_method` (absent RPC), `unavailable` (a capability is absent, naming it), `not_found` (an entity). The UI maps them; no client matches message text.

### 8. Session-scoped plugin data replaces the meta bag

The shared untyped `meta` JSON that plugins read each other's keys from, and the host's hard-coded fork-exclusion list, are replaced by: (a) a small typed core (`model`, `title`, `project`, identity, tools-off, prompt revision: the genuinely shared integration data) and (b) **plugin-owned per-session data** in each plugin's own collections, with `ISessionLifecycle` hooks (created, forked, deleted) the plugin implements to copy or drop its data. **Workspaces** become one such plugin: its table, bindings, RPCs and events leave the kernel; `SessionInfo.WorkspaceId` is gone; the working directory is project path or default unless the plugin's resolver says otherwise.

### 9. The UI shell is a slot host

The shell owns layout, the chat transcript, and slots: `composer.dock`, `composer.bar`, `topbar`, `settings.page`, `chat.row:<kind>`, plus a renderer registry for tool results and notices (`tools.list` carries `summaryArg`, `icon`, `view`; `details.blocked` replaces the `Blocked:` text prefix). Goal, Todo, Budget, Ideas recall, the Agents/Profiles/Backup editors, the context ring and the ask/guard cards are delivered by their plugins. **No built-in fallbacks:** a feature moves and its copy in `web/src` is deleted in the same change. Prerequisite: plugin bundles share the host's Svelte runtime (existing idea), so slot components share reactive state. `hello` carries a protocol version; `ctx.onCapabilities`, `UiTabInfo.Requires` and `ctx.openTab(key, params)` replace string matching and private localStorage keys.

### 10. Settings: plugins own their defaults

The host seeds nothing for plugins; every plugin declares its schema and defaults. Keys may be renamed freely (the local file is migrated).

## Decisions

| | Question | Recommendation |
|---|---|---|
| D1 | Plugin data: typed portable collections, or SQL kept as a declared optional capability (any SQLite-compatible engine only)? | **Typed collections**, gated by spike SP1 (below). They are the only route to a non-SQL backend. If the spike shows the port cannot express the ledger's atomic reserve or Ideas' queries cleanly, fall back to SQL-as-capability for plugin data only. |
| D2 | Session domain service in the kernel over a repository port, or sessions as a plugin? | **Kernel.** Sessions are the chat domain; they are provided as a service so they are never reached by `ctx`, and their persistence is replaceable. |
| D3 | Money guard in the kernel with an explicit opt-out? | **Yes** (§6). |
| D4 | Slim `IPluginContext` (a mechanical change across ~180 call sites)? | **Yes**, in the flag-day change, so no domain handle lives in the kernel contract. |
| D5 | Normalised arguments and roles (touches the runner, 14 tool classes, both guards)? | **Yes**: it removes the root cause of the guard bypass. |
| D6 | UI as a slot host with no fallbacks? | **Yes**, after SP2. |
| D7 | A throwing before-tool-call hook blocks the call? | **Yes.** |
| D8 | Freeze windows for the flag-day changes (other agents merge or pause first)? | **Yes** (see Working with other agents). |

## How: order, streams, spikes

Sizes: **S** a focused session, **M** one to two, **L** three or more. Every stream ends with the independence gate and its conformance suite green and with the replaced code deleted.

**Phase 0: foundations (parallel, ~1 week).**
- P1 `scripts/land.ps1 <branch>`: refuse if the main tree is dirty or the branch is not a fast-forward, then `git -C <main> merge --ff-only`. Ends the stale-main-checkout problem (idea-kcyxh2). S.
- P2 **Independence gate v2**: the 35-boot matrix as `scripts/independence.mjs` (on demand, ~3 min) plus a ~30 s subset in the merge gate (host only, Runtime+provider, −Runtime, −Agents, −Workspaces, −Context, −Ideas), with the probes the startup check lacks: a paid call with no metering, a spawn with isolation and no Workspaces, one real-host UI run with Runtime + one provider only. M.
- P3 **One-off migration convention**: `scripts/migrations/NNN-name.mjs`, dry-run by default, `--apply` takes a snapshot first, verifies after, refuses while NetPI is running; deleted after it has been applied and noted in `HANDOFF.md`. S.
- P4 The rule change (this branch) and a refresh of the independence plan and `PLUGINS.md` (Workspaces is the 31st plugin; extension points for the new contracts as they land). S.
- P5 **Two silent failures fixed now, without waiting:** `agent_spawn isolated` with no provisioner returns an explicit error; a throwing before-tool-call hook blocks. S each.

**Phase 1: two spikes (throwaway branches, ~1 week).**
- **SP1 plugin-data port.** Prototype `IPluginData` on SQLite and in memory; port Ideas' hardest flows (`ResolveCard`, `ClaimCheck`, list with filters and order) and the ledger's atomic `Reserve` and aggregates. Output: the port design, the query shapes it needs, and D1.
- **SP2 slot host.** Mount the Goal strip from a plugin bundle sharing the host's Svelte runtime. Output: the slot contract and D6.

**Phase 2: kernel contract, one coordinated change (the flag day, L).** Slim `IPluginContext`; `Require<T>`/`CapabilityUnavailable`; kernel capabilities as services; delete `Http`; contract purity moves (§4); one RPC owner and the error taxonomy (§7); `MetersPaidCalls`, delete `IBudgetGate` (§6); typed decision/git records; named constants. All plugins and tests updated in the same change.

**Phase 3: tools and guards (L).** §5: `ToolArguments`, roles, `Effect`; rewrite the 14 tool definitions; delete the 13 readers and the guards' name sets; `unknownTools`.

**Phase 4: storage (L, in this order, each mergeable).** (a) `ISessionRepository` plus `SessionService`: extract inside the host, guarded by the 40 existing session/workspace tests as characterisation (this class has a deadlock history: one extraction per commit, whole Host and Agent suites each time, no logic changes); (b) the sqlite provider assembly and `storage.provider` loading; (c) the `memory` provider and a conformance suite both providers pass; (d) `IPluginData` and one plugin per commit onto it (Agents ledger, Context, Runtime store, Ideas, then Workspaces in phase 5), each deleting its SQL; (e) `IStorageSnapshot`/`IBlobStore`, Backup and the restore script; (f) delete `IDatabase` and `Migrate`.

**Phase 5: workspaces and session-scoped data (L).** §8: lifecycle hooks, per-plugin session data, Workspaces owns its table/bindings/RPCs/events, delete the kernel's.

**Phase 6: UI (L, parallel with phases 4–5 once SP2 and phase 2 are in).** §9, one feature per commit (Goal, Todo, Budget first), deleting each built-in; renderer registry; protocol hello; error-taxonomy mapping; the dead Ideas absence check disappears with the code.

**Phase 7: cleanup.** Plugins own their settings defaults; typed capability docs; update the independence plan and docs; delete every applied migration script.

```
P0 ─ SP1 ─┐
    SP2 ─┤
         ├─ Phase 2 (flag day) ─┬─ Phase 3 tools/guards
         │                       ├─ Phase 4 storage ─ Phase 5 workspaces ─┐
         │                       └─ Phase 6 UI (needs SP2) ───────────────┴─ Phase 7
```

Rough scale: phases 0–2 are a couple of weeks of agent time; the whole program is several weeks, and phases 3, 4 and 6 can run in parallel worktrees once phase 2 has landed.

## Local migration (the owner's setup only)

Each is a one-off script under `scripts/migrations/`, applied by the owner after a snapshot, deleted afterwards. Product code never reads the old shapes.

| Script | When | What |
|---|---|---|
| 001 settings | phase 2 | rename moved keys, drop the seeded blocks for plugins that now own their defaults. |
| 002 session meta | phase 5 | move plugin keys (goal, todo, budgetAllowedFrom, guardrailsAllowed, agent*, …) from `sessions.meta` into the owning plugins' collections. |
| 003 workspaces | phase 5 | the `workspaces` table and `sessions.workspace_id` into the Workspaces plugin's collection. |
| 004 plugin tables | phase 4d | Ideas, ledger, Context and Runtime tables into their collections (a no-op where the SQLite provider keeps the same tables). |
| 005 idea images | phase 4e | files under `idea-images/` into the blob store. |

The SQLite provider keeps the existing core tables unless there is a reason to change them, so most phases need no data move at all. Old backup files stay restorable only by checking out the version that wrote them; say so in `BACKUPS.md` when the manifest changes.

## Working with other agents

About 25 worktrees exist and review fixes land on master continuously (46 commits since 943011b). Today only `netpi/fix-contracts-ui` (contracts + diag UI) and `netpi/fix-tools` carry unmerged work.
- The flag-day changes (phases 2 and 3) touch every plugin and will conflict with everyone: announce a window, let the other branches land first, make the change in one coordinated branch, land it by fast-forward immediately, then everyone rebases. Phases 4–6 are narrower and need only the owners of `fix-workspaces`/`diag-work-workspaces` (phase 5) and `fix-contracts-ui` (phases 2, 6) informed.
- Re-run the independence gate after every landing: another agent's merge can reintroduce an assumption.

## Risks

| Risk | Mitigation |
|---|---|
| The `SessionStore` extraction (4a) deadlocks or drops an event. | Characterise first; one extraction per commit; no logic changes; whole Host and Agent suites and the whole e2e per commit. |
| The live 200 MB database. | The sqlite provider keeps the core schema; a snapshot precedes every migration; tests only use temp homes; no migration runs without the owner. |
| The plugin-data port cannot express the ledger or Ideas cleanly. | SP1 decides before anything depends on it; the fallback (D1) is SQL kept as a declared capability for plugin data only. |
| Flag-day merge conflicts. | A freeze window, one branch, fast-forward landing, rebase for everyone else. |
| The one-connection global database lock makes a remote backend slow. | Out of scope: embedded providers first; a remote store waits for the lock to be lifted. |

## Not doing

- A portable-SQL translation layer, an ORM or LINQ over a raw SQL interface.
- A storage provider on the normal hot-reload lifecycle, or swapping storage at runtime.
- A remote database before the single global lock is lifted.
- Touching `ISettings`: it stays the bootstrap root (plugin dirs, logging and the storage provider are read from it before anything else).
- Compatibility of any kind with the shapes this plan removes.
