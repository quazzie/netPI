using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// NOTE: mirrored in NetPI.Providers.{AiProxy,Anthropic,OpenRouter}/Common (plugins cannot share code). Keep the copies in sync.
namespace NetPI.Providers.OpenRouter;

/// <summary>Tolerant accessors over JsonNode/JsonElement (servers disagree about types).</summary>
internal static class J
{
    public static string? Str(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }

    public static bool Bool(this JsonNode? node, string key, bool dflt)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return dflt;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<string>(out var s)) return bool.TryParse(s, out var sb) ? sb : dflt;
        return dflt;
    }

    public static int? Int(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
        if (jv.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var el)) return (int)el;
        return null;
    }

    public static JsonObject? Obj(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;

    public static JsonArray? Arr(this JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;

    // ---------------------------------------------------------------- JsonElement (hot path parsing)

    public static string? Str(this JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }

    public static long Long(this JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number) return (long)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    public static int? IntOrNull(this JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static JsonElement Prop(this JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;

    public static bool Has(this JsonElement e) => e.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public static bool IsObj(this JsonElement e) => e.ValueKind == JsonValueKind.Object;

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public static byte[] Utf8(JsonNode body) => Encoding.UTF8.GetBytes(body.ToJsonString(NetPiJson.Options));
}
