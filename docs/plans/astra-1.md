**Implement this as small, independently verified changes.** The target is a conversation view with tabs for open sessions, a global session picker, explicit project changes in the transcript, the existing collapsible right plugin panel, and a context-usage circle beside the composer controls. Activity is an optional plugin for agents and processes in that right panel.

This guide is based on a source review of the working tree, including uncommitted changes. It is an implementation plan, not a record of completed fixes or passing tests. Proposed names below are implementation choices, not existing APIs. The user approved the revised mockup with session tabs, the retained right panel, and a context circle. Preserve the existing collapsed thinking tag and its live one-line preview. All other chat blocks are in scope for functional fixes, performance optimization and larger visual/usability improvements where useful.

**1. Operating instructions for the implementing agent**

Complete one numbered work package at a time. For each package:

1. Read only the listed implementation files and relevant tests.
2. Record the behavior being changed and the acceptance cases.
3. Make the smallest coherent change.
4. Run focused tests, then build the affected projects.
5. Report changed files, verification and remaining work.

Preserve existing uncommitted work. Do not rewrite large files to make a small change. Do not introduce another application framework, database, message broker or background scheduler.

Cross-plugin contracts belong in `NetPI.Abstractions`; implementation belongs in plugins. Keep the permanently loaded host small.

Use deterministic fake providers for most tests. Real model calls are needed only for final smoke testing. Commit only completed, verified work; never push without instruction.

**Implementation order:** first complete P0–P1 below so rebuilding and publishing are dependable; then make the focused expanded-thinking fix in G0. Complete P2–P6 before relying on hot reload for the larger changes or enabling concurrent agents. Continue with A–F, G3's broader block work, G1–G2's navigation/context work, and H–I. Keep each change independently verified. P changes to the host or Abstractions require a coordinated restart to install; they cannot repair the already-loaded host in place.

**2. Establish the behavior before implementing it**

Use these rules consistently across storage, runtime and UI:

| Concept | Required behavior |
|---|---|
| Session | Independent conversation, visible in one global list |
| Project | Saved name and workspace directory |
| Project selection | Changes the selected session’s working context |
| New session | Created first; project chosen afterward |
| Project change | Persisted transcript event containing project context |
| Running session | Changes apply at a safe boundary, never halfway through a tool batch |
| Agent run | One execution associated with one session |
| Activity | Optional plugin showing agent runs and managed processes |
| Open-session tabs | Fast switching between the user's usual 2–3 sessions; retain tabs after runs finish |
| Navigation | Compact header, global session picker, no permanent left panel, retained collapsible right plugin panel |
| Context circle | Per-session context usage beside model/reasoning controls, with an information popup |
| Chat-history blocks | Audit and improve every block; preserve the collapsed thinking tag and its single live preview line |

Creating a session must not silently inherit the last session’s project. Allow an explicit “No project” choice; show the actual fallback working directory.

Selecting a project must never filter the global session list, open another session or erase history.

Keep the overall DSH-inspired visual language and compact, fast transcript. Replace left-panel navigation with session tabs and a global history picker. Preserve the right panel's plugin registry, rail, collapse/resize behavior and existing plugin views. The collapsed thinking tag with its one-line live preview is the specific protected design. User messages, assistant text, tools/output, expanded thinking, notices and project-context blocks may receive larger fixes and visual improvements. Use the mockup for navigation and information placement; evaluate real streaming/history states when improving the blocks.

**2a. Package P — Reliable builds, publication and plugin lifecycle**

**Review evidence and scope**

The immutable plugin-cache approach is correct and should remain. The desired guarantee is **the running application never maps assemblies from developer build/publish outputs, and an update never overwrites a live runtime directory**. It is not a guarantee that Windows releases every runtime DLL immediately after an unload request.

Read-only inspection on 2026-09-21 found the desktop executable and DLL mapped from `src/NetPI.Desktop/bin/Debug/net10.0-windows`, while the host and Abstractions were mapped from `~/.netpi/host`, and plugin assemblies were mapped from `~/.netpi/plugin-cache`. SQLite's native DLL was also mapped from a plugin-generation snapshot. This confirms desktop output remains exposed to build-lock failures even though plugin source DLLs have snapshot isolation. No running process was stopped, rebuilt or reloaded during this review.

Cache inspection also found copied `bin`/`obj` files, 14 Diagnostics cache directories, 6 Web directories, and about 102 MB under SQLite. Counts are observations, not proof every old ALC leaks: numeric generation counters restart and the sweep ignores higher-numbered directories. The loader recursively copying the whole plugin source directory is a separate, confirmed source of wasted I/O and cache size.

Primary files:

- [publish-plugins.ps1](C:/AI/Projects/NetPI/tools/publish-plugins.ps1), [keep-alive-host.ps1](C:/AI/Projects/NetPI/tools/keep-alive-host.ps1)
- [Desktop Program.cs](C:/AI/Projects/NetPI/src/NetPI.Desktop/Program.cs), [NetPI.Desktop.csproj](C:/AI/Projects/NetPI/src/NetPI.Desktop/NetPI.Desktop.csproj)
- [PluginManager.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Plugins/PluginManager.cs), [PluginLoadContext.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Plugins/PluginLoadContext.cs)
- [PluginInstance.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Plugins/PluginInstance.cs), [PluginContext.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Plugins/PluginContext.cs)
- [ServiceRegistry.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Services/ServiceRegistry.cs), [EventBus.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Events/EventBus.cs)
- [PluginManagerFacade.cs](C:/AI/Projects/NetPI/src/NetPI.Host/Services/PluginManagerFacade.cs), `CommandRegistry.cs`, `WebPanelRegistry.cs`
- Lifecycle/service-consumption code in Agent, Web, Tools, BackgroundTasks, Storage.Sqlite, AutoCompact and Diagnostics; existing plugin and reload tests.

Relevant design: PLAN §5–7 (loading, reload, ownership), §27–30 (process/storage ownership), §44 (leases), §48 (shutdown), §50 (failure cases). Source behavior currently differs from some comments and AGENTS.md guarantees; update those descriptions only after the new behavior is verified.

**P0 — Isolate desktop and host execution from builds**

1. Add one supported developer launch path that builds/publishes the desktop and host to staging, copies the complete payload to a new immutable runtime directory, then launches that copy. For example, `~/.netpi/app-cache/<build-id>/<launch-id>/`. Both desktop and host must execute outside repository `bin`, `obj` and publish outputs. Keep the project root and `NETPI_PLUGINS` explicit; `FindProjectRoot` walking up from an external runtime directory cannot discover the repo.
2. A fixed `~/.netpi/host` directory must no longer be refreshed in place. `SyncHostSnapshot` currently catches copy failures and tolerates old destination files, which can produce a mixed old/new payload. Stage and verify a complete new directory; if any copy fails, launch nothing from it. Never silently fall back file by file.
3. Unify desktop and keep-alive launcher selection of `NETPI_HOME`, plugin root, configuration and installed SDK. The keep-alive script currently overwrites `NETPI_HOME` and detects only `dotnet.exe` with one exact path, whereas Desktop launches `netPI.Host.exe`. Use a runtime-home ownership lock and a bootstrap identity check rather than process-name/path heuristics alone. Verify an existing listener is the expected netPI instance; an open TCP port is insufficient.
4. Reuse a healthy existing host only when its runtime identity/configuration is compatible. A host/Abstractions update requires an explicit restart operation, not hot reload of a plugin. Report the running build and pending build separately.
5. Fix Desktop's redirected-stream pump: it currently calls `ReadToEnd()` on stderr and then stdout inside one task. Read both concurrently with bounded logging; otherwise stdout can fill while stderr remains open, making the host look hung. Track and await pumps on exit. Review the PowerShell equivalent for runspace-safe stream handling.
6. In `NetPI.Desktop.csproj`, evaluate the host-file item list inside the copy target after the host build/publish has completed. It is currently created during project evaluation and can miss first-build files. Prefer publishing the launch payload as a coherent unit; resolve the WebView2 reference through NuGet/MSBuild paths rather than a hardcoded user directory.
7. Prune only unused runtime directories. Verify canonical cleanup targets stay under the owned cache root. Never require deletion of a locked old runtime directory before allowing a new launch/build. Do not kill unrelated `dotnet`/MSBuild processes or automatically terminate active agent runs to make a build pass.

**P1 — Publish complete immutable plugin artifacts**

