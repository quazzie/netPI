using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// User settings (~/.netpi/settings.json). Paths are dotted: "providers.aiproxy.baseUrl".
/// Changes are persisted immediately and published as <c>settings.changed</c>.
/// </summary>
public interface ISettings
{
    string FilePath { get; }
    /// <summary>A deep clone of the whole settings document.</summary>
    JsonObject Snapshot();
    JsonNode? GetNode(string path);
    T? Get<T>(string path, T? defaultValue = default);
    void Set(string path, JsonNode? value);
    /// <summary>Replace the whole document (validated JSON object).</summary>
    void Replace(JsonObject root);
    /// <summary>
    /// True while <see cref="FilePath"/> does not parse: the store serves the last valid document (or first-run
    /// defaults) and <see cref="Set"/>/<see cref="Replace"/> throw until the file is fixed, so a broken file is never
    /// silently replaced. <see cref="InvalidOnDiskError"/> carries the parse error.
    /// </summary>
    bool InvalidOnDisk { get; }
    /// <summary>The settings file's parse error while <see cref="InvalidOnDisk"/>, else null.</summary>
    string? InvalidOnDiskError { get; }
}

/// <summary>
/// Thin SQLite access shared by core and plugins (one connection, serialized, WAL).
/// Parameters: anonymous object or IDictionary; reference them as @name, :name or $name.
/// Plugins create their own tables through <see cref="Migrate"/> with a unique scope.
/// </summary>
public interface IDatabase
{
    int Execute(string sql, object? args = null);
    /// <summary>Execute an INSERT and return last_insert_rowid().</summary>
    long Insert(string sql, object? args = null);
    T? Scalar<T>(string sql, object? args = null);
    List<T> Query<T>(string sql, object? args, Func<IDbRow, T> map);
    T? QuerySingle<T>(string sql, object? args, Func<IDbRow, T> map);
    /// <summary>Run work inside a transaction (the connection lock is held for the duration).</summary>
    T Transaction<T>(Func<IDatabase, T> work);
    void Transaction(Action<IDatabase> work);
    /// <summary>Apply migrations for a scope. migrations[i] brings the scope to version i+1.</summary>
    void Migrate(string scope, params string[] migrations);
}

public interface IDbRow
{
    int FieldCount { get; }
    string GetName(int ordinal);
    int Ordinal(string name);
    bool IsNull(string name);
    long GetInt64(string name);
    long? GetInt64OrNull(string name);
    double GetDouble(string name);
    string GetString(string name);
    string? GetStringOrNull(string name);
    byte[]? GetBlob(string name);
    object? GetValue(int ordinal);
}
