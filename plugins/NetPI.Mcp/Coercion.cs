using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

/// <summary>
/// Repairs arguments a model wrote as JSON text inside a string, using the discovered schema as the only authority.
/// A remote tool's schema arrives after the model has already seen the call envelope, so it writes
/// <c>{"list_only":"true","timeout":"10","config":"{\"views\":[ … ]}"}</c> often enough to make typed arguments
/// unusable. Nothing here guesses: a value is only re-read when the schema for that exact path asks for a
/// non-string type, the text parses exactly to it, and the result is left alone when the schema allows a string.
/// The caller repairs only after plain validation has already failed, so an argument the model got right is never
/// touched.
/// </summary>
internal static class Coercion
{
    /// <summary>The repaired arguments, or null when nothing was repaired (the caller then reports the original error).</summary>
    public static JsonObject? Repair(JsonObject root, JsonObject arguments)
    {
        var repaired = Walk(arguments, root, root, 0) as JsonObject;
        if (repaired is null || JsonNode.DeepEquals(repaired, arguments)) return null;
        return repaired;
    }

    private static JsonNode? Walk(JsonNode? value, JsonNode? schema, JsonObject root, int depth)
    {
        if (depth > 32) return value;
        if (value is not JsonValue text || !text.TryGetValue<string>(out var raw)) return Structure(value, schema, root, depth);
        var types = Types(schema, root);
        // A schema that permits a string, or says nothing about the type, keeps what the model wrote.
        if (types.Count == 0 || types.Contains("string")) return value;
        foreach (var type in types)
        {
            switch (type)
            {
                case "boolean" when raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase): return JsonValue.Create(true);
                case "boolean" when raw.Trim().Equals("false", StringComparison.OrdinalIgnoreCase): return JsonValue.Create(false);
                case "integer" when Integer(raw) is { } integer: return JsonValue.Create(integer);
                case "number" when Number(raw) is { } number: return JsonValue.Create(number);
                case "object" when TryParse(raw) is JsonObject o: return Structure(o, schema, root, depth + 1);
                case "array" when TryParse(raw) is JsonArray a: return Structure(a, schema, root, depth + 1);
            }
        }
        return value;
    }

    /// <summary>Walks into objects and arrays the model already typed, repairing the values inside them.</summary>
    private static JsonNode? Structure(JsonNode? value, JsonNode? schema, JsonObject root, int depth)
    {
        if (schema is null || depth > 32) return value;
        if (value is JsonObject obj)
        {
            var properties = Properties(schema, root);
            if (properties is null) return value;
            var copy = new JsonObject();
            // Cloned, because a value left as it was still belongs to the arguments it came from.
            foreach (var (key, child) in obj) copy[key] = (Walk(child, properties[key] ?? Extra(schema, root), root, depth + 1) ?? child).DeepClone();
            return copy;
        }
        if (value is JsonArray array)
        {
            var items = schema["items"];
            if (items is null) return value;
            var copy = new JsonArray();
            foreach (var child in array) copy.Add((Walk(child, items, root, depth + 1) ?? child).DeepClone());
            return copy;
        }
        return value;
    }

    private static long? Integer(string text)
    {
        var trimmed = text.Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
        return value.ToString(CultureInfo.InvariantCulture) == trimmed ? value : null; // "007" and "+1" are not the number the model wrote
    }

    private static double? Number(string text)
    {
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return null;
        return value.ToString("R", CultureInfo.InvariantCulture) == trimmed ? value : null;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text.Trim(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return null; }
    }

    /// <summary>The types a value at this path may have: its own <c>type</c> plus the branches of anyOf/oneOf.</summary>
    private static List<string> Types(JsonNode? schema, JsonObject root)
    {
        var list = new List<string>();
        var node = Deref(schema, root);
        if (node is not JsonObject o) return list;
        void Add(JsonNode? type) => list.AddRange(type switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => [s],
            JsonArray a => a.Select(x => x is JsonValue av && av.TryGetValue<string>(out var text) ? text : "").Where(s => s.Length > 0),
            _ => [],
        });
        Add(o["type"]);
        foreach (var key in new[] { "anyOf", "oneOf" })
            if (o[key] is JsonArray branches)
                foreach (var branch in branches) Add(Deref(branch, root)?["type"]);
        return [.. list.Distinct()];
    }

    /// <summary>The property schemas of an object, or null when the value is not described as an object.</summary>
    private static JsonObject? Properties(JsonNode? schema, JsonObject root) =>
        (Deref(schema, root) as JsonObject)?["properties"] as JsonObject;

    private static JsonNode? Extra(JsonNode? schema, JsonObject root) =>
        Deref(schema, root)?["additionalProperties"];

    private static JsonNode? Deref(JsonNode? schema, JsonObject root)
    {
        for (var hop = 0; hop < 8 && schema is JsonObject o && o["$ref"] is { } reference; hop++)
        {
            var target = Schema.Resolve(root, reference.GetValue<string>());
            if (target is null) return schema;
            schema = target;
        }
        return schema;
    }
}
