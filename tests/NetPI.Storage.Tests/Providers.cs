using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Storage.Memory;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Storage.Tests;

/// <summary>
/// The providers this suite runs against. A test never names one: it goes through the factory, which opens an
/// empty store of its own, so the same scenario runs on each provider and a difference between two of them is a
/// difference in the provider and not in what the test before it left behind.
/// </summary>
public static class Providers
{
    public static readonly (string Id, Func<IStorage> Open)[] All =
    [
        ("memory", () => new MemoryStorageProvider().Open(new StorageOpenOptions
        {
            Home = T.TempDir("storage"),
            Logger = NullLogger.Instance,
            Settings = new EmptySettings(),
        })),
        ("sqlite", () => new SqliteStorageProvider().Open(new StorageOpenOptions
        {
            Home = T.TempDir("storage-sqlite"),
            Logger = NullLogger.Instance,
            Settings = new EmptySettings(),
        })),
    ];

    /// <summary>Register one test per provider. The name carries the id, so a failure says which one failed.</summary>
    public static void Add(TestRunner r, string name, Action<IStorage> body)
    {
        foreach (var (id, open) in All)
            r.Add($"[{id}] {name}", () =>
            {
                using var store = open();
                body(store);
            });
    }

    public static void AddAsync(TestRunner r, string name, Func<IStorage, Task> body)
    {
        foreach (var (id, open) in All)
            r.Add($"[{id}] {name}", async () =>
            {
                using var store = open();
                await body(store);
            });
    }
}

/// <summary>
/// The settings a provider is opened with. Nothing in the port reads them, so this is the smallest thing that
/// satisfies the contract: a provider that wants <c>storage.&lt;id&gt;.*</c> has to ask for it, and a test that
/// hands it nothing cannot notice.
/// </summary>
public sealed class EmptySettings : ISettings
{
    public string FilePath => "(none)";
    public bool InvalidOnDisk => false;
    public string? InvalidOnDiskError => null;
    public JsonObject Snapshot() => new();
    public JsonNode? GetNode(string path) => null;
    public T? Get<T>(string path, T? defaultValue = default) => defaultValue;
    public void Set(string path, JsonNode? value) { }
    public SettingsReplace Replace(JsonObject root, JsonObject? baseDocument = null) => SettingsReplace.Saved;
}
