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
    // Collections first declared inside a plugin transaction: their table is created inside it, so a rollback takes it away again.
    private readonly List<string> _createdInTransaction = [];
    // Collections whose declaration a plugin transaction changed, with the declaration they had: a rollback takes the column
    // away again, so the handle the plugin holds takes its old declaration back (it is the same object for every generation).
    private readonly List<(SqliteCollection Collection, Dictionary<string, DataFieldType> Previous)> _redeclaredInTransaction = [];

    public IDataCollection Collection(string name, CollectionSpec spec)
    {
        StorageNames.CheckCollection(name);
        StorageNames.CheckSpec(spec);
        lock (db.Gate)
        {
            lock (_open)
            {
                var fields = spec.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
                if (!db.InTransaction) { _createdInTransaction.Clear(); _redeclaredInTransaction.Clear(); }   // nothing of the plugin's is open to roll back
                _open.TryGetValue(name, out var existing);
                if (existing is not null && existing.SameFields(fields)) return existing;
                var previous = existing?.Declaration;
                // The same collection object is kept when its declaration changes: a handle a plugin holds stays the collection.
                var collection = db.Transaction(_ => SqliteCollection.OpenOrUpgrade(db, pluginId, name, fields, existing));
                if (existing is null)
                {
                    _open[name] = collection;
                    if (db.InTransaction) _createdInTransaction.Add(name);
                }
                else if (db.InTransaction) _redeclaredInTransaction.Add((existing, previous!));
                return collection;
            }
        }
    }

    public T Transaction<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The plugin's own lock first, then the store's (inside the database transaction): always in this order.
        lock (_gate)
        {
            var joined = db.InTransaction;
            int created, redeclared;
            lock (_open) { created = _createdInTransaction.Count; redeclared = _redeclaredInTransaction.Count; }
            try
            {
                var result = db.Transaction(_ => work());
                if (!joined) lock (_open) { _createdInTransaction.Clear(); _redeclaredInTransaction.Clear(); }
                return result;
            }
            catch
            {
                // a nested transaction that fails rolls back to its savepoint: what it declared goes with it, what the outer one did stays
                UndoDeclarations(created, redeclared);
                throw;
            }
        }
    }

    public void Transaction(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Transaction<object?>(() => { work(); return null; });
    }

    /// <summary>
    /// A transaction (or a nested one, to its savepoint) rolled back: a collection it created has no table any more, so the next
    /// declaration creates it again, and a collection whose declaration it changed takes the old one back, last change first.
    /// </summary>
    private void UndoDeclarations(int created, int redeclared)
    {
        lock (_open)
        {
            for (var i = _redeclaredInTransaction.Count - 1; i >= redeclared; i--)
                _redeclaredInTransaction[i].Collection.Redeclare(_redeclaredInTransaction[i].Previous);
            _redeclaredInTransaction.RemoveRange(redeclared, _redeclaredInTransaction.Count - redeclared);
            for (var i = created; i < _createdInTransaction.Count; i++) _open.Remove(_createdInTransaction[i]);
            _createdInTransaction.RemoveRange(created, _createdInTransaction.Count - created);
        }
    }
}

internal sealed class SqliteCollection : IDataCollection
{
    /// <summary>
    /// A declaration as one value: which fields a query may name, their column order, and the statements that write them.
    /// It is swapped whole and read once per call, so a call that began under one declaration finishes under it while a
    /// hot swap takes on the next (two generations of a plugin may be running, and a redeclaration runs outside the gate
    /// the statements take).
    /// </summary>
    private sealed record Shape(Dictionary<string, DataFieldType> Fields, string[] Order, string PutSql, string InsertSql);

    private readonly Database _db;
    private readonly string _table;
    private volatile Shape _shape;

    private SqliteCollection(Database db, string name, string table, Dictionary<string, DataFieldType> fields)
    {
        _db = db;
        Name = name;
        _table = table;
        _shape = Declare(table, fields);
    }

    /// <summary>A declaration: which fields a query may name, and the statements that write them.</summary>
    private static Shape Declare(string table, Dictionary<string, DataFieldType> fields)
    {
        var order = fields.Keys.ToArray();   // field names in column order
        var columns = string.Concat(order.Select(f => ", f_" + f));
        var values = string.Concat(order.Select((_, i) => ", @v" + i.ToString(CultureInfo.InvariantCulture)));
        var updates = string.Concat(order.Select((f, i) => ", f_" + f + " = @v" + i.ToString(CultureInfo.InvariantCulture)));
        return new Shape(fields, order,
            $"INSERT INTO {table}(k, doc{columns}) VALUES(@k, @doc{values}) ON CONFLICT(k) DO UPDATE SET doc = excluded.doc{updates}",
            $"INSERT INTO {table}(k, doc{columns}) VALUES(@k, @doc{values}) ON CONFLICT(k) DO NOTHING");
    }

    public string Name { get; }

    /// <summary>The declaration the collection has now (what a failed or rolled back change goes back to).</summary>
    internal Dictionary<string, DataFieldType> Declaration => _shape.Fields;

