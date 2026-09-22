using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Lanes;
using Xunit;

namespace NetPI.Host.Tests;

// astra-2 §5.3/§16 "Discovery adds a backend or changes a model route":
// discovery (the provider capacity source) must NOT create, enable, or delete
// pools — pools come from CONFIG only. It may only change the validated
// capacity the scheduler admits: unknown holds new admission, a fresh
// observation admits the already-queued, and a stale/unknown observation
// NEVER evicts owned lanes. This test drives the REAL LanePlugin (config →
// scheduler) with a fake capacity source and the real scheduler's
// observation path (the exact call the poll loop makes).
public sealed class LaneDiscoveryTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class SteerableSource : IProviderCapacitySource
    {
        private readonly object _lock = new();
        private ProviderCapacityObservation _obs;
        public SteerableSource(ProviderCapacityObservation initial) => _obs = initial;
        public void Set(ProviderCapacityObservation obs) { lock (_lock) _obs = obs; }
        public ValueTask<ProviderCapacityObservation> ObserveAsync(string modelId, CancellationToken ct = default)
        {
            lock (_lock) return ValueTask.FromResult(_obs);
        }
    }

    private static ProviderCapacityObservation Unknown(string model) =>
        new(model, null, ProviderCapacityStatus.Unknown, DateTimeOffset.UtcNow, "test");
    private static ProviderCapacityObservation Fresh(string model, int total) =>
        new(model, total, ProviderCapacityStatus.Fresh, DateTimeOffset.UtcNow, "test");

    private sealed class Ctx(JsonElement config) : IPluginContext
    {
        private sealed class Reg : IServiceRegistry
        {
            private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
            private sealed class Noop : IDisposable { public void Dispose() { } }
            private sealed class Lz<T>(T v) : IValueLease<T>
            { public T Value => v; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
            public void Put(string id, object o) => _map[id] = o;
            public IDisposable Register<T>(string id, T instance) where T : notnull { _map[id] = instance; return new Noop(); }
            public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lz<T>(Resolve<T>(id));
            public IValueLease<object> Acquire(string id, Type t)
            {
                var v = _map.TryGetValue(id, out var o) ? o : throw new ServiceUnavailableException(id, "missing");
                return new Lz<object>(v);
            }
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lz<T>(default!);
            public T Resolve<T>(string id) where T : notnull
            {
                var v = _map.TryGetValue(id, out var o) ? o : throw new ServiceUnavailableException(id, "missing");
                return (T)v;
            }
        }
        private readonly Reg _svc = new();
        public IServiceRegistry Services => _svc;
        public PluginInfo Info => new("lanes", "lanes", "1.0.0");
        public JsonElement OwnConfig => config;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => throw new NotSupportedException();
        public IPluginLogger Log => new NullLogger();
        private sealed class NL : IValueLease<object> { public object Value => this; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
        public IValueLease<object> LeaseSelf() => new NL();
        public void PutSvc(string id, object o) => _svc.Put(id, o);
    }

    private static LaneQueueEntry Entry(string id, int seq) =>
        new(id, "p1", "dep-1", seq, null, null, "s" + id, "work", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Discovery_ChangesCapacityButNeverPools_HoldsUnknownAdmitsFreshNeverEvicts()
    {
        var source = new SteerableSource(Unknown("m1"));
        var ctx = new Ctx(JsonDocument.Parse(
            """
            {
              "enabled": true,
              "deployments": [ { "id": "dep-1", "modelId": "m1" } ],
              "pools": [ { "id": "p1", "deploymentIds": [ "dep-1" ], "capacity": { "mode": "provider" } } ]
            }
            """.Trim()
        ).RootElement);
        ctx.PutSvc("provider-capacity", source);
        var plugin = new LanePlugin();
        await plugin.LoadAsync(ctx, CancellationToken.None);

        // Stop the background poll loop NOW so the test is the only observer:
        // the contract under test is the scheduler's reaction to observations,
        // not the poll timing. (The poll loop is covered by the capacity-source
        // tests; here we exercise the exact UpdateProviderObservation call the
        // poll makes.)
        await plugin.StopAsync(CancellationToken.None);

        var lanes = ctx.Services.Resolve<LaneScheduler>("lanes");

        // Config registered exactly one pool — discovery was NOT consulted to create it.
        var pools = lanes.Snapshots();
        Assert.Single(pools);
        Assert.Equal("p1", pools[0].PoolId);
        Assert.Null(pools[0].EffectiveCapacity); // no validated observation → hold, never a guess

        // Discovery reports unknown: hold-new, the run queues.
        lanes.UpdateProviderObservation(Unknown("m1"));
        var q1 = await lanes.AcquireAsync(Entry("r1", 1));
        Assert.Null(q1.Token);
        Assert.Contains("Unknown", q1.BlockedReason);

        // Discovery reports a fresh validated capacity → the ALREADY-QUEUED
        // admission starts; nothing new is created.
        lanes.UpdateProviderObservation(Fresh("m1", 2));
        var afterFresh = lanes.Snapshots().Single(p => p.PoolId == "p1");
        Assert.Equal(1, afterFresh.OwnedCount);
        Assert.Equal(0, afterFresh.QueueCount);
        var q2 = await lanes.AcquireAsync(Entry("r2", 2));
        Assert.NotNull(q2.Token); // capacity 2 admits a second run

        // Discovery loses the backend: owned lanes are NEVER evicted, new
        // admission holds again, and the pool is neither deleted nor duplicated.
        lanes.UpdateProviderObservation(Unknown("m1"));
        var q3 = await lanes.AcquireAsync(Entry("r3", 3));
        Assert.Null(q3.Token); // hold-new again
        var afterLoss = lanes.Snapshots();
        Assert.Single(afterLoss); // still exactly one pool — discovery created/deleted nothing
        Assert.Equal(2, afterLoss[0].OwnedCount); // owned lanes retained
        Assert.Equal(1, afterLoss[0].QueueCount);
    }

    [Fact]
    public async Task NoConfig_RegistersEmptyScheduler_UnknownPoolNeverAdmits()
    {
        var ctx = new Ctx(JsonDocument.Parse("{}").RootElement);
        var plugin = new LanePlugin();
        await plugin.LoadAsync(ctx, CancellationToken.None);
        try
        {
            var lanes = ctx.Services.Resolve<LaneScheduler>("lanes");
            Assert.Empty(lanes.Snapshots());
            var r = await lanes.AcquireAsync(new LaneQueueEntry("r1", "p1", "dep-1", 1, null, null, "s1", "work", DateTimeOffset.UtcNow));
            Assert.Null(r.Token);
            Assert.Contains("unknown pool", r.BlockedReason);
        }
        finally
        {
            await plugin.StopAsync(CancellationToken.None);
        }
    }
}