Current script issues: an existing `bin/<configuration>/net10.0` directory bypasses publishing and is copied without proving it is current; only top-level DLL/deps files are copied on that path; removed dependencies remain staged; native assets are copied from a pinned NuGet version only when the destination directory is missing; failures are not consistently terminating and native command exit codes are unchecked. It still prints `staged`. The source directory is then recursively copied into the runtime cache, including source/build trees.

Implement a small manifest-based publication protocol, not a general deployment service:

```text
Build/publish into a new temporary artifact directory
  → validate complete payload and manifest
  → finalize immutable artifact directory
  → atomically replace the plugin's selected-build pointer
  → request reload of that exact build ID
  → verify active build ID and startup outcome
```

- Use `dotnet publish` for a complete dependency/content payload in an isolated output folder, e.g. ignored `.artifacts/plugins/<id>/<build-id>/`. Keep intermediate outputs outside the published payload. Preserve runtime asset subdirectories and explicitly included content; do not copy arbitrary source trees or just `*.dll`. Preserve PDBs when desired for debugging.
- Build by default. An explicit no-build mode must consume a previously completed manifest for the requested configuration, target framework and RID; the existence of a directory is not evidence of a current build. Build failure must leave the selected artifact unchanged.
- Use terminating PowerShell errors and check `$LASTEXITCODE` after every native command. Log actual output on failure. Print separate built/published/reloaded outcomes rather than treating a successful copy as activation.
- Support selecting specific plugins, and discover plugin projects from an explicit project marker or maintained manifest instead of a hardcoded script array. Exclude test fixtures from normal publication unless explicitly selected. Do not publish/reload unrelated plugins on each edit.
- A manifest should declare plugin ID, entry assembly, unique build ID, target framework/RID, shared-contract compatibility, required framework and file hashes. Validate required files and relative paths. Reject traversal/reparse escapes from the artifact root; private assembly references must not couple plugins to one another.
- A small `current.json` in the plugin's discovery directory can point to the finalized artifact. Replace it atomically on the same filesystem after validation. Serialize publishers per plugin; concurrent publish/reload must see either the old complete artifact or the new complete artifact. Resolve the pointer once per operation and pin that build, so a later publish cannot change a reload already in progress.
- Update discovery to recognize the manifest/pointer without scanning dependencies as plugin candidates. Support old staged folders through a clearly bounded migration path; the old recursive loader cannot safely consume the new layout. Install the packaging/loader changes together with a host restart. Legacy folders should be copied through a runtime-file allowlist, excluding `bin`, `obj`, source and caches.
- Snapshot only the validated artifact into a unique runtime directory, e.g. `plugin-cache/<host-instance>/<plugin>/<attempt-id>-<build-id>/`. Reserve attempt IDs even for failed loads; never delete/reuse a generation directory on retry or startup. Copy into a temporary snapshot and finalize before constructing an ALC.
- Keep last-known-good artifacts for rollback. Cache pruning uses explicit active/retired ownership and retention metadata, not restarted numeric counters alone. A locked/deferred cleanup is diagnostic information, not a failed publication or reload. Use a runtime-home lock or another verified ownership check before pruning another host's files.

Build and publish must remain possible while the old plugin is busy. Activation may legitimately wait for leases/agent idle; present those as separate stages.

**P2 — Serialize lifecycle changes and implement truthful recovery**

Confirmed issues: `ReloadAsync` checks and changes state without serializing the entire operation; `NextGeneration` only advances on successful registration; snapshot/ALC construction happen outside the load-error handler; the idle-gate cancellation source is not applied to the awaited delegate; lifecycle cancellation tokens are cooperative rather than enforced time bounds. A new generation is marked Active before `StartAsync` succeeds. Start failure still returns a non-null instance, and the facade interprets non-null as success. Scan also adds IDs after start failure.

Use one small host-owned lifecycle operation queue initially. Serialize load/scan/reload/reload-all/shutdown mutations, including public load paths. This is simpler than parallel plugin updates and adequate for this harness. Ordinary agent/tool execution remains concurrent. Coordinate shutdown with the queue and reject new operations after shutdown starts.

Return structured outcomes, for example:

```text
OperationId, PluginId, RequestedBuildId, ActiveBuildId,
Phase, Outcome, Error, RestartRequired

Outcome: Applied | Unchanged | Deferred | Failed | RolledBack | RestartRequired
```

Keep availability, operation progress and ALC collection as separate facts. “Update failed, previous build active” is different from “plugin unavailable.” Do not emit a generic Failed plugin state just because reload was deferred while work is running. `ReloadAll` must return per-plugin outcomes, not the number of current plugins.

Recommended reload sequence:

1. Pin/validate the candidate artifact and shared ABI before touching the old generation. Determine whether a host restart is required.
2. Check the relevant idle policy before denying new work. Atomically close the generation's admission gate and drain in-flight work. Block new agent starts during AgentIdle transitions to avoid an idle-check/start race. Never stop a plugin before drain completes.
3. If cancellation/drain timeout occurs before stop begins, restore the actual previous state (including Failed if that was the starting state) and report Deferred/Cancelled as appropriate.
4. Stop and cleanly retire the old generation, retaining its immutable artifact and compatible config snapshot for rollback. Existing same-port Kestrel plugins cannot run old and new listeners simultaneously.
5. Load the candidate with generation-owned registrations, then start it. Mark Active and report Applied only after successful startup/readiness. Keep Loading/Starting unavailable to ordinary callers; use explicit startup coordination for declared dependencies.
6. On candidate load/start failure, perform full partial-initialization cleanup. If the old generation was already stopped, instantiate the last-known-good artifact as a fresh generation and start it. Report RolledBack only if that recovery succeeds. Otherwise report unavailable/Failed with both errors. Never claim the old running instance was preserved when it was already unloaded.

The current implementation stops/unloads old first, so comments promising that the previous generation always remains Active are inaccurate. Do not attempt to reload the candidate into the live service registry alongside old registrations; current duplicate-service rules reject it. Validation before stopping improves safety, but cannot prove every runtime startup succeeds. Rollback of code also cannot undo database migrations or external side effects: permit hot rollback only for compatible/reversible state changes; otherwise require a coordinated upgrade/restart.

Apply bounded awaits as well as cancellation signals to idle/lifecycle phases. A timeout cannot forcibly stop arbitrary in-process code. If `StopAsync` ignores cancellation or throws after partial shutdown, do not declare the generation healthy or start a conflicting listener blindly. Keep it quarantined/unavailable, observe outstanding tasks, and report RestartRequired if cleanup cannot be established. Process isolation for uncooperative plugins is a future option, not required for the current minimal harness.

**P3 — Correct leases, ownership and failure cleanup**

