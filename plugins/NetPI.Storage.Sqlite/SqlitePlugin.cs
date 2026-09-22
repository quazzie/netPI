using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// The reloadable storage plugin (PLAN §29). Registers an
/// <see cref="ISessionStore"/> backed by <c>~/.netpi/netpi.db</c> (or the
/// path configured under the plugin's <c>database</c> key).
/// Stop does not dispose the store — consumers may outlive the generation.
/// 
/// </summary>
public sealed class SqlitePlugin : INetPiPlugin
{
    private SqliteSessionStore? _store;

    public PluginInfo Info { get; } = new("netPI.Storage.Sqlite", "SQLite Session Store", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var path = context.OwnConfig.ValueKind == JsonValueKind.Object
            && context.OwnConfig.TryGetProperty("database", out var db)
            && db.ValueKind == JsonValueKind.String
                ? db.GetString()!
                : DefaultDbPath();

        _store = new SqliteSessionStore(path);
        context.Services.Register<ISessionStore>("sessions", _store);
        var projects = new SqliteProjectStore(path);
        context.Services.Register<IProjectStore>("projects", projects);
        // astra-1 D2: pending project changes (a selection made while a session’s
        // run is in flight). The table is DDL-owned by SqliteSessionStore (v3);
        // Like the other stores it is NOT disposed on Stop — the registry holds it.
        context.Services.Register<IPendingProjectChangeStore>("pending-projects",
            new PendingProjectChangeStore(path));
        // astra-2 §7: agent-orchestration persistence (agents/assignments/mailboxes/
        // waits/checkpoints/lane-journal). DDL is owned by SqliteSessionStore (v4);
        // the orchestration plugin consumes this through the registry, never the
        // storage assembly.
        context.Services.Register<IOrchestrationStore>("orchestration-store",
            new SqliteOrchestrationStore(path));
        // astra-2 11.2 (package F): shared cloud budget + atomic reservations.
        // DDL is owned by SqliteSessionStore migration v5; the provider
        // resolves this lazily under service "cloud-budgets" (like the other
        // stores it is NOT disposed on Stop — consumers hold the registry ref).
        var budgetStore = new SqliteCloudBudgetStore(path);
        context.Services.Register<ICloudBudgetStore>("cloud-budgets", budgetStore);
        // The execution gate the provider consults BEFORE a paid request
        // (service "cloud-gate"). Policy (allowances/allowlists) comes from
        // this plugin's "cloudBudgets" config section.
        var gate = new ConfigCloudExecutionGate(budgetStore, context.OwnConfig, context.Log,
            policySourceFactory: () => SafeResolve<IDeploymentPolicySource>(context, "deployments"));
        context.Services.Register<ICloudExecutionGate>("cloud-gate", gate);
        context.Log.Information($"Storage ready at {path}");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // Intentionally NOT disposing the store here: the service is registered
        // in the host registry, and other plugins (Web, Diagnostics, the agent
        // runner) may hold a resolved reference that outlives this generation.
        // Disposing on stop left them with a dead connection after a
        // plugin.reload ("connection is not open"). The connection is released
        // when the final holder unloads.
        _store = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static T? SafeResolve<T>(IPluginContext context, string id) where T : class
    {
        try
        {
            using var lease = context.Services.Acquire<T>(id);
            return lease.Value;
        }
        catch (ServiceUnavailableException)
        {
            return null; // owner not loaded yet / draining: policy unknown
        }
    }

    /// <summary>astra-1 P0.5: derive the default database from the single
    /// effective runtime home (<see cref="NetPI.Abstractions.RuntimeHome"/>,
    /// NETPI_HOME aware). No database migration: an existing netpi.db in place
    /// is simply used; two different runtime homes keep separate databases.</summary>
    private static string DefaultDbPath() =>
        Path.Combine(NetPI.Abstractions.RuntimeHome.Dir, "netpi.db");
}
