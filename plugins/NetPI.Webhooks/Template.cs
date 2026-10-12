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

    public static string Expand(string template, JsonNode? payload, DateTimeOffset now) =>
        Placeholder().Replace(template, m =>
        {
            var path = m.Groups[1].Value;
            if (path == "payload") return Clip(Pretty(payload), MaxPayloadChars);
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

    // ---------------------------------------------------------------- match conditions

    /// <summary>
    /// Every condition must hold. A condition is <c>{ path, equals? | in? | notIn? | matches? | exists? }</c>; comparisons are on the
    /// value's text, case-insensitive. Returns null when the payload matches, else a sentence saying which condition failed.
    /// </summary>
    public static string? FailedCondition(JsonArray? conditions, JsonNode? payload)
    {
        if (conditions is null) return null;
        foreach (var c in conditions.OfType<JsonObject>())
        {
            var path = c["path"]?.GetValue<string>() ?? "";
            var node = Resolve(payload, path);
            var value = Show(node);
            var present = node is not null;
            if (c["exists"] is JsonValue ex && ex.TryGetValue<bool>(out var want) && present != want)
                return want ? $"{path} is missing" : $"{path} is present";
            if (c["equals"] is { } eq && !string.Equals(Show(eq), value, StringComparison.OrdinalIgnoreCase))
                return $"{path} is “{Clip(value, 60)}”, not “{Show(eq)}”";
            if (c["in"] is JsonArray inn && !inn.Any(x => string.Equals(Show(x), value, StringComparison.OrdinalIgnoreCase)))
                return $"{path} is “{Clip(value, 60)}”, not one of {string.Join("/", inn.Select(Show))}";
            if (c["notIn"] is JsonArray notIn && notIn.Any(x => string.Equals(Show(x), value, StringComparison.OrdinalIgnoreCase)))
                return $"{path} is “{Clip(value, 60)}”, which is excluded";
            if (c["matches"] is JsonValue rx && rx.TryGetValue<string>(out var pattern))
            {
                try
                {
                    if (!Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                        return $"{path} does not match {pattern}";
                }
                catch (ArgumentException) { return $"the pattern {pattern} is not a valid regular expression"; }
                catch (RegexMatchTimeoutException) { return $"the pattern {pattern} took too long"; }
            }
        }
        return null;
    }

    /// <summary>Check a condition list when a webhook is saved, so a typo is told then and not at 3 a.m.</summary>
    public static JsonArray ValidateConditions(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray list) throw new RpcException("bad_request", "match must be an array of conditions");
        var clean = new JsonArray();
        foreach (var item in list)
        {
            if (item is not JsonObject c || c["path"] is not JsonValue p || !p.TryGetValue<string>(out var path) || path.Trim().Length == 0)
                throw new RpcException("bad_request", "every match condition needs a path");
            var keys = c.Select(kv => kv.Key).Where(k => k != "path").ToList();
            if (keys.Count == 0 || keys.Any(k => k is not ("equals" or "in" or "notIn" or "matches" or "exists")))
                throw new RpcException("bad_request", $"condition on {path}: use one or more of equals, in, notIn, matches, exists");
            if (c["matches"] is JsonValue m && m.TryGetValue<string>(out var pattern))
            {
                try { _ = new Regex(pattern); }
                catch (ArgumentException ex) { throw new RpcException("bad_request", $"condition on {path}: {ex.Message}"); }
            }
            clean.Add(c.DeepClone());
        }
        return clean;
    }
}
