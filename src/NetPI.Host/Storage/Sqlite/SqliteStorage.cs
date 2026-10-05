using Microsoft.Extensions.Logging;

namespace NetPI.Host.Storage.Sqlite;

/// <summary>
/// The built-in storage provider: one SQLite file (<c>netpi.db</c> in the home folder) over a single serialized connection
/// (WAL). It is today's database code behind the storage port, and the only place that knows SQL: sessions, messages and
/// projects keep their tables, plugin collections become tables of their own (<see cref="SqlitePluginDataStore"/>).
/// </summary>
internal sealed class SqliteStorageProvider : IStorageProvider
{
    public string Id => "sqlite";

    /// <summary>
    /// The oldest SQLite this provider runs on: <c>RETURNING</c> (3.35) is what an append is written with, and UPSERT (3.24)
    /// what every put is. An older library opens the file without complaint and fails at the first message with a syntax
    /// error that says nothing about why, so the version is checked at open and refused with the library named.
    /// </summary>
    internal const int MinVersionNumber = 3_035_000;
    internal const string MinVersion = "3.35.0";

    public IStorage Open(StorageOpenOptions options)
    {
        Sqlite3.ConfiguredPath = options.Settings.Get<string>("database.sqlitePath");
        var file = Path.Combine(options.Home, "netpi.db");
        Database db;
        try
        {
            Sqlite3.EnsureLoaded();
            CheckVersion(Sqlite3.VersionNumber, Sqlite3.Version, Sqlite3.LoadedFrom);
            db = new Database(file, options.Logger);
        }
        catch (StorageException) { throw; }
        catch (Exception ex) { throw new StorageException($"Cannot open the SQLite database '{file}': {ex.Message}", ex); }
        try
        {
            SqliteStorage.EnsureSchema(db);
            return new SqliteStorage(db, file);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Refuses a library older than <see cref="MinVersion"/>, naming the one that was loaded and where it came from.</summary>
    internal static void CheckVersion(int versionNumber, string version, string? loadedFrom)
    {
        if (versionNumber >= MinVersionNumber) return;
        throw new StorageException(
            $"The SQLite library NetPI loaded ({loadedFrom ?? "unknown location"}) is version {version}, and this build needs {MinVersion} or newer. " +
            "Set NETPI_SQLITE (or the setting 'database.sqlitePath') to the full path of a newer sqlite3 library.");
    }
}

internal sealed class SqliteStorage : IStorage, IStorageSnapshot
{
    /// <summary>
    /// The schema, as steps: this provider's own tables (projects, sessions, messages, kv), then the indexes the hot
    /// reads seek. A session's project is a plain value (the
    /// session service checks it exists, and a deleted project's sessions are cleared first): no foreign key ties the two, as in every provider.
    /// A message id is never reused, even after the newest message is deleted (AUTOINCREMENT). A database written by an earlier
    /// shape of NetPI is brought to this one by the one-off storage migration, never by this code, and is refused until it is.
    /// </summary>
    internal static readonly string[] CoreMigrations =
    [
        """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            last_used_at INTEGER,
            meta TEXT
        );
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL DEFAULT '',
            project_id TEXT,
            parent_session_id TEXT,
            kind TEXT NOT NULL DEFAULT 'chat',
            model TEXT,
            reasoning TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0,
            pinned INTEGER NOT NULL DEFAULT 0,
            message_count INTEGER NOT NULL DEFAULT 0,
            context_tokens INTEGER NOT NULL DEFAULT 0,
            meta TEXT
        );
        CREATE INDEX ix_sessions_updated ON sessions(updated_at DESC);
        CREATE INDEX ix_sessions_project ON sessions(project_id, updated_at DESC);
        CREATE INDEX ix_sessions_parent ON sessions(parent_session_id);
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            parts TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            provider TEXT,
            model TEXT,
            stop_reason TEXT,
            usage TEXT,
            duration_ms INTEGER,
            compacted INTEGER NOT NULL DEFAULT 0,
            meta TEXT
        );
        CREATE UNIQUE INDEX ix_messages_session_seq ON messages(session_id, seq);
        CREATE TABLE kv (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        """,
        // The context read (every turn, every session being worked on) selects the session's live rows, compacted = 0.
        // The flag sits in the fat row, so the plain index made it a row lookup per message of the session (16.8 ms
        // cold on 12k messages with 500 live); this partial index holds only the live rows (0.95 ms).
        """
        CREATE INDEX IF NOT EXISTS ix_messages_live ON messages(session_id, seq) WHERE compacted = 0;
        """,
        // The session list is read in one order only, pinned first, then newest, then id (ListSessions). The index on
        // updated_at alone served no query (the list was a scan and a temp sort, and every append maintained it for
        // nothing); this one is that order, so the list walks it and stops at its page.
        """
        DROP INDEX IF EXISTS ix_sessions_updated;
        CREATE INDEX IF NOT EXISTS ix_sessions_list ON sessions(pinned DESC, updated_at DESC, id DESC);
        """,
    ];

    /// <summary>The catalog of plugin collections: which plugin's collection lives in which table, and with which index fields.</summary>
    internal static readonly string[] DataMigrations =
    [
        """
        CREATE TABLE _collections (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            plugin TEXT NOT NULL,
            name TEXT NOT NULL,
            spec TEXT NOT NULL,
            UNIQUE(plugin, name)
        );
        """,
    ];

    private readonly Database _db;
    private readonly string _file;

    public SqliteStorage(Database db, string file)
    {
        _db = db;
        _file = file;
        Sessions = new SqliteSessionRepository(db);
        Values = new SqliteKeyValueStore(db);
        Plugins = new SqlitePluginDataStore(db);
    }

    /// <summary>Creates the tables of a new database; refuses one whose schema this build does not know (fail closed, never guess).</summary>
    internal static void EnsureSchema(Database db)
    {
        foreach (var (scope, steps) in new[] { ("core", CoreMigrations), ("data", DataMigrations) })
        {
            if (db.Scalar<long?>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_migrations'") is > 0
                && db.Scalar<long?>("SELECT version FROM _migrations WHERE scope = @scope", new { scope }) is { } version
                && version > steps.Length)
                throw new StorageException(
                    $"The database '{db.FilePath}' has schema version {version} of '{scope}', and this build of NetPI expects {steps.Length}. " +
                    "It was written by a different shape of NetPI: restore it into a matching build, or run the storage migration for this one.");
            db.Migrate(scope, steps);
        }
    }

    /// <summary>The SQL engine, for tests that stage or inspect rows directly.</summary>
    internal Database Database => _db;

    public string ProviderId => "sqlite";
    public ISessionRepository Sessions { get; }
    public IKeyValueStore Values { get; }
    public IPluginDataStore Plugins { get; }
    public IStorageSnapshot Snapshot => this;
    public object Lock => _db.Gate;

    public StorageInfo Info
    {
        get
        {
            long? size = null;
            try { size = new FileInfo(_file).Length; }
            catch (IOException) { }
            return new StorageInfo("sqlite", Sqlite3.Version, _file, size);
        }
    }

    /// <summary>A consistent copy of the live database, with the committed contents of the WAL, as one file. The copy
    /// runs in slices (a backup, not a <c>VACUUM INTO</c>), so the gate is released between them.</summary>
    public IReadOnlyList<string> Write(string directory)
    {
        _db.BackupTo(Path.Combine(directory, "netpi.db"));   // in batches: the gate is free between pages of the copy
        return ["netpi.db"];
    }

    public void Dispose() => _db.Dispose();
}

internal sealed class SqliteKeyValueStore(Database db) : IKeyValueStore
{
    public string? Get(string key) => db.Scalar<string>("SELECT value FROM kv WHERE key = @key", new { key });

    public void Set(string key, string? value)
    {
        if (value is null) db.Execute("DELETE FROM kv WHERE key = @key", new { key });
        else db.Execute("INSERT INTO kv(key, value) VALUES(@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value });
    }
}
