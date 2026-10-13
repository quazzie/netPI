using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Webhooks;

/// <summary>
/// The two small languages a webhook uses on its payload: <c>{{path.to.value}}</c> in a template, and the match conditions that
/// decide whether a payload starts a run at all. A path is dot-separated names and array indexes (<c>items.0.title</c>);
/// <c>{{payload}}</c> is the whole payload as indented JSON. A missing value is an empty string, never an error: the
/// sender's payload is not under our control and a template must keep working when a field is absent.
/// </summary>
internal static partial class Template
{
    /// <summary>The most one substituted value may add, so one huge field cannot flood the prompt.</summary>
    public const int MaxValueChars = 4000;
    /// <summary>The whole payload as {{payload}}.</summary>
    public const int MaxPayloadChars = 8000;

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-\[\]]+)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <param name="whole">What <c>{{payload}}</c> renders when it is not <paramref name="payload"/> itself (a batch's frame reads the first alert's fields but must not print the helper fields added to it).</param>
    public static string Expand(string template, JsonNode? payload, DateTimeOffset now, JsonNode? whole = null) =>
        Placeholder().Replace(template, m =>
        {
            var path = m.Groups[1].Value;
            if (path == "payload") return Clip(Pretty(whole ?? payload), MaxPayloadChars);
            if (path == "now") return now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            return Clip(Show(Resolve(payload, path)), MaxValueChars);
        });

    public static JsonNode? Resolve(JsonNode? root, string path)
    {
        var node = root;
        foreach (var part in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(part, out var v) ? v : FindIgnoreCase(o, part),
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    private static JsonNode? FindIgnoreCase(JsonObject o, string name)
    {
        foreach (var kv in o) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>A value as text: a string as it is, a number or bool as JSON, an object or array as compact JSON, null as empty.</summary>
    public static string Show(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    public static string Pretty(JsonNode? n) => n?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
