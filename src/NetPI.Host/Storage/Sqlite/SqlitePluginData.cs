using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Host.Storage.Sqlite;

/// <summary>
/// Plugin collections in SQLite: one table per (plugin, collection), <c>k</c> (the key), <c>doc</c> (the JSON) and one typed
/// column per declared index field, with an index on each. The values of the index fields are read from the document by this
/// provider on every write, so nothing here depends on a JSON function of the engine.
/// </summary>
internal sealed class SqlitePluginDataStore(Database db) : IPluginDataStore
{
    // One instance per plugin id for the life of the store: the instance is the plugin's lock, so two generations of a plugin
    // that are both running during a hot reload still take turns.
    private readonly ConcurrentDictionary<string, SqlitePluginData> _plugins = new(StringComparer.Ordinal);

    public IPluginData For(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _plugins.GetOrAdd(pluginId, id => new SqlitePluginData(db, id));
    }
}

internal sealed partial class SqlitePluginData(Database db, string pluginId) : IPluginData
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SqliteCollection> _open = new(StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    public IDataCollection Collection(string name, CollectionSpec spec)
    {
        if (!NamePattern().IsMatch(name)) throw new ArgumentException($"'{name}' is not a collection name (letters, digits and underscores, starting with a letter)", nameof(name));
        ArgumentNullException.ThrowIfNull(spec);
        foreach (var field in spec.Fields.Keys)
            if (!NamePattern().IsMatch(field)) throw new ArgumentException($"'{field}' is not an index field name", nameof(spec));
        lock (db.Gate)
        {
            lock (_open)
            {
                var fields = spec.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
                if (_open.TryGetValue(name, out var existing) && existing.SameFields(fields)) return existing;
                var collection = db.Transaction(_ => SqliteCollection.OpenOrUpgrade(db, pluginId, name, fields));
                _open[name] = collection;
                return collection;
            }
        }
    }

    public T Transaction<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The plugin's own lock first, then the store's (inside the database transaction): always in this order.
        lock (_gate) return db.Transaction(_ => work());
    }

    public void Transaction(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Transaction<object?>(() => { work(); return null; });
    }
}

internal sealed class SqliteCollection : IDataCollection
{
    private readonly Database _db;
    private readonly string _table;
    private readonly Dictionary<string, DataFieldType> _fields;
    private readonly string[] _order;   // field names in column order
    private readonly string _putSql;
    private readonly string _insertSql;

    private SqliteCollection(Database db, string name, string table, Dictionary<string, DataFieldType> fields)
    {
        _db = db;
        Name = name;
        _table = table;
        _fields = fields;
        _order = [.. fields.Keys];
        var columns = string.Concat(_order.Select(f => ", f_" + f));
        var values = string.Concat(_order.Select((_, i) => ", @v" + i.ToString(CultureInfo.InvariantCulture)));
        var updates = string.Concat(_order.Select((f, i) => ", f_" + f + " = @v" + i.ToString(CultureInfo.InvariantCulture)));
        _putSql = $"INSERT INTO {_table}(k, doc{columns}) VALUES(@k, @doc{values}) ON CONFLICT(k) DO UPDATE SET doc = excluded.doc{updates}";
        _insertSql = $"INSERT INTO {_table}(k, doc{columns}) VALUES(@k, @doc{values}) ON CONFLICT(k) DO NOTHING";
    }

    public string Name { get; }

    internal bool SameFields(Dictionary<string, DataFieldType> other) =>
        other.Count == _fields.Count && other.All(f => _fields.TryGetValue(f.Key, out var t) && t == f.Value);

    private static string SqlType(DataFieldType t) => t switch { DataFieldType.Text => "TEXT", DataFieldType.Integer => "INTEGER", _ => "REAL" };

