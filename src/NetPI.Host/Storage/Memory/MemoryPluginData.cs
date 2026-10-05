using System.Collections;
using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Memory;

/// <summary>All the plugin data, by plugin id. Each entry is what a hot reload inherits: the same collections, the same lock.</summary>
internal sealed class PluginData
{
    /// <summary>This plugin id's own lock. A transaction takes it <em>before</em> the provider's, so a transaction of one
    /// plugin id is exclusive across every generation of that plugin and never waits on one from inside the other.</summary>
    public readonly object Gate = new();

    public readonly Dictionary<string, Collection> Collections = new(StringComparer.Ordinal);
}

/// <summary>One collection: its declared index fields and its documents, keyed (ordinal) by the caller's key.</summary>
internal sealed class Collection(string name)
{
    public string Name { get; } = name;
    /// <summary>The declared index fields, replaced whole by a changed declaration.</summary>
    public Dictionary<string, DataFieldType> Fields = new(StringComparer.Ordinal);
    /// <summary>
    /// The index fields a declaration dropped, each with the type it had, replaced whole like <see cref="Fields"/>. A field
    /// keeps its type even then: declared again as another type it is refused, as the sqlite provider refuses it (there the
    /// dropped field's column stays, and with it the type).
    /// </summary>
    public Dictionary<string, DataFieldType> Retired = new(StringComparer.Ordinal);
    public readonly Dictionary<string, JsonObject> Docs = new(StringComparer.Ordinal);
    /// <summary>The instance handed to callers, so the same name again is the same collection.</summary>
    public IDataCollection? Opened;
}

internal sealed class MemoryPluginDataStore(MemoryStorage store) : IPluginDataStore
{
    public IPluginData For(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (store.Lock)
        {
            if (!store.PluginsById.TryGetValue(pluginId, out var data))
            {
                data = new PluginData();
                store.PluginsById[pluginId] = data;
            }
            // A new view of one plugin's data, not a new copy of it: the collections and the transaction lock belong
            // to the plugin id, so a reloaded generation sees the same ones and cannot interleave with its predecessor.
            return new MemoryPluginData(store, pluginId, data);
        }
    }
}

/// <summary>One plugin's data. Its collections live in the store, under its own id.</summary>
internal sealed class MemoryPluginData(MemoryStorage store, string pluginId, PluginData data) : IPluginData
{
    public IDataCollection Collection(string name, CollectionSpec spec)
    {
        StorageNames.CheckCollection(name);
        StorageNames.CheckSpec(spec);
        lock (store.Lock)
            return store.Apply(() =>
            {
                if (data.Collections.TryGetValue(name, out var declared))
                    foreach (var (field, type) in spec.Fields)
                    {
                        if (declared.Fields.TryGetValue(field, out var was) && was != type)
                            throw new StorageException($"Collection '{name}' of plugin '{pluginId}': field '{field}' was declared {was} and is now {type}; a field's type cannot change");
                        if (declared.Retired.TryGetValue(field, out var had) && had != type)
                            throw new StorageException($"Collection '{name}' of plugin '{pluginId}': field '{field}' was declared {had} and is now {type}; a field's type cannot change");
                    }
                if (!data.Collections.TryGetValue(name, out var collection))
                {
                    collection = new Collection(name);
                    data.Collections[name] = collection;
                }
                if (collection.Fields.Count != spec.Fields.Count || spec.Fields.Any(f => !collection.Fields.TryGetValue(f.Key, out var t) || t != f.Value))
                {
                    // A field that is new to the declaration is read from every stored document (the sqlite provider back-fills its
                    // column): a document holding the wrong kind of value in it refuses the declaration, and nothing has changed yet.
                    foreach (var (field, type) in spec.Fields)
                        if (!collection.Fields.ContainsKey(field))
                            foreach (var stored in collection.Docs.Values) MemoryDataCollection.CheckFieldType(stored, field, type);
                    // A changed declaration is applied to the documents that are already stored. In memory that needs
                    // no rewrite: a document that does not carry the new field is a field with no value, which is
                    // exactly what a row stored before the column was added reads as.
                    store.Touch(() => (Dictionary<string, DataFieldType>?)collection.Fields, was => { if (was is not null) collection.Fields = was; });
                    store.Touch(() => (Dictionary<string, DataFieldType>?)collection.Retired, was => { if (was is not null) collection.Retired = was; });
                    // A field the declaration drops keeps its type (checked above when it is declared again); one it takes back is live again.
                    var retired = new Dictionary<string, DataFieldType>(collection.Retired, StringComparer.Ordinal);
                    foreach (var (field, type) in collection.Fields)
                        if (!spec.Fields.ContainsKey(field)) retired[field] = type;
                    foreach (var field in spec.Fields.Keys) retired.Remove(field);
                    collection.Retired = retired;
                    collection.Fields = new Dictionary<string, DataFieldType>(spec.Fields, StringComparer.Ordinal);
                }
                if (collection.Opened is null) collection.Opened = new MemoryDataCollection(store, pluginId, collection);
                return collection.Opened;
            });
    }

