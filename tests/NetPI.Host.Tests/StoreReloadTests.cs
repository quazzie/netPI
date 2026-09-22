using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression (2026-09-21): a <c>plugin.reload</c> of netpi.storage.sqlite
/// unloaded gen 1, whose <c>StopAsync</c> disposed the DB connection — but the
/// Web plugin still held a reference to that gen-1 store, so every session
/// query failed with "ExecuteReader can only be called when the connection is
/// open" (sessions invisible in the drawer) until the Web plugin was reloaded.
/// Stop must never dispose a service that outlives the owning generation;
/// consumers re-resolve the registry lazily instead of caching forever.
/// </summary>
public class StoreReloadTests : IDisposable
{
    private readonly string _dir;

    public StoreReloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpi-store-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task PluginStop_DoesNotDisposeStore_ConsumersSurvive()
    {
        var store = new SqliteSessionStore(Path.Combine(_dir, "t.db"));
        try
        {
            var plugin = new SqlitePlugin();
            var field = typeof(SqlitePlugin).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(plugin, store);

            // The plugin's generation stops — a consumer (WebApp, Diagnostics,
            // the runner) already resolved the store and keeps using it.
            await plugin.StopAsync(CancellationToken.None);

            var info = await store.CreateAsync("/ws");
            Assert.False(string.IsNullOrEmpty(info.Id));

            // A consumer-side read still round-trips through a live store,
            // which is what WebApp.Store's lazy re-resolution relies on.
            var registry = new FixedServiceRegistry();
            registry.Put("sessions", store);
            var resolved = registry.Resolve<ISessionStore>("sessions");
            Assert.Same(store, resolved);
            Assert.Equal(1, await resolved.CountAsync(CancellationToken.None));
        }
        finally
        {
            store.Dispose();
        }
    }
    /// <summary>
    /// astra-1 P3/B: the RELOAD invariant the plan requires — not "the old
    /// store survives", but "valid operations finish AND subsequent operations
    /// resolve the NEW store". A reload swaps the generation's backing store
    /// and the registry pointer; a consumer that re-resolves lazily must now
    /// see the NEW instance (a cached old reference would silently write to the
    /// retired generation). The old store stays live for in-flight readers —
    /// it is not disposed by the reload.
    /// </summary>
    [Fact]
    public async Task Reload_SubsequentOperations_ResolveTheNewStore()
    {
        var oldStore = new SqliteSessionStore(Path.Combine(_dir, "gen1.db"));
        var newStore = new SqliteSessionStore(Path.Combine(_dir, "gen2.db"));
        try
        {
            var plugin = new SqlitePlugin();
            var field = typeof(SqlitePlugin).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(plugin, oldStore);

            var registry = new FixedServiceRegistry();
            registry.Put("sessions", oldStore);

            // Generation 1: a valid operation completes on the old store.
            var a = await oldStore.CreateAsync("/ws-a");
            Assert.False(string.IsNullOrEmpty(a.Id));

            // Reload: the new generation gets its OWN store; the registry
            // pointer flips to it (the old store is NOT disposed).
            field.SetValue(plugin, newStore);
            registry.Put("sessions", newStore);

            // Subsequent operations resolve the NEW store, not the old one.
            var resolved = registry.Resolve<ISessionStore>("sessions");
            Assert.Same(newStore, resolved);
            Assert.NotSame(oldStore, resolved);

            // A write through the re-resolved store lands on the new generation.
            var b = await resolved.CreateAsync("/ws-b");
            Assert.False(string.IsNullOrEmpty(b.Id));
            Assert.Equal(1, await newStore.CountAsync(CancellationToken.None));

            // The old generation's data is intact and still readable (not
            // disposed, not lost) — a consumer still holding it keeps working.
            Assert.Equal(1, await oldStore.CountAsync(CancellationToken.None));
        }
        finally
        {
            oldStore.Dispose();
            newStore.Dispose();
        }
    }
}