    /// <summary>Creates the collection's table, or brings an existing one to the declared fields: a new field gets a column, an index and a back-fill.</summary>
    internal static SqliteCollection OpenOrUpgrade(Database db, string plugin, string name, Dictionary<string, DataFieldType> fields)
    {
        var row = db.QuerySingle("SELECT id, spec FROM _collections WHERE plugin = @plugin AND name = @name", new { plugin, name },
            r => (Id: r.GetInt64("id"), Spec: r.GetString("spec")));
        var specJson = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)JsonValue.Create(f.Value.ToString()))));
        if (row == default)
        {
            var id = db.Insert("INSERT INTO _collections(plugin, name, spec) VALUES(@plugin, @name, @spec)", new { plugin, name, spec = specJson.ToJsonString() });
            var table = "c" + id.ToString(CultureInfo.InvariantCulture);
            var cols = string.Concat(fields.Select(f => $", f_{f.Key} {SqlType(f.Value)}"));
            db.Execute($"CREATE TABLE {table} (k TEXT PRIMARY KEY, doc TEXT NOT NULL{cols})");
            foreach (var f in fields.Keys) db.Execute($"CREATE INDEX ix_{table}_{f} ON {table}(f_{f})");
            return new SqliteCollection(db, name, table, fields);
        }

        var tableName = "c" + row.Id.ToString(CultureInfo.InvariantCulture);
        var stored = JsonNode.Parse(row.Spec) as JsonObject ?? [];
        var physical = db.Query($"PRAGMA table_info({tableName})", null, r => r.GetString("name")).ToHashSet(StringComparer.Ordinal);
        var backfill = new List<string>();
        foreach (var (field, type) in fields)
        {
            var wasDeclared = stored[field]?.GetValue<string>();
            if (wasDeclared is not null && wasDeclared != type.ToString())
                throw new StorageException($"Collection '{name}' of plugin '{plugin}': field '{field}' was declared {wasDeclared} and is now {type}; a field's type cannot change");
            if (!physical.Contains("f_" + field))
            {
                db.Execute($"ALTER TABLE {tableName} ADD COLUMN f_{field} {SqlType(type)}");
                db.Execute($"CREATE INDEX ix_{tableName}_{field} ON {tableName}(f_{field})");
                backfill.Add(field);
            }
            else if (wasDeclared is null)
            {
                backfill.Add(field);   // the column exists from an earlier declaration and may be stale
            }
        }
        db.Execute("UPDATE _collections SET spec = @spec WHERE id = @id", new { spec = specJson.ToJsonString(), id = row.Id });
        var collection = new SqliteCollection(db, name, tableName, fields);
        if (backfill.Count > 0) collection.Backfill(backfill);
        return collection;
    }

    private void Backfill(List<string> fields)
    {
        foreach (var (key, text) in _db.Query($"SELECT k, doc FROM {_table}", null, r => (r.GetString("k"), r.GetString("doc"))))
        {
            var doc = JsonNode.Parse(text) as JsonObject ?? [];
            var sets = string.Join(", ", fields.Select((f, i) => $"f_{f} = @v{i}"));
            var args = new Dictionary<string, object?> { ["k"] = key };
            for (var i = 0; i < fields.Count; i++) args["v" + i.ToString(CultureInfo.InvariantCulture)] = FieldValue(fields[i], doc);
            _db.Execute($"UPDATE {_table} SET {sets} WHERE k = @k", args);
        }
    }

    // ------------------------------------------------------------------ documents

    public JsonObject? Get(string key) => _db.QuerySingle($"SELECT doc FROM {_table} WHERE k = @k", new { k = key }, r => Parse(r.GetString("doc")));

    public void Put(string key, JsonObject doc) => _db.Execute(_putSql, WriteArgs(key, doc));

    public bool Insert(string key, JsonObject doc) => _db.Execute(_insertSql, WriteArgs(key, doc)) > 0;

    public bool Delete(string key) => _db.Execute($"DELETE FROM {_table} WHERE k = @k", new { k = key }) > 0;

    public IReadOnlyList<DataDoc> Find(DataQuery? query = null)
    {
        var args = new Dictionary<string, object?>();
        var sql = $"SELECT k, doc FROM {_table}{Where(query, args)}{OrderBy(query)}";
        if (query?.Limit is { } limit)
        {
            sql += " LIMIT @limit OFFSET @offset";
            args["limit"] = Math.Max(0, limit);
            args["offset"] = Math.Max(0, query.Offset);
        }
        else if (query is { Offset: > 0 })
        {
            sql += " LIMIT -1 OFFSET @offset";
            args["offset"] = query.Offset;
        }
        return _db.Query(sql, args, r => new DataDoc(r.GetString("k"), Parse(r.GetString("doc"))));
    }

    public long Count(DataQuery? query = null)
    {
        var args = new Dictionary<string, object?>();
        return _db.Scalar<long?>($"SELECT COUNT(*) FROM {_table}{Where(query, args)}", args) ?? 0;
    }

    public double Sum(string field, DataQuery? query = null)
    {
        if (!_fields.TryGetValue(field, out var type)) throw new ArgumentException($"'{field}' is not an index field of '{Name}'");
        if (type == DataFieldType.Text) throw new ArgumentException($"'{field}' is a Text field and cannot be summed");
        var args = new Dictionary<string, object?>();
        return _db.Scalar<double?>($"SELECT TOTAL(f_{field}) FROM {_table}{Where(query, args)}", args) ?? 0;
    }

    public int DeleteWhere(DataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var args = new Dictionary<string, object?>();
        return _db.Execute($"DELETE FROM {_table}{Where(query, args)}", args);
    }

    // ------------------------------------------------------------------ queries

    private string Where(DataQuery? query, Dictionary<string, object?> args)
    {
        if (query is null || query.Where.Count == 0) return "";
        var parts = new List<string>();
        var n = 0;
        string Param(object? value, DataFieldType type)
        {
            var name = "p" + (n++).ToString(CultureInfo.InvariantCulture);
            args[name] = Coerce(value, type);
            return "@" + name;
        }
        foreach (var f in query.Where)
        {
            if (!_fields.TryGetValue(f.Field, out var type)) throw new ArgumentException($"'{f.Field}' is not an index field of '{Name}'");
            var col = "f_" + f.Field;
            switch (f.Op)
            {
                case DataOp.IsNull: parts.Add($"{col} IS NULL"); break;
                case DataOp.NotNull: parts.Add($"{col} IS NOT NULL"); break;
                case DataOp.Eq: parts.Add($"{col} = {Param(RequireValue(f), type)}"); break;
                case DataOp.Ne: parts.Add($"{col} <> {Param(RequireValue(f), type)}"); break;
                case DataOp.Lt: parts.Add($"{col} < {Param(RequireValue(f), type)}"); break;
                case DataOp.Le: parts.Add($"{col} <= {Param(RequireValue(f), type)}"); break;
                case DataOp.Gt: parts.Add($"{col} > {Param(RequireValue(f), type)}"); break;
                case DataOp.Ge: parts.Add($"{col} >= {Param(RequireValue(f), type)}"); break;
                case DataOp.In:
                case DataOp.NotIn:
                    var values = (f.Value as System.Collections.IEnumerable ?? throw new ArgumentException($"{f.Op} on '{f.Field}' needs a list of values"))
                        .Cast<object?>().ToList();
                    if (values.Count == 0) { parts.Add(f.Op == DataOp.In ? "0" : $"{col} IS NOT NULL"); break; }
                    var list = string.Join(", ", values.Select(v => Param(v ?? throw new ArgumentException($"{f.Op} on '{f.Field}' has a null value"), type)));
                    parts.Add($"{col} {(f.Op == DataOp.In ? "IN" : "NOT IN")} ({list})");
                    break;
                default: throw new ArgumentException($"Unknown operator {f.Op}");
            }
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    private static object RequireValue(DataFilter f) =>
        f.Value ?? throw new ArgumentException($"{f.Op} on '{f.Field}' needs a value; use IsNull / NotNull to test for no value");

    private string OrderBy(DataQuery? query)
    {
        var sb = new StringBuilder(" ORDER BY ");
        if (query is not null)
            foreach (var o in query.OrderBy)
            {
                if (!_fields.ContainsKey(o.Field)) throw new ArgumentException($"'{o.Field}' is not an index field of '{Name}'");
                sb.Append("f_").Append(o.Field).Append(o.Descending ? " DESC, " : " ASC, ");
            }
        return sb.Append('k').ToString();
    }

    // ------------------------------------------------------------------ values

    private Dictionary<string, object?> WriteArgs(string key, JsonObject doc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(doc);
        var args = new Dictionary<string, object?> { ["k"] = key, ["doc"] = doc.ToJsonString() };
        for (var i = 0; i < _order.Length; i++) args["v" + i.ToString(CultureInfo.InvariantCulture)] = FieldValue(_order[i], doc);
        return args;
    }

    /// <summary>The value of an index field in a document, as the column's type: null when the property is missing or JSON null.</summary>
    private object? FieldValue(string field, JsonObject doc)
    {
        var type = _fields[field];
        if (!doc.TryGetPropertyValue(field, out var node) || node is null || node.GetValueKind() is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var kind = node.GetValueKind();
        switch (type)
        {
            case DataFieldType.Text:
                if (kind == JsonValueKind.String) return node.GetValue<string>();
                break;
            case DataFieldType.Integer:
                if (kind == JsonValueKind.True) return 1L;
                if (kind == JsonValueKind.False) return 0L;
                if (kind == JsonValueKind.Number && long.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                break;
            case DataFieldType.Real:
                if (kind == JsonValueKind.Number) return double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                break;
        }
        throw new ArgumentException($"Field '{field}' of '{Name}' is declared {type}, and the document holds {kind}");
    }

    private static object Coerce(object? value, DataFieldType type)
    {
        switch (type)
        {
            case DataFieldType.Text:
                if (value is string s) return s;
                break;
            case DataFieldType.Integer:
                if (value is long or int or short or byte) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (value is bool b) return b ? 1L : 0L;
                break;
            case DataFieldType.Real:
                if (value is double or float or decimal or long or int) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
        }
        throw new ArgumentException($"A {value?.GetType().Name ?? "null"} value does not fit a {type} field");
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text) as JsonObject ?? [];
}