    public T Transaction<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The plugin's own lock first, the provider's inside it: two transactions of this plugin id never overlap, and
        // two plugins are not kept apart by it.
        lock (data.Gate)
            return store.Atomic(work);
    }

    public void Transaction(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Transaction<object?>(() => { work(); return null; });
    }
}

/// <summary>
/// A named set of JSON documents. A call outside <see cref="MemoryPluginData.Transaction{T}"/> is one atomic unit of its
/// own; what a caller gets back is its own copy, so nothing it does to a document reaches the store.
/// </summary>
internal sealed class MemoryDataCollection(MemoryStorage store, string pluginId, Collection collection) : IDataCollection
{
    public string Name => collection.Name;

    public JsonObject? Get(string key)
    {
        StorageNames.CheckKey(key);
        lock (store.Lock)
            return store.Read(() => collection.Docs.TryGetValue(key, out var doc) ? Copy(doc) : null);
    }

    /// <summary>A document whose value in a declared index field is not of the field's type is refused (as the sqlite provider refuses it), never stored as "no value".</summary>
    private void CheckFieldTypes(JsonObject doc)
    {
        foreach (var (field, type) in collection.Fields) CheckFieldType(doc, field, type);
    }

    internal static void CheckFieldType(JsonObject doc, string field, DataFieldType type)
    {
        if (doc.TryGetPropertyValue(field, out var node) && node is not null && node.GetValueKind() is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            && Field(node, type) is null)
            throw new ArgumentException($"Field '{field}' is declared {type}, and the document holds {node.GetValueKind()}");
    }

    public void Put(string key, JsonObject doc)
    {
        StorageNames.CheckKey(key);
        ArgumentNullException.ThrowIfNull(doc);
        CheckFieldTypes(doc);
        lock (store.Lock)
            store.Apply(() => Write(key, doc));
    }

    public bool Insert(string key, JsonObject doc)
    {
        StorageNames.CheckKey(key);
        ArgumentNullException.ThrowIfNull(doc);
        CheckFieldTypes(doc);
        lock (store.Lock)
            return store.Apply(() =>
            {
                if (collection.Docs.ContainsKey(key)) return false;
                Write(key, doc);
                return true;
            });
    }

    public bool Delete(string key)
    {
        StorageNames.CheckKey(key);
        lock (store.Lock)
            return store.Apply(() =>
            {
                if (!collection.Docs.ContainsKey(key)) return false;
                Keep(key);
                collection.Docs.Remove(key);
                return true;
            });
    }

    public IReadOnlyList<DataDoc> Find(DataQuery? query = null)
    {
        lock (store.Lock)
            return store.Read<IReadOnlyList<DataDoc>>(() =>
            {
                var rows = Match(query);
                if (query?.Offset is { } offset and > 0) rows = rows.Skip(offset).ToList();
                if (query?.Limit is { } limit) rows = rows.Take(Math.Max(0, limit)).ToList();
                return [.. rows.Select(r => new DataDoc(r.Key, Copy(r.Doc)))];
            });
    }

    public long Count(DataQuery? query = null)
    {
        lock (store.Lock)
            return store.Read(() => (long)Match(query).Count);
    }

