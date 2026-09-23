using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Files;

/// <summary>
/// Lenient access to tool arguments. Names are matched ignoring case, '_' and '-'
/// (so <c>file_path</c>, <c>filePath</c> and <c>FilePath</c> are the same), numbers may arrive as strings,
/// booleans as "true"/"yes"/1, and a double-encoded JSON string object is unwrapped.
/// </summary>
public readonly struct ToolArgs
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
public static class Schema
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
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)e).ToArray());
        return o;
    }

    public static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
    public static JsonObject Array(string description, JsonObject items) => new() { ["type"] = "array", ["description"] = description, ["items"] = items };
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

/// <summary>Path formatting helpers shared by the tools.</summary>
public static class PathDisplay
{
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Path relative to <paramref name="baseDir"/> with forward slashes, or the absolute path when outside of it.</summary>
    public static string Relative(string baseDir, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(baseDir, fullPath);
            if (rel == ".") return ".";
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return fullPath.Replace('\\', '/');
            return rel.Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(',', '.'),
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB".Replace(',', '.'),
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB".Replace(',', '.'),
    };

    /// <summary>A short "did you mean" hint for a missing path (same directory, similar names).</summary>
    public static string? Suggest(string missingPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(missingPath);
            var name = Path.GetFileName(missingPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            if (!Directory.Exists(dir))
                return $"The directory {dir} does not exist either.";
            var candidates = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Take(5000)
                .Select(f => (f.Name, Score: Similarity(name, f.Name)))
                .Where(x => x.Score >= 0.5)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .Select(x => x.Name)
                .ToList();
            return candidates.Count > 0 ? "Did you mean: " + string.Join(", ", candidates.Select(c => Path.Combine(dir, c))) + "?" : null;
        }
        catch
        {
            return null;
        }
    }

    private static double Similarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (Path.GetFileNameWithoutExtension(a) == Path.GetFileNameWithoutExtension(b)) return 0.9;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length > 200 || b.Length > 200) return Math.Max(a.Length, b.Length);
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