- Lease admission must be atomic with transition to Draining. Today `Acquire` checks state and then adds a lease in separate steps, and `AcquireSelfLease` admits work without a state check. Introduce owner-level `TryAcquireLease`/`TryBeginDrain` synchronization with a documented lock order. An already-admitted lease remains valid until its operation finishes; no new work enters after draining starts. Do not make `Value` unusable merely because its admitted owner is draining.
- `ServiceOwner.Current` is a process-global mutable static. Replace implicit shared ownership with explicit owner parameters through scoped service/event wrappers. Prefer that over relying on ambient state across concurrent callbacks. The scoped `AcquireSelfLease<T>` must bind the actual owner instead of ambient state left over from loading. Audit registrations created in `StartAsync` and later callbacks too.
- `Resolve<T>` currently returns an object after immediately disposing its lease. It cannot protect an asynchronous operation or a retained reference. Inventory cross-plugin consumers, including Web's cached `_store ??=`, runner/agent/catalog fields, Diagnostics, AutoCompact, and AgentRuntime's misleading `Acquire<T>` helper that actually calls `Resolve`. Hold operation-scoped leases through the relevant awaits, release them in `finally`, and re-acquire current services for subsequent operations. Do not replace this with permanent leases that prevent all reloads.
- Track actual implementation ownership for contributed tools. BackgroundTasks registers tool objects into Tools' registry: a lease on the registry alone does not protect the plugin implementing those tools. Associate contributed tools with their owner/generation and hold that owner's lease during execution. Stop callbacks/continuations from retaining a retired implementation indefinitely.
- Failed-load cleanup currently clears tracking dictionaries without removing service/event entries from the host and without explicitly initiating ALC unload. Replace it with one idempotent cleanup path used for partial Load, partial Start, normal unload and shutdown. Remove actual owned registrations/subscriptions/commands/panels, stop/dispose resources that were initialized, drop references and call `Unload()` after execution has quiesced. Wrap staging/ALC creation in failure handling too.
- Registration disposal must physically remove the matching entry/reference. `ServiceRegistry.RegistrationHandle` currently marks an entry removed while retaining its instance and service type. `EventBus.SubHandle` can leave an empty dictionary keyed by a plugin-owned event type. Clean these up without deleting newer replacements. Web-panel removal currently matches ID and URL, so an old handle could remove a replacement with the same values; use registration identity/generation. Use a consistent lock for command registration and removal.
- Event callbacks need an in-flight execution guard/lease around invocation, coordinated with drain. Removing a subscription does not cancel a callback already snapshotted for delivery. Fire-and-forget work launched by handlers must be explicitly owned, cancellable and awaited; do not assume a synchronous event handler means all of its work has finished.
- `WebPlugin.LoadAsync` currently starts Kestrel and subscribes/resolves services through `WebApp.StartAsync`; move active startup to `StartAsync` to match the documented lifecycle. Make partial-start cleanup release listeners/subscriptions even when start fails.
- Replace Storage's “never dispose the old store” workaround only after consumers hold proper leases and stop retaining unleased instances. Coordinate with Package B's connection lifetime changes. Then close owned resources deterministically after drain. Update `StoreReloadTests`: the invariant should be that valid operations finish and subsequent operations resolve the new store, not that an unleased old store remains usable indefinitely.
- Track agent tasks, process-exit continuations, Kestrel disposal and output pumps through shutdown. Never clear the lease table to conceal work that is still running. Surface retained ALCs with owner/build/age and bounded diagnostics; do not run repeated full GCs in normal reload code.

**P4 — Fix entry-assembly resolution and native-library lifetime**

`PluginLoadContext` currently constructs `AssemblyDependencyResolver` with a directory. Supply the full manifest-declared entry DLL path; that is the constructor's documented input. Load that entry assembly and resolve private dependencies on demand. The current discovery loop loads every top-level DLL and may instantiate multiple plugin implementations while claiming to use the first. Validate exactly one declared entry type/implementation and reject ambiguous artifacts.

Continue resolving Abstractions from the host/default ALC. Validate an explicit contract compatibility version before activation; assembly names alone do not prove compatibility. Host framework dependencies should use the host's declared shared frameworks. A generated plugin `runtimeconfig.json` does not install or activate a missing shared framework inside an already-running process. Fail clearly for an unsupported runtime/framework rather than masking the mismatch through bulk assembly preloading.

`PreloadNativeAssets` currently walks all RID folders and calls `NativeLibrary.Load` for each candidate without retaining/releasing handles. Repeated storage generations can therefore retain native mappings independently of managed ALC collection. Remove blanket preloading and select only native assets for the actual process RID/architecture.

First test normal `AssemblyDependencyResolver.ResolveUnmanagedDllToPath` plus ALC-owned native loading with the real SQLite package. If SQLite needs process-lifetime native initialization, make that an explicit narrow policy: load the approved native build once from a separate immutable, content-addressed native cache, reuse it for compatible managed reloads, and classify a native-library version change as RestartRequired. Do not repeatedly preload the same native library from new generation directories, or call `NativeLibrary.Free` while any connection/callback can still execute it. Managed plugin reload should not depend on deleting native binaries still mapped by the process.

