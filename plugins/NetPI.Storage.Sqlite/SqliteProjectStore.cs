using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// astra-1 C: project CRUD over the SAME database file as
/// <see cref="SqliteSessionStore"/> (the plan keeps project persistence in the
/// existing SQLite plugin — a separate project-management plugin is
/// unnecessary for the initial implementation). Projects are keyed by their
/// CANONICAL workspace path (case/slash-insensitive on Windows), so two spellings
/// of the same directory are one project. Uses the same pooled short-lived
/// connection strategy; the <c>projects</c> table is created by the shared
/// migration v2.
/// </summary>
public sealed class SqliteProjectStore : IProjectStore
{
    private readonly string _connStr;

    public SqliteProjectStore(string dbPath)
    {
        _connStr = SqliteSessionStore.BuildConnectionString(dbPath);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        RunPragma(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static void RunPragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<ProjectInfo>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, workspace_path, created_at, updated_at FROM projects ORDER BY updated_at DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadProject(reader));
        return list;
    }

    public async ValueTask<ProjectInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, workspace_path, created_at, updated_at FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProject(reader) : null;
    }

    public async ValueTask<ProjectInfo?> GetByWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        var key = SqliteSessionStore.NormalizeProjectPathKey(workspacePath);
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, workspace_path, created_at, updated_at FROM projects WHERE normalized_path_key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProject(reader) : null;
    }

    /// <summary>
    /// Create a project; a workspace path that already has a project returns
    /// the EXISTING one (upsert by canonical path — no duplicate projects for
    /// the same directory).
    /// </summary>
    public async ValueTask<ProjectInfo> CreateAsync(string name, string workspacePath, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            var (id, _) = await UpsertProject(tx, cmd, name, workspacePath, ct);
            tx.Commit();
            var info = await GetAsync(id, ct);
            if (info is null)
                throw new InvalidOperationException($"Project vanished after upsert: {id}");
            return info;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async ValueTask RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE projects SET name = $n, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", newName);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// astra-1 C: upsert a project by its CANONICAL workspace path — a second
    /// create for the same directory (different case/slashes) returns the
    /// existing row; a fresh directory inserts.
    /// </summary>
    private async ValueTask<(string Id, string Name)> UpsertProject(SqliteTransaction tx, SqliteCommand cmd,
        string name, string workspacePath, CancellationToken ct)
    {
        var key = SqliteSessionStore.NormalizeProjectPathKey(workspacePath);
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM projects WHERE normalized_path_key = $k;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$k", key);
        var existing = await cmd.ExecuteScalarAsync(ct);
        if (existing is not null)
        {
            cmd.CommandText = "SELECT name FROM projects WHERE id = $id;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", existing);
            var existingName = (string)(await cmd.ExecuteScalarAsync(ct))!;
            return ((string)existing, existingName);
        }
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.CommandText = "INSERT INTO projects (id, name, workspace_path, normalized_path_key, created_at, updated_at) VALUES ($id, $n, $w, $k, $now, $now);";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$w", workspacePath);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return (id, name);
    }

    private static ProjectInfo ReadProject(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)));
}
