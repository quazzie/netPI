using System.Text.Json;

namespace NetPI.Tools.Agents;

/// <summary>Lenient argument access: names match ignoring case, '_' and '-'; numbers/bools may be strings.</summary>
internal static class ToolArgs
{
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    /// <summary>Unwrap arguments that were sent as a JSON string.</summary>
    public static JsonElement Unwrap(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
                return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return args;
    }

    public static JsonElement? Get(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            var n = Norm(name);
            foreach (var p in args.EnumerateObject())
                if (Norm(p.Name) == n && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        }
        return null;
    }

    public static string? Str(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
        _ => null,
    };

    public static bool? Bool(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v when bool.TryParse(v.GetString(), out var b) => b,
        { ValueKind: JsonValueKind.String } v when v.GetString() is "1" or "yes" => true,
        { ValueKind: JsonValueKind.String } v when v.GetString() is "0" or "no" => false,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble() != 0,
        _ => null,
    };

    public static double? Num(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>An array of strings, a JSON-encoded array, or a comma/space separated string.</summary>
    public static List<string>? List(JsonElement args, params string[] names)
    {
        var v = Get(args, names);
        if (v is null) return null;
        var e = v.Value;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString()?.Trim() ?? "";
            if (s.StartsWith('['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    e = doc.RootElement.Clone();
                }
                catch (JsonException) { }
            }
            if (e.ValueKind == JsonValueKind.String)
                return s.Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        if (e.ValueKind == JsonValueKind.Array)
            return e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim())
                .Where(x => x.Length > 0).ToList();
        return null;
    }
}
