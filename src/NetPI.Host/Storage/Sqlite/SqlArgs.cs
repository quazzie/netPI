using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Sqlite;

/// <summary>
/// Resolves named SQL parameters from an argument object: anonymous/POCO objects (cached reflection, safe for
/// collectible plugin types), <see cref="IDictionary{TKey,TValue}"/>, <see cref="IDictionary"/> and <see cref="JsonObject"/>.
/// Positional <c>?</c> parameters are taken from an array/list.
/// </summary>
internal static class SqlArgs
{
    // Weak keys: plugin types must not be rooted by this cache or their load context could never unload.
    private static readonly ConditionalWeakTable<Type, Dictionary<string, Func<object, object?>>> Accessors = new();

    public static bool IsPositional(object args) =>
        args is IList and not byte[] && args is not JsonNode && args is not IDictionary;

    public static bool TryGet(object args, string name, out object? value)
    {
        switch (args)
        {
            case JsonObject jo:
                if (jo.TryGetPropertyValue(name, out var node) || TryFindCaseInsensitive(jo, name, out node))
                {
                    value = FromJsonNode(node);
                    return true;
                }
                value = null;
                return false;

            case IDictionary<string, object?> d:
                if (d.TryGetValue(name, out value)) return true;
                foreach (var kv in d)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
                value = null;
                return false;

            case IReadOnlyDictionary<string, object?> rd:
                if (rd.TryGetValue(name, out value)) return true;
                foreach (var kv in rd)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
                value = null;
                return false;

            case IDictionary nd:
                if (nd.Contains(name)) { value = nd[name]; return true; }
                foreach (DictionaryEntry kv in nd)
                    if (kv.Key is string k && string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
                value = null;
                return false;

            default:
                var map = Accessors.GetValue(args.GetType(), BuildAccessors);
                if (map.TryGetValue(name, out var getter)) { value = getter(args); return true; }
                value = null;
                return false;
        }
    }

    public static object? GetPositional(object args, int index, string sql)
    {
        var list = (IList)args;
        if (index >= list.Count)
            throw new ArgumentException($"SQL expects at least {index + 1} positional arguments, got {list.Count}\n  SQL: {sql}");
        return list[index];
    }

    private static bool TryFindCaseInsensitive(JsonObject jo, string name, out JsonNode? node)
    {
        foreach (var kv in jo)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { node = kv.Value; return true; }
        node = null;
        return false;
    }

    /// <summary>JSON values bind as their natural SQL type; objects/arrays bind as raw JSON text.</summary>
    public static object? FromJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        switch (node.GetValueKind())
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return 1L;
            case JsonValueKind.False:
                return 0L;
            case JsonValueKind.String:
                return node.Deserialize<string>();
            case JsonValueKind.Number:
                var raw = node.ToJsonString();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            default:
                return node.ToJsonString();
        }
    }

    private static Dictionary<string, Func<object, object?>> BuildAccessors(Type type)
    {
        var map = new Dictionary<string, Func<object, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            var prop = p;
            map.TryAdd(p.Name, o => prop.GetValue(o));
        }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var field = f;
            map.TryAdd(f.Name, o => field.GetValue(o));
        }
        return map;
    }
}

/// <summary>Conversions of SQLite values (long/double/string/byte[]/null) to requested CLR types.</summary>
internal static class DbConvert
{
    public static T? To<T>(object? value)
    {
        var target = typeof(T);
        if (value is null) return default;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying.IsInstanceOfType(value) && underlying != typeof(object)) return (T)value;
        if (underlying == typeof(object)) return (T)value;
        return (T?)Convert(value, underlying);
    }

    public static object? Convert(object value, Type t)
    {
        if (t == typeof(string))
            return value switch
            {
                byte[] b => System.Text.Encoding.UTF8.GetString(b),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        if (t == typeof(bool)) return value switch { long l => l != 0, double d => d != 0, string s => s is "1" || bool.TryParse(s, out var b) && b, _ => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture) };
        if (t == typeof(DateTimeOffset)) return value switch { long l => DateTimeOffset.FromUnixTimeMilliseconds(l), string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture), _ => DateTimeOffset.FromUnixTimeMilliseconds(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)) };
        if (t == typeof(DateTime)) return value switch { long l => DateTimeOffset.FromUnixTimeMilliseconds(l).UtcDateTime, string s => DateTime.Parse(s, CultureInfo.InvariantCulture), _ => DateTimeOffset.FromUnixTimeMilliseconds(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)).UtcDateTime };
        if (t == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
        if (t == typeof(Guid)) return value is byte[] gb && gb.Length == 16 ? new Guid(gb) : Guid.Parse(value.ToString()!);
        if (t.IsEnum) return value is string es ? Enum.Parse(t, es, ignoreCase: true) : Enum.ToObject(t, System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (t == typeof(byte[])) return value is string bs ? System.Text.Encoding.UTF8.GetBytes(bs) : value;
        if (typeof(JsonNode).IsAssignableFrom(t)) return JsonNode.Parse(value.ToString()!);
        if (t == typeof(JsonElement)) { using var doc = JsonDocument.Parse(value.ToString()!); return doc.RootElement.Clone(); }
        return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }
}