    internal void Redeclare(Dictionary<string, DataFieldType> fields) => _shape = Declare(_table, fields);

    internal bool SameFields(Dictionary<string, DataFieldType> other)
    {
        var fields = _shape.Fields;
        return other.Count == fields.Count && other.All(f => fields.TryGetValue(f.Key, out var t) && t == f.Value);
    }

    private static string SqlType(DataFieldType t) => t switch { DataFieldType.Text => "TEXT", DataFieldType.Integer => "INTEGER", _ => "REAL" };

    /// <summary>The field type a column was created for (<see cref="SqlType"/> the other way round).</summary>
    private static DataFieldType FieldType(string sqlType) => sqlType switch { "INTEGER" => DataFieldType.Integer, "REAL" => DataFieldType.Real, _ => DataFieldType.Text };

    /// <summary>Creates the collection's table, or brings an existing one to the declared fields: a new field gets a column, an index and a back-fill.</summary>
    internal static SqliteCollection OpenOrUpgrade(Database db, string plugin, string name, Dictionary<string, DataFieldType> fields, SqliteCollection? existing)
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
        // The field columns the table has, each with the type it was created with. A column outlives the field it was made
        // for (a dropped field keeps its column), and its type is the type the field has to come back with: declared again as
        // another type, the values would land in a column of the other affinity, where they neither compare nor order right.
        var physical = db.Query($"PRAGMA table_info({tableName})", null, r => (Name: r.GetString("name"), Type: r.GetString("type")))
            .Where(c => c.Name.StartsWith("f_", StringComparison.Ordinal))
            .ToDictionary(c => c.Name["f_".Length..], c => c.Type, StringComparer.Ordinal);
        var backfill = new List<string>();
        foreach (var (field, type) in fields)
        {
            var wasDeclared = stored[field]?.GetValue<string>();
            if (wasDeclared is not null && wasDeclared != type.ToString())
                throw new StorageException($"Collection '{name}' of plugin '{plugin}': field '{field}' was declared {wasDeclared} and is now {type}; a field's type cannot change");
            if (!physical.TryGetValue(field, out var columnType))
            {
                db.Execute($"ALTER TABLE {tableName} ADD COLUMN f_{field} {SqlType(type)}");
                db.Execute($"CREATE INDEX ix_{tableName}_{field} ON {tableName}(f_{field})");
                backfill.Add(field);
            }
            else if (columnType != SqlType(type))
            {
                // the column of an earlier declaration (one since dropped): the field keeps the type it had then
                throw new StorageException($"Collection '{name}' of plugin '{plugin}': field '{field}' was declared {FieldType(columnType)} and is now {type}; a field's type cannot change");
            }
            else if (wasDeclared is null)
            {
                backfill.Add(field);   // the column exists from an earlier declaration and may be stale
            }
        }
        db.Execute("UPDATE _collections SET spec = @spec WHERE id = @id", new { spec = specJson.ToJsonString(), id = row.Id });
        var collection = existing ?? new SqliteCollection(db, name, tableName, fields);
        var previous = collection._shape;
        collection._shape = Declare(tableName, fields);
        try
        {
            if (backfill.Count > 0) collection.Backfill(backfill);
        }
        catch
        {
            // the caller's transaction rolls the columns back; the handle the plugin keeps must not keep the declaration they were for
            collection._shape = previous;
            throw;
        }
        return collection;
    }

    private void Backfill(List<string> fields)
    {
        var shape = _shape;
        foreach (var (key, text) in _db.Query($"SELECT k, doc FROM {_table}", null, r => (r.GetString("k"), r.GetString("doc"))))
        {
            var doc = JsonNode.Parse(text) as JsonObject ?? [];
            var sets = string.Join(", ", fields.Select((f, i) => $"f_{f} = @v{i}"));
            var args = new Dictionary<string, object?> { ["k"] = key };
            for (var i = 0; i < fields.Count; i++) args["v" + i.ToString(CultureInfo.InvariantCulture)] = FieldValue(shape, fields[i], doc);
            _db.Execute($"UPDATE {_table} SET {sets} WHERE k = @k", args);
        }
    }

    // ------------------------------------------------------------------ documents

    public JsonObject? Get(string key) { StorageNames.CheckKey(key); return Read(key); }

    private JsonObject? Read(string key) => _db.QuerySingle($"SELECT doc FROM {_table} WHERE k = @k", new { k = key }, r => Parse(r.GetString("doc")));

    public void Put(string key, JsonObject doc)
    {
        StorageNames.CheckKey(key);
        var shape = _shape;   // one declaration for the whole call
        _db.Execute(shape.PutSql, WriteArgs(shape, key, doc));
    }

    public bool Insert(string key, JsonObject doc)
    {
        StorageNames.CheckKey(key);
        var shape = _shape;
        return _db.Execute(shape.InsertSql, WriteArgs(shape, key, doc)) > 0;
    }

    public bool Delete(string key) { StorageNames.CheckKey(key); return DeleteRow(key); }

    private bool DeleteRow(string key) => _db.Execute($"DELETE FROM {_table} WHERE k = @k", new { k = key }) > 0;

    public IReadOnlyList<DataDoc> Find(DataQuery? query = null)
    {
        var shape = _shape;
        var args = new Dictionary<string, object?>();
        var sql = $"SELECT k, doc FROM {_table}{Where(shape, query, args)}{OrderBy(shape, query)}";
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
        var shape = _shape;
        var args = new Dictionary<string, object?>();
        return _db.Scalar<long?>($"SELECT COUNT(*) FROM {_table}{Where(shape, query, args)}", args) ?? 0;
    }

    public double Sum(string field, DataQuery? query = null)
    {
        var shape = _shape;
        if (!shape.Fields.TryGetValue(field, out var type)) throw new ArgumentException($"'{field}' is not an index field of '{Name}'");
        if (type == DataFieldType.Text) throw new ArgumentException($"'{field}' is a Text field and cannot be summed");
        var args = new Dictionary<string, object?>();
        return _db.Scalar<double?>($"SELECT TOTAL(f_{field}) FROM {_table}{Where(shape, query, args)}", args) ?? 0;
    }

    public int DeleteWhere(DataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var shape = _shape;
        var args = new Dictionary<string, object?>();
        return _db.Execute($"DELETE FROM {_table}{Where(shape, query, args)}", args);
    }

    // ------------------------------------------------------------------ queries

    private string Where(Shape shape, DataQuery? query, Dictionary<string, object?> args)
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
            if (!shape.Fields.TryGetValue(f.Field, out var type)) throw new ArgumentException($"'{f.Field}' is not an index field of '{Name}'");
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
                {
                    var values = (f.Value as System.Collections.IEnumerable ?? throw new ArgumentException($"{f.Op} on '{f.Field}' needs a list of values"))
                        .Cast<object?>().ToList();
                    // Every value is a parameter of the one statement, and SQLite caps those: the port caps the list the same for every provider.
                    if (values.Count > StorageNames.MaxListValues)
                        throw new ArgumentException($"{f.Op} on '{f.Field}' names {values.Count} values; a list holds at most {StorageNames.MaxListValues}");
                    if (values.Count == 0) { parts.Add(f.Op == DataOp.In ? "0" : $"{col} IS NOT NULL"); break; }
                    var list = string.Join(", ", values.Select(v => Param(v ?? throw new ArgumentException($"{f.Op} on '{f.Field}' has a null value"), type)));
                    parts.Add($"{col} {(f.Op == DataOp.In ? "IN" : "NOT IN")} ({list})");
                    break;
                }
                default: throw new ArgumentException($"Unknown operator {f.Op}");
            }
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    private static object RequireValue(DataFilter f) =>
        f.Value ?? throw new ArgumentException($"{f.Op} on '{f.Field}' needs a value; use IsNull / NotNull to test for no value");

    private string OrderBy(Shape shape, DataQuery? query)
    {
        var sb = new StringBuilder(" ORDER BY ");
        if (query is not null)
            foreach (var o in query.OrderBy)
            {
                if (!shape.Fields.ContainsKey(o.Field)) throw new ArgumentException($"'{o.Field}' is not an index field of '{Name}'");
                sb.Append("f_").Append(o.Field).Append(o.Descending ? " DESC, " : " ASC, ");
            }
        return sb.Append('k').ToString();
    }

    // ------------------------------------------------------------------ values

    private Dictionary<string, object?> WriteArgs(Shape shape, string key, JsonObject doc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(doc);
        var args = new Dictionary<string, object?> { ["k"] = key, ["doc"] = doc.ToJsonString() };
        for (var i = 0; i < shape.Order.Length; i++) args["v" + i.ToString(CultureInfo.InvariantCulture)] = FieldValue(shape, shape.Order[i], doc);
        return args;
    }

    /// <summary>The value of an index field in a document, as the column's type: null when the property is missing or JSON null.</summary>
    private object? FieldValue(Shape shape, string field, JsonObject doc)
    {
        var type = shape.Fields[field];
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

    /// <summary>A filter value as the field's type: a string, a long, a double or a bool (0/1 in an integer field), and an int as the long it is — the same set the memory provider takes, so a query that runs on one provider runs on the other.</summary>
    private static object Coerce(object? value, DataFieldType type)
    {
        switch (type)
        {
            case DataFieldType.Text:
                if (value is string s) return s;
                break;
            case DataFieldType.Integer:
                if (value is long l) return l;
                if (value is int i) return (long)i;
                if (value is bool b) return b ? 1L : 0L;
                break;
            case DataFieldType.Real:
                if (value is double d) return d;
                if (value is long rl) return (double)rl;
                if (value is int ri) return (double)ri;
                break;
        }
        throw new ArgumentException($"A {value?.GetType().Name ?? "null"} value does not fit a {type} field");
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text) as JsonObject ?? [];
}