    public double Sum(string field, DataQuery? query = null)
    {
        lock (store.Lock)
            return store.Read(() =>
            {
                var type = FieldType(field);       // a field this collection does not declare is not a field here
                if (type == DataFieldType.Text)
                    throw new ArgumentException($"'{field}' is a text field: only an integer or real field has a sum");
                var total = 0d;
                foreach (var row in Match(query))
                {
                    // A document with no value adds nothing, like a NULL in SQL's SUM.
                    if (FieldValue(row.Doc, field, type) is { } value) total += Convert.ToDouble(value);
                }
                return total;
            });
    }

    public int DeleteWhere(DataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (store.Lock)
            return store.Apply(() =>
            {
                // A query's order and its page are ignored: what is deleted is what matched.
                var rows = Match(query);
                foreach (var row in rows)
                {
                    Keep(row.Key);
                    collection.Docs.Remove(row.Key);
                }
                return rows.Count;
            });
    }

    // ------------------------------------------------------------------ writing

    /// <summary>The caller's own copy: nothing it does to a document it put, or to one it got back, reaches the store.</summary>
    private static JsonObject Copy(JsonObject doc) => (JsonObject)doc.DeepClone();

    private void Write(string key, JsonObject doc)
    {
        Keep(key);
        collection.Docs[key] = Copy(doc);
    }

    private void Keep(string key) =>
        store.Touch(() => (JsonObject?)collection.Docs.GetValueOrDefault(key), was =>
        {
            if (was is null) collection.Docs.Remove(key);
            else collection.Docs[key] = was;
        });

    // ------------------------------------------------------------------ querying

    /// <summary>
    /// The documents a query matches, in the query's order. Key order (ascending, ordinal) is where every tie
    /// starts, and a field or a value the query gets wrong throws before a document is looked at.
    /// </summary>
    private List<DataDoc> Match(DataQuery? query)
    {
        var filters = (query?.Where ?? []).Select(f => Plan(f)).ToList();
        var orders = (query?.OrderBy ?? []).Select(Plan).ToList();
        var rows = collection.Docs
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Where(d => filters.All(f => f(d.Value)))
            .Select(d => new DataDoc(d.Key, d.Value))
            .ToList();
        if (orders.Count == 0) return rows;
        return rows.OrderBy(r => r, Comparer<DataDoc>.Create((a, b) =>
        {
            foreach (var (field, type, descending) in orders)
            {
                var c = Order(a.Doc[field], b.Doc[field], type, descending);
                if (c != 0) return c;
            }
            return 0;   // a tie keeps key order
        })).ToList();
    }

    private (string Field, DataFieldType Type, bool Descending) Plan(DataOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return (order.Field, FieldType(order.Field), order.Descending);
    }

    private DataFieldType FieldType(string field)
    {
        if (!collection.Fields.TryGetValue(field, out var type))
            throw new ArgumentException($"'{field}' is not an index field of collection '{Name}' of plugin '{pluginId}'");
        return type;
    }

    private Func<JsonObject, bool> Plan(DataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var type = FieldType(filter.Field);
        switch (filter.Op)
        {
            case DataOp.IsNull:
                return doc => FieldValue(doc, filter.Field, type) is null;
            case DataOp.NotNull:
                return doc => FieldValue(doc, filter.Field, type) is not null;
            case DataOp.In:
            case DataOp.NotIn:
            {
                var values = List(filter, type);
                // A comparison never matches a field with no value, an empty list included: an empty In matches
                // nothing at all, an empty NotIn matches every document that has a value.
                return filter.Op == DataOp.In
                    ? doc => FieldValue(doc, filter.Field, type) is { } v && values.Contains(v)
                    : doc => FieldValue(doc, filter.Field, type) is { } v && !values.Contains(v);
            }
            default:
            {
                var wanted = One(filter.Value, type, filter.Op, filter.Field);
                return doc =>
                {
                    // No value never compares — not for Ne either: ask for it with IsNull.
                    if (FieldValue(doc, filter.Field, type) is not { } value) return false;
                    var c = Compare(value, wanted);
                    return filter.Op switch
                    {
                        DataOp.Eq => c == 0,
                        DataOp.Ne => c != 0,
                        DataOp.Lt => c < 0,
                        DataOp.Le => c <= 0,
                        DataOp.Gt => c > 0,
                        _ => c >= 0,
                    };
                };
            }
        }
    }