Runtime facts to preserve: unload is cooperative; `Unload()` initiates collection rather than guaranteeing immediate file release. The resolver expects an assembly path. See [Microsoft's unloadability guide](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability) and [AssemblyDependencyResolver constructor](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblydependencyresolver.-ctor?view=net-10.0).

**P5 — Self-reload, dependencies and the operator workflow**

- Web must acknowledge an update request with an operation ID and hand execution to the host-owned queue before it stops itself. Reloading Web currently awaits the facade inside its own request path; an independent cancellation token alone does not establish independent task/lifetime ownership. After reconnect, query the host's operation result. Apply this to scan and reload-all as well; reload-all currently still uses the request token.
- Declare a small set of required/optional service dependencies or explicit startup ordering. Reload-all is currently alphabetical. BackgroundTasks registers tools into the current Tools registry at Start; reloading Tools replaces that registry and can make background tools disappear. Provide generation-aware re-registration after the dependency changes, or coordinate affected dependents. Do not restart BackgroundTasks blindly: its Stop currently kills its jobs.
- While background jobs are running, defer BackgroundTasks reload unless there is an explicit supported state-transfer protocol. Keep those jobs owned by their running generation until they finish, with lease/drain behavior that prevents accidental termination. Activity is presentation-only and remains freely reloadable. Agent/provider/storage/tool updates should report the precise blocking work rather than vague “reload failed.”
- Shutdown order must reflect actual dependencies/recorded start order. Ordering by per-plugin generation number is not reverse load order. Stop admission first, finish/cancel owned work according to policy, stop consumers before providers, then release services and ALCs. Apply bounded cleanup to partially started and Failed generations too.
- Expose active build ID, available build ID, generation/attempt, loaded path, stage/phase, blocking leases/work, last error, rollback result and RestartRequired in the existing diagnostics plugin view. Distinguish “active new build” from “old ALC still awaiting collection.” Bounded cache cleanup should never masquerade as an activation failure.
- One developer command should eventually perform build → publish → request update → wait for typed outcome. Keep each step separately callable for diagnosis. Use the existing control surface and host queue; no extra update daemon is needed. For frontend assets, stage a complete Vite output before switching its served root; do not expose a partially overwritten `dist` while Web is live.

**P6 — Verification required before calling updates reliable**

Extend the existing `PluginHotSwapTests` (including scan cases), `PluginFailureTests`, `StoreReloadTests`, registry and WebPanel tests. Existing tests prove managed snapshot copying/reload and some collection, but do not establish a clean live desktop rebuild, transactional publication, real native-library retirement, partial-registration cleanup, or successful recovery after Start failure. The current TestPlugin load-failure hook throws before registration; add failure points after registration/subscription and during partial startup.

Use isolated runtime homes and separate child host processes for lock/native tests. Do not stop the user's actual app or alter their database to verify this plan.

1. Launch the actual desktop/host from isolated runtime snapshots. Build changed host, desktop and plugin payloads while it remains open. Verify mapped application/plugin paths are outside source build outputs; a successful no-op incremental build alone proves little.
2. Publish version A then genuinely behavior-changing B with a changed private dependency/content asset. Verify running code stays A until reload, then becomes B. Test removed dependencies and added runtime assets; appending a byte to a PE only proves byte copying, not behavior/dependency replacement.
3. Inject build failure, failed file copy, missing dependency, corrupt manifest/hash and interrupted publication. The previous selected artifact stays complete and runnable. Concurrent publisher/scan/reload sees one coherent version, never a mixed payload.
4. Retry failed loads and restart the host with old cache directories present. No directory name is reused or overwritten. A locked retired snapshot does not block publication/startup; pruning respects live owners and rollback retention.
5. Race new service/self/tool/callback leases against drain with deterministic barriers. There must be no admitted work after the drain gate closes, no premature stop, and no leaked lease after cancellation/error. Serialize simultaneous reload/reload-all/scan/shutdown requests.
6. Fail after service registration, event subscription and listener startup. Assert counts return to baseline, ports close, no old callback executes afterward, no duplicate service remains, and a fresh retry works. Verify stale disposer handles cannot remove new registrations.
7. Fail candidate Load and Start after the old version stops. Verify rollback actually serves the old behavior under a fresh generation. Failed rollback must report unavailable/RestartRequired accurately. A start failure must never emit `plugin.reloaded` success.
8. Test a lifecycle callback that ignores cancellation. The operation must return a bounded honest outcome without launching a second conflicting generation or pretending cleanup finished.
9. Reload storage and exercise sessions through Web/Agent afterward. Verify new operations use the new generation and old resources can retire after legitimate leases release. Exercise SQLite native loading in a child process; repeated managed reloads must not create one permanently retained native mapping per generation.
10. Reload Tools and confirm contributed background tools remain available. Request BackgroundTasks reload with a live process: it is deferred without killing the process. Verify Activity-only reload has no effect on jobs/runs.
11. Reload Web from its own connection and reload-all from the UI. Verify acknowledgement/operation tracking, expected disconnect/reconnect and final build ID. Verify the operation survives the initiating socket closing.
12. Capture per-stage time, bytes/files copied, artifact size, active leases and retained ALC/cache counts over repeated cycles. Confirm snapshots exclude source, `bin`, `obj` and unrelated RIDs. Forced GC is acceptable in an isolated collection test, not as the production update strategy.

Do not promise that every change is hot-reloadable: managed plugin updates with a compatible contract should be; host/Abstractions/framework/native ABI changes require a coordinated restart. All of them should still be buildable while the existing app runs. Keep the process-per-plugin redesign out of scope unless a specific dependency proves incompatible with collectible loading.

**3. Package A — Repair context and run correctness**

Primary files:

- [AgentRunner.cs](C:/AI/Projects/NetPI/plugins/NetPI.Agent/AgentRunner.cs)
- [AgentRuntime.cs](C:/AI/Projects/NetPI/plugins/NetPI.Agent/AgentRuntime.cs)
- [AutoCompactPlugin.cs](C:/AI/Projects/NetPI/plugins/NetPI.AutoCompact/AutoCompactPlugin.cs)
- [AiProxyProvider.cs](C:/AI/Projects/NetPI/plugins/NetPI.Provider.AiProxy/AiProxyProvider.cs)

Do this before adding projects or concurrency.

**Context reads.** The runner’s fallback reads the oldest 200 entries. AutoCompact also reads from the beginning with a fixed maximum, so it can miss newer messages and newer compaction checkpoints.

Replace this with explicit storage queries:

- Latest compaction checkpoint for a session.
- Entries after a given sequence, read in pages.
- Recent entries in chronological order when a bounded fallback is necessary.

Find the latest checkpoint independently of history pagination. A safety limit must not silently substitute old history for current history. If context cannot fit and compaction is unavailable, return a clear error or apply a documented recent-tail policy.

**Responses system content.** `BuildResponsesPayload` currently selects only the first system message on a full request. Compaction reconstructs a separate system summary, which therefore gets omitted.

Collect every applicable system message in deterministic order. Ensure effective instructions are included on chained requests too; the current chained branch leaves `instructions` unset. Test payload construction directly.

**Message identity.** The runner persists the user message, then reconstructs another with a different ID. It also creates a fresh system-message ID on each run. IDs participate in Responses fingerprints.

- Reuse the stored user message and its ID.
- Remove text-based deduplication; identical consecutive user messages are legitimate.
- Give effective system context a deterministic identity based on its content/version.
- Derive compaction-summary identity from its persisted checkpoint.
- Do not weaken fingerprint checks merely to increase cache reuse.

**Run cleanup.** Runtime service acquisition and startup publication currently occur before the main cleanup `try/finally`. Ensure failures there release leases and clear state.

The runner should own exactly one terminal outcome: succeeded, failed or cancelled. Handle `AgentRunResult.Ok/Note`; cancellation currently can be returned as a result rather than thrown.

Also:

- Fail a send when initial persistence fails.
- Do not silently replace failed context construction with an empty prompt.
- Clear running state before publishing the terminal notification.
- Observe background execution tasks.
- On plugin stop, cancel and await owned runs before releasing them.

Relevant design: §10–12, §14c, §31–33, §44–45.

**Acceptance tests**

- A session longer than 200 entries includes its newest messages.
- A checkpoint beyond the old read limit is found.
- Responses payload contains base instructions and compaction summary.
- Identical user messages with different IDs remain distinct.
- Unchanged context preserves fingerprints across runs.
- Missing provider, startup exception and cancellation leave no active run or leaked lease.
- Every accepted run produces exactly one terminal outcome.

**4. Package B — Make storage ready for project transactions and concurrent runs**

Primary files:

- [Sessions.cs](C:/AI/Projects/NetPI/src/NetPI.Abstractions/Sessions.cs)
- [SqliteSessionStore.cs](C:/AI/Projects/NetPI/plugins/NetPI.Storage.Sqlite/SqliteSessionStore.cs)

The store currently uses one connection, and append allocates its sequence separately from insertion. Do not enable concurrent agents on top of this.

Use short-lived, pooled connections per operation. Initialize schema once, retain WAL, configure connection-specific settings consistently, and use transactions for multi-statement mutations.

An append transaction must:

1. Allocate the next sequence atomically.
2. Insert the entry.
3. Update session metadata.
4. Commit.

Use the same transaction pattern for project changes. Do not rely solely on an instance semaphore: plugin reload can leave old and new store instances alive temporarily.

Add explicit, versioned migrations. Test them against an existing database, not only a newly created one.

Suggested additions:

```text
projects
  id
  name
  workspace_path
  normalized_path_key
  created_at
  updated_at

sessions
  project_id                 nullable
  context_revision           integer
  pending_project_change     nullable serialized request
```

Preserve the existing workspace field during migration for compatibility. Once a project selection is applied, update that field in the same transaction.

Migrate distinct existing workspace paths into project records. Do not fabricate historical project-change messages. For an older session, insert one context-initialization event before its next run.

**Acceptance tests**

- Concurrent appends have unique, increasing sequences.
- A failed project transaction changes neither session metadata nor transcript.
- Migration preserves transcripts, titles, models and workspace paths.
- Canonically equivalent Windows paths do not create duplicate projects.
- Re-running migration is safe.

**5. Package C — Add project contracts and instruction snapshots**

Primary files:

- New `Projects.cs` in [NetPI.Abstractions](C:/AI/Projects/NetPI/src/NetPI.Abstractions)
- [ContextPiPlugin.cs](C:/AI/Projects/NetPI/plugins/NetPI.Context.Pi/ContextPiPlugin.cs)
- Storage plugin registration and implementation

Keep project persistence in the existing SQLite plugin. A separate project-management plugin is unnecessary for the initial implementation.

Suggested contracts:

```text
ProjectInfo
  Id, Name, WorkspacePath

ProjectContextSnapshot
  ProjectId, ProjectName, WorkspacePath
  InstructionSources[]
  EffectiveInstructions
  ContentHash
  CapturedAt

ProjectChangeRequest
  OperationId, SessionId, ProjectId
  ExpectedContextRevision
```

Use a stable `OperationId` to make retries idempotent. A repeated request must not create duplicate transcript entries.

Add an `IProjectStore` for project CRUD and an atomic session-project operation implemented by the same storage service. The atomic operation returns the resulting session and persisted entry.

**Instruction discovery**

The current implementation discovers `.netpi/AGENTS.md`; it does not read project-root `AGENTS.md`.

Define precedence explicitly. A practical compatibility rule, at each directory:

1. `.netpi/AGENTS.override.md`
2. `AGENTS.override.md`
3. `AGENTS.md`
4. `.netpi/AGENTS.md`

Choose one file per directory, then layer directories broad to specific. Preserve the global home layer without adding it twice. This deliberately makes ordinary project-root `AGENTS.md` work while preserving the existing override location.

Separate global instructions from project-specific layers so the project message does not duplicate global AGENTS content.

Preserve existing `SYSTEM.md` and `APPEND_SYSTEM.md` semantics (§17). If a project changes effective system configuration, rebuild the system context and invalidate the provider chain accordingly.

Store the exact effective AGENTS text and source paths in the snapshot. Missing AGENTS files are valid; unreadable existing files should produce an actionable error.

Do not reread files on every streamed token or tool call. Resolve at selection and check for changes before starting another run. If instructions changed, append a context-refresh event before the next user input.

**Acceptance tests**

- Root `AGENTS.md` is included.
- Broad-to-specific layering and override precedence are deterministic.
- Global instructions occur once.
- Reopening a session uses its persisted snapshot until an explicit refresh is applied.
- Missing and unreadable files have different outcomes.

**6. Package D — Implement project changes as durable transcript events**

Add `EntryKind.ProjectContext`, with the snapshot in its payload. Update serialization, replay, UI conversion and active-context construction together.

Persist one structured event. Project it into one model-facing user message with a deterministic ID derived from the entry ID. Do not store a second duplicate message.

Example projection:

```text
Project changed to NetPI.
Workspace: C:\AI\Projects\NetPI

Use this workspace for subsequent relative tool paths.
These project instructions supersede the previous project's instructions.

<project_instructions>
...exact resolved AGENTS content...
</project_instructions>
```

Use the existing system prompt to explain the meaning of these application-generated context messages. Keep their provenance distinct in the UI.

**Fresh session ordering**

```text
System/global instructions
Project context message
First user message
```

**Existing session ordering**

```text
Existing conversation
Completed results from any executing tool batch
Project context change
Subsequent user/steering input and model work
```

Never rewrite earlier transcript entries when switching projects.

**Idle session**

Validate and snapshot first. Then atomically update session project/workspace/revision and append the context event. Broadcast only after commit.

**Running session**

Persist a pending change and show “Switch to X pending.” Apply it before the next model request, after all results from the current batch are stored.

At that boundary:

1. Resolve and validate the target snapshot.
2. Commit the project change.
3. Update the run’s workspace and effective context.
4. Add the event to the in-memory transcript.
5. Continue the run.

If the model finishes without another iteration, apply the pending selection after the run unwinds and before the session becomes available for another send.

A later pending selection may replace an earlier unapplied one; make this visible. On restart, retain the pending selection and apply it before the next run. Do not automatically restart the interrupted agent.

Serialize send, project-change and compaction operations per session so ordering is deterministic.

**Compaction**

The current project snapshot must survive exactly, not as a model-generated paraphrase.

- Preserve project-change entries inside the retained tail.
- If the active project event falls outside the tail, reconstruct it once from its persisted snapshot.
- Keep compact historical project markers where needed to explain older work.
- Never label obsolete instructions as the active project.
- Include pinned instructions and system content in token-budget calculations.

**Acceptance tests**

- New session sees project context before its first user message.
- A→B switching preserves history and changes subsequent tool cwd.
- A tool batch started in A finishes in A.
- Duplicate operation IDs do not duplicate events.
- Failed selection leaves A active.
- Compaction, reconnect and restart preserve the exact active snapshot.

**7. Package E — Introduce session-specific run management**

Primary files:

- [AgentPlugin.cs](C:/AI/Projects/NetPI/plugins/NetPI.Agent/AgentPlugin.cs)
- AgentRunner and AgentRuntime
- [IAgentRunner.cs](C:/AI/Projects/NetPI/src/NetPI.Abstractions/IAgentRunner.cs)
- [Agent.cs](C:/AI/Projects/NetPI/src/NetPI.Abstractions/Agent.cs)
- [AgentEvent.cs](C:/AI/Projects/NetPI/src/NetPI.Abstractions/AgentEvent.cs)

Start with the new structure while retaining a concurrency limit of one. Enable multiple simultaneous runs only after isolation tests pass.

The runner becomes the owner of:

```text
RunId → RunHandle
SessionId → active RunId
SessionId → steering/control queue
```

Each run gets its own runtime instance and mutable state:

- Cancellation source and execution task.
- Workspace and project snapshot.
- Transcript and usage counters.
- Provider/tool leases.
- Current state and terminal outcome.

The current runtime has shared `_activeSession`, `_state`, `_selfLease` and usage fields. Merely removing its “already running” check would be incorrect.

Expose a shared run-query contract:

```text
ListRuns()
GetRun(runId)
GetSessionRun(sessionId)
CancelRun(runId)
```

Run information should include ID, session, project snapshot, model, state, start/end time and terminal reason.

Keep `IAgentRunner.IsRunning` as an aggregate compatibility property: true while any owned run is active, including preparation and cleanup. The host’s existing idle reload gate can then remain simple.

Require session IDs for steering when multiple runs exist. Do not use a global “active session” fallback.

Add `RunId` to events without renumbering existing event enum values. Delayed events from a previous run must not change the current run’s state.

Use configurable `maxConcurrentRuns`, default **1**. Permit one run per session. Initially reject starts exceeding capacity with a clear message; a persistent run scheduler is unnecessary.

Audit shared services for concurrency, particularly context-file caching, provider state and SQLite access.

**Acceptance tests**

- With limit two, sessions A and B run independently.
- Cancelling or steering A does not affect B.
- Two starts for the same session cannot both succeed.
- Limit one retains existing resource usage.
- Plugin stop awaits every owned run.
- Reload remains deferred until all affected runs drain.

**8. Package F — Correct WebSocket routing and reconnect behavior**

Primary files:

- [WebApp.cs](C:/AI/Projects/NetPI/plugins/NetPI.Web/WebApp.cs)
- [ws.ts](C:/AI/Projects/NetPI/web/netpi-web/src/ws.ts)
- [store.svelte.ts](C:/AI/Projects/NetPI/web/netpi-web/src/store.svelte.ts)
- [types.ts](C:/AI/Projects/NetPI/web/netpi-web/src/types.ts)

Suggested commands:

| Command | Purpose |
|---|---|
| `project.list/create/update` | Manage saved projects |
| `session.project` | Select or clear the session project |
| `session.project.refresh` | Refresh instruction snapshot |
| `runs.list` | Obtain active/recent runs |
| `agent.cancel` with `runId` | Cancel one specific run |

Add explicit project-pending, project-applied and run-update events.

Add session-scoped context information to session-open/bootstrap snapshots and publish updates when a model request is assembled, provider usage arrives, or compaction completes. Include the session/run identity and a context revision so late events cannot overwrite a newer session's meter. Carry effective context-window size, estimated current input tokens, last provider-reported input tokens and their request identity, reserve/compaction threshold, compaction availability, and latest compaction metadata. Category estimates (system/tools, project instructions, conversation, tool results) are optional and must have a declared, non-overlapping counting convention. Reuse existing usage and compaction events where sufficient; do not add a polling endpoint just for the circle.

The current client applies `agent.state` globally and applies every `session.updated` as the selected session. Separate those responsibilities:

- Update global session metadata regardless of selection.
- Replace the visible session only after explicit create/open navigation.
- Apply transcript events only to the matching selected session.
- Keep lightweight run summaries for other sessions.
- Key transient assistant/tool state and delta batches by session and run.

On session open, return persisted entries plus a snapshot of the currently streaming assistant, if present. Persistence alone cannot reconstruct an unfinished streamed message.

Use a snapshot revision/event cursor to reconcile live events arriving during replay. Test the race rather than relying on timing delays.

Re-resolve reloadable services at operation boundaries and hold appropriate leases. WebApp currently retains several service references, including a cached store, across operations.

**Acceptance tests**

- A background session update does not navigate the visible chat.
- Switching sessions during streaming does not mix output.
- Reconnect restores busy state and partial output without duplication.
- A delayed terminal event from an older run is ignored.
- Reloaded agent/storage services are used by subsequent operations.
- Opening a session restores its context meter; updates from other sessions cannot change it.

**9. Package G — Fix and optimize all chat blocks, preserve collapsed thinking, and update navigation**

**Package G0 — Fix expanded thinking streaming first**

Primary files:

- [ThinkingBlock.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/conversation/ThinkingBlock.svelte)
- [ConversationViewport.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/ConversationViewport.svelte)
- [AssistantMessage.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/conversation/AssistantMessage.svelte) (inspect integration; avoid unrelated renderer changes)
- [app.css](C:/AI/Projects/NetPI/web/netpi-web/src/app.css) (inspect both sets of thinking selectors)

**Observed implementation issue:** `.thinking-body` retains `max-height: 360px; overflow: auto` despite later style overrides. `ThinkingBlock.svelte` renders its text without binding or following this inner scroll container. `ConversationViewport.svelte` pins only the outer conversation, using tail changes, a list `ResizeObserver`, and a 30 ms timer. Once the thinking body reaches its height limit, its internal `scrollHeight` grows while its visible height and the outer list height can stay unchanged. Outer pinning cannot reveal the new thinking text. Reproduce this before editing; do not assume more outer polling will fix it.

Preserve the existing collapsed thinking tag with its one-line streaming preview, compact colors/spacing and recognizable disclosure treatment. For the isolated G0 fix, retain the expanded body's current presentation so scroll behavior can be verified without unrelated layout changes. Its size, controls and presentation can subsequently improve in G3; the expanded body is not a protected design. Keep timing information available.

Implementation steps:

1. Bind the expanded thinking-body element. Own its follow state locally in `ThinkingBlock`; keep it separate from the outer conversation's follow state.
2. Track thinking identity, text revision/length, expanded state and completion. After Svelte applies the text (`tick()`), set the inner `scrollTop` to `scrollHeight - clientHeight` only when inner follow is enabled. Avoid smooth scrolling on a live stream.
3. Decide follow intent before content growth. An append can push the bottom away without any user gesture; that must not disable follow. Distinguish programmatic scroll events from actual user scrolling, and let real wheel/touch/keyboard scroll-up stop inner follow. Returning near the bottom or choosing a small “Follow latest” control resumes it.
4. When opening a currently streaming block, start at its latest text. While it remains open, preserve manual scroll position until the user resumes follow. Opening completed thinking starts at the beginning for reading; completion itself must not jump a reader who scrolled up.
5. Treat “Keep thinking open” as a default, with a per-block manual override. The current `ui.keepThinkingOpen || (thinking.done && userOpen)` prevents collapsing when the setting is on and prevents manually opening a live collapsed block. Use an explicit unset/open/closed override, preserving the existing compact visual treatment and keyboard-accessible disclosure control. Keep the collapsed live preview unchanged in appearance.
6. Coalesce follow work per rendered update; do not create one timer or observer per completed block. Clean up any scheduled work/listeners when the body closes, the block unmounts, or the session changes. Guard delayed `tick()` continuations against a replaced/detached body or changed block identity.
7. Keep outer conversation following independent: pin the outer viewport only if it was already following. An inner scroll must not trigger history pagination or pull a manually scrolled outer viewport back down. Expansion/collapse should preserve the appropriate visible anchor.
8. Retain a post-update path that does not rely solely on `requestAnimationFrame`, which is throttled in background WebView2. On visibility restoration, settle the currently visible streaming body only if it was following. Do not force-scroll a reader on focus changes.

Optimize only after reproducing and fixing the inner-container defect. Keep reasoning as plain text and preserve stable block identities. Avoid full-transcript scans or Markdown re-parsing on thinking deltas. Profile `latestLine` for long thinking text and update only the required suffix if it is material; retain the same single-line output. Do not increase the outer timer rate. Reassess its need independently after the targeted fix.

**G0 acceptance checks**

- Stream enough multiline reasoning to exceed the 360 px body limit. With “Keep thinking open” on, the newest line remains visible throughout the stream and at completion.
- Start collapsed: the existing tag/one-line live preview stays visually unchanged. Expand mid-stream, collapse again, and reopen; the body follows the latest line without duplicate content.
- Scroll up inside expanded thinking while tokens arrive: it stays where the user is reading. Resume follow or return to the bottom: it follows again.
- Scroll up in the outer conversation: thinking updates do not pull the whole chat down or fetch older history accidentally.
- Toggle “Keep thinking open” during streaming; manual expand/collapse still works. Completed reasoning remains readable from its beginning when opened later.
- Switch between streaming sessions, resize the right panel, and hide/restore the desktop window: only the intended visible session follows, with no stale timer/listener or cross-session scroll writes.
- Preserve tool output following, older-history anchors, final text layout and file-link behavior.
- Use a deterministic streamed-thinking fixture and real browser/WebView2 layout checks. Assert the inner bottom gap is within a small rounding tolerance when following and that scroll-up remains stable across appended chunks. DOM-less tests cannot validate element geometry. Include long text, unbroken lines and bursty deltas.

**Package G1 — Session tabs and retained right panel**

Primary files:

- [App.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/App.svelte)
- [HarnessHeader.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/HarnessHeader.svelte)
- [LeftPanel.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/LeftPanel.svelte)
- [RightPanel.svelte](C:/AI/Projects/NetPI/web/netpi-web/src/components/RightPanel.svelte)
- [ui.svelte.ts](C:/AI/Projects/NetPI/web/netpi-web/src/ui.svelte.ts)
- [app.css](C:/AI/Projects/NetPI/web/netpi-web/src/app.css)

Target arrangement:

```text
netPI   Sessions   New                              Activity  Settings
[● Session A ×] [● Session B ×] [○ Session C ×]
──────────────────────────────────────────────────────────────────────
Session title                   Project ▾  | Right plugin panel | Rail
                                          | Activity           | A
              Conversation                | Diagnostics        | D
                                          | Existing plugins   | …
Composer: model · reasoning    context ◔ ↑  |                    |
```

Implement a small set of components:

| Component | Responsibility |
|---|---|
| `SessionPicker` | Searchable global list, pagination, rename/delete |
| `SessionTabs` | Open-session navigation, selected tab, run/unread indicators, close tab |
| `ProjectPicker` | Select/create project, display current path |
| `SettingsDialog` | Existing appearance and behavior preferences |
| Existing `RightPanel` | Preserve plugin rail, resize/collapse behavior and registered plugin views |
| `ContextUsage` | Small context circle and detail popup beside composer controls |
| `ProjectContextBlock` | Compact project-change row with expandable instructions |

Keep the composer’s existing model/reasoning controls rather than duplicating them in the header.

Tabs represent **open sessions**, not just currently running agents. Keep tab order stable; completing a run must not move or remove its tab. Show running, finished/unread, failed and selected states without animations that continually demand attention. Distinguish closing a tab from cancelling a run or deleting a session. Closing a running tab leaves its run visible in Activity and the global picker; opening it again restores that session. If the last tab closes, show an empty start state without creating a database session until requested.

Persist open-session IDs and the selected ID as lightweight UI state; discard IDs for sessions that no longer exist. Preserve per-session drafts, outer scroll position/follow intent and disclosure state across tab changes, with bounded storage. Restore the current run snapshot before following new output. For many tabs use a compact overflow list; keep the common 2–3-session case immediately accessible and support keyboard switching. Never filter tabs or history by project selection.

Session picker rows show title, project badge, updated time and running state. Search must cover stored sessions through the server, not only the first loaded page.

Remove only the left slot, left-panel resizer and obsolete left navigation preferences after the picker and settings dialog work. Preserve the right slot, rail, resizer, saved width, open state and selected plugin tab. Migrate existing UI preferences while preserving font and tool/thinking settings. Avoid retaining a second “active sessions” list in a permanent left panel; tabs already serve that purpose.

Continue using `IWebPanelRegistry` and the existing right-panel rendering. The header Activity action selects/opens the Activity plugin in that panel. Existing Diagnostics and BackgroundTasks views remain accessible through their registrations; no hardcoded plugin content belongs in the shell.

Keep plugin content lazy-mounted as today, and preserve lifecycle cleanup. Provide Escape, focus restoration and keyboard navigation for pickers/popups. On narrow windows, move secondary header actions into an overflow menu and allow the right panel to overlay/collapse so chat stays usable.

Preserve:

- Incremental Markdown rendering.
- Older-history scroll anchoring.
- Manual scroll-up during streaming.
- File-link behavior.
- Tool and thinking expansion preferences.
- The compact collapsed thinking tag and one-line streaming preview. Other blocks may change substantially under G3; retain their content, ordering, accessible controls and working interactions rather than freezing their current layout.

**Package G2 — Context circle and information popup**

Place a small circular meter beside the existing composer model/reasoning controls. Its accessible label states usage and opens the same details as a click; the details cannot be hover-only. The circle describes the selected session's current model context, not cumulative session tokens or daily usage.

Show in the popup:

- Input/context tokens used versus the selected model's context window, plus percent used.
- Estimated breakdown: system and tool definitions, project instructions, conversation, and tool results. Clearly label estimates; do not fabricate precision if a category is unavailable.
- Configured reserve, room before automatic compaction, and the effective trigger threshold from the runtime's policy.
- Last compaction time or turn and a “Compact now” action using the existing `session.compact` path.

Derive displayed values from Package F's session-scoped context snapshot. Prefer provider-reported input usage for the matching last request, supplemented by a labeled estimate for newly appended content. Do not add cached tokens again: cached input is already part of input context. Do not label the whole AutoCompact reserve as provider output allocation unless those values actually match. Keep categories disjoint, total the same context used by the runtime, and distinguish last measured usage from the current estimate.

For the existing simple policy, `trigger = contextWindow - reserveTokens`, `remaining = max(0, trigger - currentInputEstimate)`, and ring percent is `currentInputEstimate / contextWindow`; use the effective policy if it changes. Unknown limits/usage display “Unknown” or “Estimating,” never a misleading zero. Sample numbers from the mockup must not become production constants. Show disabled/unavailable auto-compaction honestly.

Update at coalesced usage/context events, not a new high-frequency timer. Refresh after compaction, model changes, project changes and session switches. Surface failed compaction without presenting a lower usage value. Disable “Compact now” during an incompatible active operation unless the runtime supports safely queuing it at a turn boundary; do not compact concurrently with an executing tool batch or another compaction.

**Acceptance checks**

Fresh session → select project → send → switch project → continue → reopen session. Verify the global list, transcript markers, selected project and working directory throughout.

Also check narrow windows, keyboard-only use, long titles and hidden/background WebView2 scrolling.

With three tabs open, switch repeatedly while two sessions stream; drafts, project, context meter, scroll intent and thinking disclosure state must remain session-specific. Complete or close a tab and verify the run/history semantics above. Confirm existing right-panel plugins still load, resize, collapse and hot-reload. Check context display with unknown usage, cached input, changed model limits, a failed compaction and unavailable AutoCompact. Run the G0 scrolling regression checks again after the shell changes.

**Package G3 — Audit, fix and optimize every chat block**

This is required work, not an optional styling pass. The only visually protected block treatment is the collapsed thinking tag and its live one-line preview. Improve other blocks as needed for correctness, legibility, useful disclosure and speed; maintain a compact conversation instead of turning every message into a large card.

Inspect `components/conversation/BlockRenderer.svelte`, `UserMessage.svelte`, `AssistantMessage.svelte`, `ToolCallBlock.svelte`, `ThinkingBlock.svelte`, `ConversationViewport.svelte`, `AgentActivity.svelte`, `store.svelte.ts`, `types.ts`, `ws.ts`, and the corresponding CSS. Also inspect server live-event/replay conversion when a defect is in the data rather than the component.

Begin with a compact block/state checklist: initial, streaming, complete, failed, cancelled/interrupted, restored from history, and switched away/back. Record actual defects and implement small verified fixes by block type. Do not assume every state applies to every block, or restyle a working block without a usability reason.

| Block | Required review and intended improvements |
|---|---|
| User message | Preserve multiline text, indentation and long paths; handle large pastes without breaking chat width; keep selection/copy usable. Reconcile optimistic submission with the persisted entry by identity so rejected sends do not look accepted and reconnect does not duplicate messages. |
| Assistant text | Preserve content across streaming/final transitions, retries and replay. Check paragraphs, lists, tables, code fences, long lines and incomplete Markdown. Improve readability and code/copy controls where useful while retaining sanitization and stable-prefix rendering. |
| Thinking | Preserve the collapsed tag/live line. Apply G0 follow behavior; improve the expanded reading surface and controls where helpful. Preserve full reasoning, duration and explicit user expansion/follow choices across updates. |
| Tool header/input | Show a concise operation/path/command with an explicit lifecycle state. Keep partial arguments readable without claiming incomplete JSON is valid. Allow manual disclosure regardless of the global default, and avoid parsing or pretty-printing large inputs repeatedly when collapsed. |
| Tool output | Distinguish streamed output from completion. Follow the actual output scroller only when requested; preserve manual reading, selection and final output. Handle empty output, failures, cancellation and interruption clearly. Bound rendered content with explicit truncation/load-more behavior and a route to available full output; never silently lose results. |
| System/project/compaction notices | Use compact, distinct entries with expandable detail where needed. Preserve chronology and show what changed without disguising application events as user prose or successful assistant work. |
| Standalone/replayed tool blocks | Render with equivalent status, detail and output behavior to live tool calls. Avoid a degraded raw-text fallback merely because the entry was loaded from storage. |

**Concrete issues to reproduce from this review**

- `ToolCallBlock` derives `running` from `call.result === undefined`. A `tool.output` event populates the result before `tool.completed`, so a still-running tool can lose its running state as soon as output arrives. Introduce explicit lifecycle state driven by start/completion/failure/interruption events; output presence and duration are not completion signals. Update live and replay conversion together, with a compatibility mapping for older persisted entries.
- `expanded = userOpen || ui.keepToolsOpen` means a user cannot collapse a tool when “Keep tool calls open” is enabled, despite the comment saying manual toggles win. Use an unset/open/closed override, as with thinking, and preserve it across result updates.
- The tool file-link handler prevents the default action unconditionally. Preserve plain-click shell-open, but allow Ctrl/Meta/middle-click viewer behavior and prevent the enclosing disclosure from toggling when a link is activated. Review keyboard interaction for the link nested in the clickable header; use independent controls if necessary.
- `BlockRenderer` renders a standalone tool as a plain `sys-block`. Review history mapping and provide a consistent renderer for supported standalone tools; do not duplicate a result already attached to its assistant call.
- Tool input/output containers have their own overflow styles. Verify their scroll ownership just as in G0; following only the outer chat does not necessarily follow inner output.

**Shared implementation rules**

- Use stable persisted message/tool IDs. Repeated text is valid; do not deduplicate by content. Key transient state by session and block, and bound retained state for unmounted history.
- Define the live/replay block model once. Reopening a session must restore the same semantic status and content; presentation improvements must not alter model-visible transcript ordering.
- Give each scrollable surface one owner for follow behavior. Coordinate inner reasoning/output and outer history without unconditional scroll-to-bottom writes. Share a small helper only if their verified behavior is actually identical.
- Keep expensive formatting lazy and updates local to the changing block. Audit whole-string copies, repeated JSON parsing, growing transcript scans and DOM replacement. Avoid mounting hidden output or adding one interval per block. Render bounded windows for very large output only when needed, with honest limits.
- Keep existing Markdown sanitization and correct file-link behavior. Project switching introduces an additional path concern: a link in an older block should resolve using that entry's original workspace, not whichever project the session uses now. Carry an appropriate workspace/context reference for new entries and document the fallback for legacy history.
- Larger block layout/control changes are authorized. Keep essential status and error details accessible, separate destructive actions from disclosure, and verify text selection, keyboard use and narrow layouts. Do not add noisy animations or status rows that repeat the same information.

**G3 acceptance checks**

- Stream thinking, assistant Markdown, partial tool arguments and shell output in one run. Only the changing content updates; the collapsed thinking preview keeps its existing appearance and behavior.
- A shell call remains “running” through multiple output chunks and changes state only on its terminal event. Test empty output, a zero-duration completion, failure, interruption and cancellation.
- Global “keep open” preferences set defaults; manual open/close choices survive new deltas and completion. Test both thinking and tools.
- Large multiline user text, code blocks, tables, JSON arguments and output do not cause page-wide horizontal overflow. Text/code/output can be selected and copied accurately.
- Live, paginated, reconnected and reopened history agree on content, ordering, statuses and tool-result association. No duplicate optimistic messages, orphaned output or falsely completed tool calls.
- Plain-click and modified file-link actions work without toggling the enclosing block. After a project change, a new-format older entry still opens its original file.
- Scroll-up stays put in each scrollable region, resuming follow works, and expanding/collapsing content preserves history anchors. Repeat with the right panel resized and in hidden/restored WebView2.
- Compare render/formatting work and memory with the same long-transcript/large-output fixture before and after. Preserve improvements already present in incremental Markdown rendering. Document remaining limitations rather than claiming a broad performance win without measurements.

**10. Package H — Add the Activity plugin**

Create `NetPI.Activity`. It consumes the run-query service and existing background-job service through Abstractions.

Register it with the existing right-panel plugin registry. Its view opens in the retained right panel, not a replacement full-screen Activity overlay. Selecting “Open session” selects or adds that session's top tab. Keep the plugin useful even for runs whose session tabs were closed.

It owns presentation and its subscriptions. It does not own or terminate agent runtimes when unloaded.

One view contains two sections:

| Agents | Processes |
|---|---|
| Session/project | Command/project |
| Model | Shell and PID, when available |
| Current state | Running/exited state |
| Elapsed time | Elapsed time |
| Open session / Cancel | Output / Stop |

Extend background-job metadata with optional `SessionId`, `RunId`, `ProjectId` and original working directory. Capture ownership when the process starts. A later project switch must not relabel an existing process.

Include foreground shell processes too if the view is described as **all managed running processes**. Expose lightweight lifecycle information from the tools plugin; do not enumerate unrelated operating-system processes.

Use event updates for agents. For processes, add lifecycle events or use modest polling while Activity is open. Stop polling when closed.

Keep process output bounded and cursor-based. The current `Slice` implementation copies the entire retained string before slicing; remove that unnecessary allocation before considering a more complex buffer.

Retain only a bounded recent-completions list. Opening a job should load its output on demand.

Follow the existing plugin-hosted view pattern first. Sharing HTTP hosting can be a later measured optimization.

**Acceptance tests**

- Activity lists runs from every session.
- Cancel/stop targets only the selected item.
- Project switches do not change old process ownership.
- Activity unload/reload leaves work running.
- Missing BackgroundTasks or Activity plugins do not prevent normal chat.

**11. Package I — Measure and optimize the completed path**

Use the same fixtures before and after changes:

- Empty session.
- Long transcript with multiple compactions.
- Long streamed text/thinking output.
- Large shell output.
- Two simultaneous sessions.
- WebView2 foreground, background and restored.

Measure host readiness, session-open time, UI update latency, CPU, memory, retained DOM nodes and Responses chain reuse. Separate provider latency from harness overhead.

Optimize in this order:

1. Incorrect context reconstruction and unnecessary chain resets.
2. Unbounded output or retained run state.
3. Repeated transcript scans and whole-string copies.
4. Unnecessary UI work for non-visible sessions.
5. Scroll/layout work.

Keep current incremental Markdown rendering and the collapsed thinking tag/one-line preview. The known expanded-thinking defect is addressed early in G0; do not postpone that correctness fix until profiling. Audit all other block performance and correctness in G3, including larger presentation improvements where useful. Profile the 30 ms outer scroll-follow timer before replacing it; it exists to address hidden-WebView2 behavior and cannot fix separate inner thinking/output scroll containers. Any alternative must pass the G0/G3 regressions and retain the protected collapsed thinking presentation.

Do not add full transcript virtualization, persistent iframe caching or extra background services without measurements showing a need.

**11a. Remaining reliability gaps — fold into existing packages**

These are targeted corrections found during the final source pass, not additional subsystems. Implement them in the indicated packages and then stop expanding the review scope unless a regression reveals another defect.

**P0/B: make runtime-home isolation real**

`SqlitePlugin.DefaultDbPath()` uses `HOME`/the user profile plus `.netpi/netpi.db`, ignoring `NETPI_HOME`, although the host honors that variable. An isolated host can therefore still use the ordinary user database unless the database setting is explicit. Establish one effective runtime-home value and derive the default database, logs, host/plugin caches and config from it. Preserve explicit database overrides and document relative-path resolution. Do not silently migrate/copy databases when this changes. Test two runtime homes with no database override and prove their sessions are separate before using isolated instances for destructive/failure tests. Update P6's fixtures to specify a temporary database explicitly until this fix is installed.

**P/B: preserve config through bad edits and interrupted saves**

`ConfigService.MergePluginSection()` currently treats invalid JSON as an empty object, then overwrites the file with the modified plugin section. This can discard every other setting. Direct `File.WriteAllText` can also expose partial config during a crash or concurrent read. Reject updates to malformed config with a useful error; retain the original bytes. Validate a complete proposed config, write a sibling temporary file, then atomically replace the original with a recoverable last-known-good copy. Serialize read/modify/write across legitimate writers or use a revision check to avoid lost updates. Do not silently turn a parse failure into missing provider settings. Test malformed JSON, failed replacement, concurrent updates and interrupted writes. Keep credentials out of diagnostic diffs/errors.

**A/B: treat transcript persistence failure as a run failure**

The runner's initial message is not the only persistence risk: `AgentRuntime.AppendAsync()` catches all failures, logs a warning and continues. The runtime may then execute tools whose call entry was never saved, or request another model turn after losing results. Propagate persistence failure into an explicit failed-run state. Persist accepted input and assistant tool-call intent before execution; persist results before advancing. If saving a completed side-effecting tool's result fails, stop further work, report the uncertain recovery state and never automatically rerun that tool. Keep bounded recoverable output where practical. A database transaction cannot make arbitrary filesystem/process side effects exactly-once; document that limit. Test failures before tool execution and after tool completion, including cancellation and disk-full-style errors.

**F: receive complete WebSocket messages and bound transport work**

`HandleWsAsync()` currently allocates a 64 KiB buffer, receives once, decodes that chunk and parses it as a whole command without checking `EndOfMessage`. A long pasted prompt or fragmented UTF-8 sequence can therefore be rejected or corrupted. Assemble frames until `EndOfMessage`, enforce a configurable total message limit, decode UTF-8 only across complete bytes (or with a stateful decoder), handle close/binary messages explicitly, and return a clear oversized-message error. Do not assume one WebSocket receive equals one JSON message. Reuse/pool bounded buffers where useful.

Also review outbound backpressure: broadcasts currently await clients serially, and `SendSafeAsync` sends with the client's lifetime token rather than the operation's token. One connected non-reading client should not stall updates for every other client. Use bounded per-client delivery or bounded sends/disconnection with explicit resync after overflow; preserve ordering and never silently drop durable events or arbitrary text deltas. Test a slow reader, fragmented messages, multibyte splits and payloads above/below the limit.

**F/A: make send retries idempotent**

Browser `requestId` values correlate replies and expire after a 15-second client timeout; they do not establish durable command identity. A send can be accepted while its acknowledgement is lost, and a user retry can start duplicate work. Add a stable client-generated `operationId` for `chat.send` and associate it atomically with the accepted user entry/run. Retrying the same operation returns its existing result/status; it must not append another message or execute another run. A deliberate repeat gets a new ID even when the text is identical. Reconcile on reconnect, including accepted-but-interrupted work after a host restart; do not silently auto-resume it. Keep model-request retry distinct from retrying a completed tool's side effects. Test disconnect after acceptance but before acknowledgement.

**F/P5: validate the local browser control boundary**

The Web listener binds loopback, but its WebSocket upgrade currently accepts requests without checking `Origin`; `/api/open` performs a shell-open action via GET. Loopback binding is not browser-origin validation. Validate allowed Host/Origin values for browser control routes, require a same-origin anti-forgery/session proof for mutations, and make `/api/open` a protected POST. Define a separate explicit authentication path for any supported originless CLI client; an absent/spoofed Origin is not proof of trust. Apply equivalent protection to mutating plugin endpoints. Preserve normal same-origin WebView2/browser operation and the registered plugin-view workflow; do not add a login flow for the local user.

Audit `/api/file` content types too: rendering arbitrary workspace HTML/SVG as active content on the harness origin can give that document access to same-origin controls. Serve active file content as a download or in an appropriately isolated/sandboxed viewer without control credentials. Preserve intentional absolute-path access, plain-click shell-open, and modified-click viewing with these protections; this is not a new filesystem sandbox. Test rejected foreign origins/hosts, unprotected mutation requests, same-origin operation and an HTML file containing script. Keep local-control tokens out of URLs and logs.

**12. Final verification and handoff**

Extend the existing storage, context, AutoCompact, Responses, agent-scenario, socket-streaming and plugin-reload tests. Use fake providers and synchronization barriers instead of timing-based sleeps.

Run:

```powershell
dotnet build NetPI.sln
dotnet test NetPI.sln
```

From the frontend directory:

```powershell
npx svelte-check --tsconfig ./tsconfig.app.json
npx vite build
```

Record any verified pre-existing frontend errors separately from new failures.

After implementation, use Package P's verified publish/update path and check active build IDs as well as generation numbers. Changes to Abstractions require rebuilding dependents and a coordinated host restart; frontend-only changes use coherent asset publication and the Web-plugin update workflow. Keep host/desktop execution outside build outputs.

Update `AGENTS.md`, web-panel documentation and the active design plan. Document project ordering, instruction precedence, pending changes, run isolation, session tabs, retained right-panel behavior, context accounting, inner/outer scroll-follow ownership and protocol additions.

For the local agent, issue work in this form:

> Implement Package G0 only: fix the existing expanded-thinking body's streaming follow behavior. Preserve the collapsed thinking tag and one-line preview. Keep this first fix focused; the wider block review and larger improvements are authorized separately in G3. Read the named files, reproduce the nested-scroll defect, make the targeted fix and verify the G0 cases in real layout. Run frontend checks and build. Report concrete results and remaining issues. Do not implement the shell redesign, commit or push.

Start with P0–P1 to make builds/publication reliable, then use the G0 prompt above. Complete P2–P6 before depending on hot reload or introducing concurrent agents. Continue with A–F, G3's block fixes, G1–G2's shell/context work, and H–I. Split P and G3 into their bounded subtasks; do not ask a local agent to implement the entire lifecycle redesign in one turn. Recheck G0/G3 after shell changes. This keeps the implementation context small while preserving the overall architecture.
