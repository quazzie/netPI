# Plugin architecture (deep doc)

`AGENTS.md` states the core invariant (plugins talk only through
`IPluginContext`); this is the behavioral reference. Sources:
`src/NetPI.Host/Plugins/` (PluginManager, PluginLoadContext, PluginInstance),
`src/NetPI.Host/Services/ServiceRegistry.cs`, `src/NetPI.Host/Events/EventBus.cs`.

## The hard rules

- **One collectible `AssemblyLoadContext` per plugin generation**
  (`<plugin-id>-gen<n>`). No shared cache context for plugin code — a reload
  must be able to unload the old bytes. The host (`netPI.Host.dll`) is the only
  always-loaded runtime.
- **Never reference another plugin's assembly.** Cross-plugin coupling is via
  the **service registry** (id-keyed `Register`/`Acquire`) and the **event bus**
  (`IEventBus.Publish`/`Subscribe`) only. `src/NetPI.Abstractions` is the only
  shared surface.
- **Service ownership is explicit.** `Register<T>(id, instance, owner)` stamps
  the owning `PluginInstance` — no ambient-owner thread context (astra-1 P3:
  the process-global `ServiceOwner` static is gone; a plugin always registers
  through its scoped wrapper, which binds the real owning generation). The
  host-side overload `Register<T>(id, instance)` (owner = null) exists only
  for host-owned entries. On unload, `ServiceRegistry.RemoveAllFor(instance)`
  and `EventBus.RemoveAllFor(instance)` remove exactly that plugin's entries;
  other plugins' services and subscriptions survive a reload.

## Lifecycle (per generation)

`LoadAsync → StartAsync … StopAsync → UnloadAsync` (`INetPiPlugin`).

- `LoadAsync(context)`: register services/tools/commands, subscribe to the bus.
  Services registered here are visible to *other* plugins immediately.
- `StartAsync`: long-running work begins (Kestrel surfaces, loops).
- `StopAsync`: stop work; **do not dispose shared state** — stores (e.g.
  `sessions`) must survive so consumers that re-resolve lazily keep working.
- `UnloadAsync`: final teardown; then the ALC is unrooted and the GC can
  collect the old generation's assemblies.

State machine (`PluginState`): `Loading → Active ⇄ Draining → Unloading →
Unloaded`, plus `Failed` (a failed load keeps the previous generation Active).
`Draining` = new `Acquire` is denied, live leases are drained (poll 10 ms,
bounded 30 s), then unload proceeds.

## Reload policy

`ReloadPolicy` (`src/NetPI.Host/Plugins/PluginState.cs`):

- `PluginIdle` — unload as soon as all service leases release. The default for
  every plugin.
- `AgentIdle` — additionally waits for the agent to be idle, through a
  **pluggable gate** (`PluginManager.AgentIdleGate`, a
  `Func<CancellationToken, Task<bool>>` re-asked on a short interval while
  draining). `netPI.Agent` and `netPI.Web` are in `AgentIdlePlugins` (the gate
  is `runner.IsRunning == false`). This is why reloading the Web surface while
  a run streams is deferred, never interrupted.

Reload flow (WS `plugin.reload` / `plugin.reloadAll`): re-resolve the build
pointer → snapshot new bytes into the per-instance plugin-cache dir → drain
leases → apply the policy gate → `Stop`/`Unload` the old generation → `Load`/`Start`
the new one. A load failure aborts and the old generation stays Active.
`plugin.scan` re-discovers `plugins/` and loads+starts ids the host has never
seen — no restart for a newly staged plugin.

**After a reload completes, consumers re-resolve.** The host removes the old
generation's services (`RemoveAllFor`), so a plugin that keeps a live
reference to e.g. the `sessions` store must `Acquire` it at use time, not cache
it across reloads. `NetPI.Web` does this in `RefreshReloadableServices`
(runner/agent/catalog/steering/compaction re-resolved at the top of
`OnPluginUpdateCompleted`).

## Leases

Cross-plugin service use is lease-based, not reference-based:

```csharp
IValueLease<T> lease = context.Services.Acquire<T>("sessions");
try { /* use lease.Value */ } finally { lease.Dispose(); }
```

`Acquire<T>` hands the holder an `IValueLease<T>` (a `Dispose` releases it).
While a plugin holds *any* lease, it cannot be drained (`TryAcquireLease`
fails for a draining/unloading owner → the `Acquire` fails). This is how a
reloader finds "nobody is using the old generation" and how a *blocking* lease
is counted (`IPluginManagerFacade` exposes `ActiveLeases` / `BlockingLeases`).

A plugin can also lease *itself* (`context.LeaseSelf()`) to block its own
reload while an in-flight operation is live (the reload then waits, up to the
30 s drain bound).

## Snapshots & native entry-point DLLs

- The host **never loads from `.artifacts/` or `plugins/`**. It resolves
  `plugins/<id>/current.json` → `.artifacts/plugins/<id>/<buildId>/` and
  snapshots the bytes into
  `~/.netpi/plugin-cache/<host-instance>/<plugin>/<attempt>-<buildId>/`
  (immutable once written). The ALC loads from that snapshot. Publishing a new
  build flips `current.json`; `plugin.reload` re-resolves and snapshots the
  next attempt. Stale per-plugin snapshots are pruned to
  `MaxCachedGenerations` (default 2) **at each new snapshot** — the prune is
  ownership-aware (this process must own the instance root) and any snapshot
  still locked by a live ALC is simply left for the next prune, so one host
  instance never deletes another's cache.
- **Native libs**: a plugin DLL may carry native dependencies
  (`.deps.json` runtime assets). Before the ALC loads the managed entry
  assembly, the host locates native assets by name via the managed
  entry-point assembly's location and pre-loads them with
  `NativeLibrary.Load(_nativeCache.EnsureCached(path))` — so a first reference
  inside the new ALC never triggers an implicit `LoadLibrary` from a dead
  path. `_nativeCache` dedupes by path.

## Service id map (who registers what)

| service id | registered by |
|---|---|
| `agent`, `steering`, `runner` | NetPI.Agent |
| `compaction` | NetPI.AutoCompact |
| `retry` | NetPI.Retry |
| `sessions`, `projects`, `pending-projects` | NetPI.Storage.Sqlite |
| `provider`, `catalog` | NetPI.Provider.AiProxy |
| `tools`, `resolver:bash`, `resolver:powershell` | NetPI.Tools |
| `background`, `foreground-processes` | NetPI.BackgroundTasks |
| `system-prompt`, `workspace-context`, `instruction-context` | NetPI.Context.Pi |
| `background-tasks` surface | NetPI.BackgroundTasks (panel on :5275) |
| `plugins` (facade), `host-config`, `commands` | host-owned (not a plugin) |

Panels (right-hand tab) are registered on the Web side by the Diagnostics
(:5274), BackgroundTasks (:5275), and Activity (:5276) plugins — see
`docs/web-panels.md`.

## Reload safety for shared state

- Stores are **not** disposed in `StopAsync` (see above) — the old-generation
  store instance keeps serving until consumers re-resolve, then becomes
  unreachable and collectable.
- **Do** dispose *surface-local* resources in `StopAsync` (Kestrel servers,
  timers, sockets). **Do not** dispose anything another plugin can `Acquire`.
- Event subscriptions are removed per-owner on unload, so a reloaded plugin
  re-subscribes in its new `LoadAsync` and gets no duplicate handlers.