    /// <summary>The value of an In/NotIn list, each entry of the field's type. An empty list is an empty set, not an error; one past the port's cap is.</summary>
    private HashSet<object?> List(DataFilter filter, DataFieldType type)
    {
        if (filter.Value is string || filter.Value is not IEnumerable values)
            throw new ArgumentException($"A {filter.Op} filter on '{filter.Field}' takes a list of values, not '{filter.Value?.GetType().Name ?? "null"}'");
        var list = values.Cast<object?>().ToList();
        // The cap is the port's, the same for every provider: a provider that binds every value as a parameter of one statement has a cap of its own.
        if (list.Count > StorageNames.MaxListValues)
            throw new ArgumentException($"{filter.Op} on '{filter.Field}' names {list.Count} values; a list holds at most {StorageNames.MaxListValues}");
        var set = new HashSet<object?>();
        foreach (var value in list) set.Add(One(value, type, filter.Op, filter.Field));
        return set;
    }

    /// <summary>A comparison's value as the field's type; a null or a value of the wrong type is a caller's mistake.</summary>
    private static object One(object? value, DataFieldType type, DataOp op, string field) => value switch
    {
        null => throw new ArgumentException($"A {op} filter on '{field}' needs a value; a field with no value is IsNull"),
        _ => Coerce(value, type, op, field) ?? throw new ArgumentException($"A {op} filter on '{field}' cannot take a {value.GetType().Name}"),
    };

    private static object? Coerce(object value, DataFieldType type, DataOp op, string field)
    {
        switch (type)
        {
            case DataFieldType.Text:
                return value as string;
            case DataFieldType.Integer:
            {
                // A bool is stored as 0/1 in an integer field, as the port says.
                if (value is bool b) return b ? 1L : 0L;
                if (value is long l) return l;
                return value is int i ? (long)i : null;
            }
            case DataFieldType.Real:
            {
                if (value is double d) return d;
                if (value is long l) return (double)l;
                return value is int i ? (double)i : null;
            }
            default:
                throw new ArgumentException($"A {op} filter on '{field}' has no value type");
        }
    }

    private static int Compare(object left, object right) => left switch
    {
        string x => string.CompareOrdinal(x, (string)right),
        long x => x.CompareTo((long)right),
        _ => ((double)left).CompareTo((double)right),
    };

    /// <summary>A field with no value sorts first ascending and last descending, so one order puts it at either end.</summary>
    private static int Order(JsonNode? a, JsonNode? b, DataFieldType type, bool descending)
    {
        var left = a is null ? null : Field(a, type);
        var right = b is null ? null : Field(b, type);
        int c;
        if (left is null || right is null)
        {
            if (left is null && right is null) return 0;
            c = left is null ? -1 : 1;
        }
        else c = Compare(left, right);
        return descending ? -c : c;
    }

    /// <summary>
    /// A node's value as the declared type of the field, or null when the document has no value there: the property
    /// is missing, JSON null, another JSON kind, or a value the declared type cannot hold (which is what a provider
    /// that keeps documents in columns would store as NULL).
    /// </summary>
    private static object? Field(JsonNode node, DataFieldType type)
    {
        if (node is not JsonValue value) return null;
            return type switch
            {
                DataFieldType.Text => value.TryGetValue<string>(out var s) ? s : null,
                DataFieldType.Integer => Integer(value),
                _ => Real(value),
            };
    }

    private static object? Integer(JsonValue value)
    {
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<bool>(out var b)) return b ? 1L : 0L;
        return value.TryGetValue<int>(out var i) ? (long)i : null;
    }

    private static object? Real(JsonValue value)
    {
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return (double)l;
        return value.TryGetValue<int>(out var i) ? (double)i : null;
    }

    private static object? FieldValue(JsonObject doc, string field, DataFieldType type) =>
        doc.TryGetPropertyValue(field, out var node) && node is not null ? Field(node, type) : null;
}
