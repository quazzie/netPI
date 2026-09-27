using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Shell;

// Private copy of the file plugin's argument helpers (each plugin is self-contained).
/// <summary>
/// Lenient access to tool arguments. Names are matched ignoring case, '_' and '-'
/// (so <c>file_path</c>, <c>filePath</c> and <c>FilePath</c> are the same), numbers may arrive as strings,
/// booleans as "true"/"yes"/1, and a double-encoded JSON string object is unwrapped.
/// </summary>
internal readonly struct ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;

    public JsonElement Raw { get; }

    public ToolArgs(JsonElement args)
    {
        _props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.String)
        {
            // Some models send the arguments object as a JSON string.
            try
            {
                using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        Raw = args;
        if (args.ValueKind != JsonValueKind.Object) return;
        foreach (var p in args.EnumerateObject())
            _props.TryAdd(Key(p.Name), p.Value);
    }

    private static string Key(string name)
    {
        Span<char> buf = stackalloc char[name.Length];
        var n = 0;
        foreach (var c in name)
            if (c != '_' && c != '-' && c != ' ') buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    public bool Has(params string[] names) => TryGet(out _, names);

    public bool TryGet(out JsonElement value, params string[] names)
    {
        foreach (var n in names)
            if (_props.TryGetValue(Key(n), out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return true;
        value = default;
        return false;
    }

    /// <summary>String value (numbers/bools are returned as their JSON text). Null when absent.</summary>
    public string? Str(params string[] names)
    {
        if (!TryGet(out var v, names)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => string.Join("\n", v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
            _ => v.GetRawText(),
        };
    }

    public int? Int(params string[] names)
    {
        if (!TryGet(out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetDouble(out var d)) return d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)d;
        }
        return null;
    }

    public double? Double(params string[] names)
    {
        if (!TryGet(out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public bool? Bool(params string[] names)
    {
        if (!TryGet(out var v, names)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.TryGetDouble(out var d) && d != 0;
            case JsonValueKind.String:
                var s = v.GetString()?.Trim().ToLowerInvariant();
                if (s is "true" or "yes" or "1" or "on" or "y") return true;
                if (s is "false" or "no" or "0" or "off" or "n" or "") return false;
                return null;
            default: return null;
        }
    }

    /// <summary>Array elements; a single object/string is returned as a one-element list.
    /// A string containing a JSON array is parsed.</summary>
    public List<JsonElement>? List(params string[] names)
    {
        if (!TryGet(out var v, names)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim() ?? "";
            if (s.StartsWith('[') || s.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    v = doc.RootElement.Clone();
                }
                catch (JsonException) { }
            }
        }
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v];
    }
}

/// <summary>Tiny JSON-schema builder for tool parameter definitions.</summary>
internal static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, req) in props)
        {
            properties[name] = schema;
            if (req) required.Add(name);
        }
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) o["required"] = required;
        return o;
    }

    public static JsonObject Str(string description, params string[] enumValues)
    {
        var o = Typed("string", description);
        if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)e).ToArray());
        return o;
    }

    public static JsonObject Int(string description) => Typed("integer", description);
    public static JsonObject Bool(string description) => Typed("boolean", description);
    public static JsonObject Array(string description, JsonObject items) { var o = Typed("array", description); o["items"] = items; return o; }

    /// <summary>A typed property; an empty description is left out (every request carries the schema).</summary>
    private static JsonObject Typed(string type, string description)
    {
        var o = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(description)) o["description"] = description;
        return o;
    }
}

internal static class SettingsExtensions
{
    /// <summary>Settings read that never throws (wrong types in settings.json fall back to the default).</summary>
    public static T SafeGet<T>(this ISettings? settings, string path, T defaultValue)
    {
        if (settings is null) return defaultValue;
        try
        {
            var node = settings.GetNode(path);
            if (node is null) return defaultValue;
            if (node is JsonValue v)
            {
                if (v.TryGetValue<T>(out var t)) return t;
                // "120" for an int setting, 120 for a string setting...
                var s = v.ToString();
                if (typeof(T) == typeof(string)) return (T)(object)s;
                if (typeof(T) == typeof(int) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return (T)(object)i;
                if (typeof(T) == typeof(double) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (T)(object)d;
                if (typeof(T) == typeof(bool) && bool.TryParse(s, out var b)) return (T)(object)b;
            }
            return settings.Get(path, defaultValue) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
