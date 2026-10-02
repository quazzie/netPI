namespace NetPI.Host.Storage;

/// <summary>The storage providers built into the host. The <c>storage.provider</c> setting names one.</summary>
internal static class StorageProviders
{
    public static IStorageProvider Create(string id) => id.Trim().ToLowerInvariant() switch
    {
        "sqlite" => new Sqlite.SqliteStorageProvider(),
        "memory" => new Memory.MemoryStorageProvider(),
        _ => throw new StorageException($"Unknown storage provider '{id}' (setting storage.provider). Built in: sqlite, memory."),
    };
}

/// <summary>What plugins may ask of the store without touching it: what it is, and a consistent copy of it (<see cref="IStorageAccess"/>).</summary>
internal sealed class StorageAccess(IStorage storage) : IStorageAccess
{
    public StorageInfo Info => storage.Info;
    public IStorageSnapshot Snapshot => storage.Snapshot;
}
