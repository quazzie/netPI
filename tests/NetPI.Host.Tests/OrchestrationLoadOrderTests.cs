using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Orchestration;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression: the host loads plugins ALPHABETICALLY (every LoadAsync before
/// any StartAsync), so "NetPI.Orchestration" always loads before
/// "NetPI.Storage.Sqlite" registers the "orchestration-store" service. An
/// eager store resolve in LoadAsync therefore FAILED every fresh host load
/// ("Service 'orchestration-store' is unavailable"), leaving the plugin in
/// Failed state. The orchestrator now resolves the store lazily per
/// operation (AgentOrchestrator.Store()), exactly like its runner/lanes/
/// sessions services:
///   * LoadAsync succeeds with the store service still absent;
///   * store-dependent operations fail CLOSED until the store is registered;
///   * once the dependent plugin registers the store (later in the load
///     cycle), the same orchestrator works without a reload.
/// </summary>
public sealed class OrchestrationLoadOrderTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-orch-loadorder-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    // Mirrors the host registry semantics: Resolve throws ServiceUnavailable
    // for an absent id; Register/Remove model the load-order race.
    private sealed class Registry : IServiceRegistry
    {
        private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
        public void Add(string id, object service) => _services[id] = service;
        public void Remove(string id) => _services.Remove(id);
        public T Resolve<T>(string id) where T : notnull
            => (T)(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "not registered"));
        public IDisposable Register<T>(string id, T instance) where T : notnull { _services[id] = instance; return new Noop(); }
        public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>(Resolve<T>(id));
        public IValueLease<object> Acquire(string id, Type expectedType)
            => new Lease<object>(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "no"));
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoopBus : IEventBus
    {
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null) => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class Ctx(Registry services, NoopBus events) : IPluginContext
    {
        public PluginInfo Info { get; } = new("netpi.orchestration", "Orchestration Load Order", "0.1.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => events;
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement.Clone();
        public IPluginLogger Log => new NullLog();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    private sealed class NullLog : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private static SqliteOrchestrationStore NewStore()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("netpi-orch-loadorder-db-").FullName, "test.db");
        using (var _ = new SqliteSessionStore(path)) { /* migrate */ }
        return new SqliteOrchestrationStore(path);
    }

    [Fact]
    public async Task LoadAsync_SucceedsWithStoreAbsent_OperationsFailClosed_UntilStoreRegistered()
    {
        var registry = new Registry(); // no "orchestration-store" — storage not loaded yet
        var ctx = new Ctx(registry, new NoopBus());

        // 1) Load with the store service ABSENT — the fresh-host ordering.
        var plugin = new OrchestrationPlugin();
        await plugin.LoadAsync(ctx, CancellationToken.None);

        var orch = registry.Resolve<IAgentOrchestrator>("orchestration");
        Assert.NotNull(orch);

        // 2) Store-dependent operations fail closed (the service is genuinely
        //    absent) — no crash, no half-state.
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            async () => await orch.ListAssignmentsAsync(CancellationToken.None));

        // 3) The dependent plugin (Storage.Sqlite) finishes loading and
        //    registers the store — no reload of Orchestration needed.
        registry.Add("orchestration-store", NewStore());
        var all = await orch.ListAssignmentsAsync(CancellationToken.None);
        Assert.NotNull(all);
    }

    [Fact]
    public async Task FreshHostOrdering_PluginLoadsFirst_ThenDependentStore_OrchestratorWorks()
    {
        // The exact host ordering: alphabetical load (Orchestration before
        // Storage), all LoadAsync before any StartAsync.
        var registry = new Registry();
        var orchPlugin = new OrchestrationPlugin();
        await orchPlugin.LoadAsync(new Ctx(registry, new NoopBus()), CancellationToken.None);

        // "NetPI.Storage.Sqlite" LoadAsync: migrate + register the store.
        var store = NewStore();
        registry.Add("orchestration-store", store);

        var orch = registry.Resolve<IAgentOrchestrator>("orchestration");
        var root = await store.EnsureRootAgentAsync("sess-loadorder", null, "root");
        var spawned = await orch.SpawnChildAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "late-store", OperationId = "op-late" },
            CancellationToken.None);
        Assert.Equal(AgentAssignmentLifecycle.Queued, spawned.Status); // no runner -> queued
        Assert.NotNull(await store.GetAssignmentAsync(spawned.AssignmentId, CancellationToken.None));
    }
}
