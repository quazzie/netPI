using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// The reloadable storage plugin (PLAN §29). Registers an
/// <see cref="ISessionStore"/> backed by <c>~/.netpi/netpi.db</c> (or the
/// path configured under the plugin's <c>database</c> key).
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
        context.Log.Information($"Storage ready at {path}");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _store?.Dispose();
        _store = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static string DefaultDbPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".netpi", "netpi.db");
    }
}
