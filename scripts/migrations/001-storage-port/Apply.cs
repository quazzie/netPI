using Microsoft.Extensions.Logging.Abstractions;
using NetPI;
using NetPI.Host;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

/// <summary>
/// The apply: the home lock first (a NetPI running on the home holds it, and the swap would race it), then the old
/// database renamed aside (it and its -wal/-shm, to netpi.db.pre-storage-port*), and the verified one moved into place.
/// Nothing is ever deleted, and settings.json is never touched.
/// </summary>
internal static class Apply
{
    public static int Run(string home, string newDb)
    {
        var oldDb = Path.Combine(home, "netpi.db");
        try
        {
            using (var homeLock = HomeLock.Acquire(home))
            {
                if (File.Exists(newDb + "-wal") || File.Exists(newDb + "-shm"))
                {
                    // A checkpointed, closed database is one file: make it so before it moves.
                    using var db = new Database(newDb, null);
                    db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                }
                File.Move(oldDb, oldDb + ".pre-storage-port");
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(oldDb + suffix)) File.Move(oldDb + suffix, oldDb + suffix + ".pre-storage-port");
                File.Move(newDb, oldDb);
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
        // The installed database opens with the provider, or not at all.
        using (new SqliteStorageProvider().Open(new StorageOpenOptions
        {
            Home = home,
            Logger = NullLogger.Instance,
            Settings = MigrationSettings.For(home),
        })) { }
        Console.WriteLine("applied: " + oldDb);
        Console.WriteLine("settings.json was not touched.");
        Console.WriteLine("rollback (close NetPI first):");
        Console.WriteLine($"  1. rename netpi.db aside (e.g. netpi.db.post-migration)");
        Console.WriteLine($"  2. rename netpi.db.pre-storage-port back to netpi.db" +
                          " (and netpi.db-wal.pre-storage-port / netpi.db-shm.pre-storage-port back to -wal / -shm, when present)");
        Console.WriteLine($"  3. when it is sure to be unused, delete the set-aside file and the .migrating folder.");
        return 0;
    }
}
