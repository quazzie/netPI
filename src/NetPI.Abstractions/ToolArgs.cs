using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// Lenient access to tool arguments: the one reader every tool, hook and guard shares. Names are matched ignoring
/// case, '_' and '-' (so <c>file_path</c>, <c>filePath</c> and <c>FilePath</c> are the same), numbers may arrive as
/// strings, booleans as "true"/"yes"/1, and a double-encoded JSON string object is unwrapped — the runner unwraps it
/// for the tools once, and this keeps the same result for what a hook or a test builds by hand.
/// </summary>
public readonly struct ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;

    /// <summary>The arguments root after unwrapping: an object with the properties to read, or whatever was sent.</summary>
    public JsonElement Raw { get; }

    public ToolArgs(JsonElement args)
    {
        _props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Raw = Unwrap(args);
        if (Raw.ValueKind != JsonValueKind.Object) return;
        foreach (var p in Raw.EnumerateObject())
            _props.TryAdd(Key(p.Name), p.Value);
    }

    /// <summary>
    /// The arguments object a call was sent with, when some models send it as a JSON <em>string</em>: the same element
    /// when it is not a string (or the string does not parse), otherwise the parsed one. Never throws.
    /// </summary>
    public static JsonElement Unwrap(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return args;
    }

    /// <summary>
    /// Tool arguments from the raw JSON string of a <see cref="ToolCallPart"/> — the hooks' view of a call: an empty
    /// string is an empty object, and input that does not parse reads no properties (a call that cannot be read
    /// cannot be judged, and the tool will fail it anyway).
    /// </summary>
    public static ToolArgs Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new ToolArgs(EmptyObject);
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return new ToolArgs(doc.RootElement.Clone());
        }
        catch (JsonException) { return new ToolArgs(default); }
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

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
public static class ToolSchema
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
