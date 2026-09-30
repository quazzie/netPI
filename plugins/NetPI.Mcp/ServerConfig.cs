using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Mcp;

internal sealed record ServerConfig
{
    public required string Id { get; init; }
    public bool Enabled { get; init; } = true;
    public string Transport { get; init; } = "stdio";
    public string Command { get; init; } = "";
    public string[] Args { get; init; } = [];
    public string Cwd { get; init; } = "";
    public string Url { get; init; } = "";
    // Values are environment variable names, never literal credentials.
    public Dictionary<string, string> Env { get; init; } = [];
    public Dictionary<string, string> HeaderEnv { get; init; } = [];
    public string[]? Tools { get; init; }
    public string[] Pinned { get; init; } = [];
    public string[] ReadOnly { get; init; } = [];
    public string[] Synonyms { get; init; } = [];
    public int ConnectTimeoutMs { get; init; } = 5000;
    public int CallTimeoutMs { get; init; } = 60000;

    public static ServerConfig Parse(string id, JsonObject value, string defaultCwd)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,48}$")) throw new RpcException("bad_request", "Server id must be 1–48 letters, digits, underscores or hyphens.");
        var c = new ServerConfig
        {
            Id = id, Enabled = Bool(value, "enabled", true), Transport = Str(value, "transport", "stdio"),
            Command = Str(value, "command"), Args = Strings(value, "args") ?? [],
            Cwd = Str(value, "cwd", defaultCwd), Url = Str(value, "url"),
            Env = Map(value, "env"), HeaderEnv = Map(value, "headerEnv"),
            Tools = Strings(value, "tools"), Pinned = Strings(value, "pinned") ?? [],
            ReadOnly = Strings(value, "readOnly") ?? [], Synonyms = Strings(value, "synonyms") ?? [],
            ConnectTimeoutMs = Int(value, "connectTimeoutMs", 5000, 100, 30000),
            CallTimeoutMs = Int(value, "callTimeoutMs", 60000, 100, 300000),
        };
        if (c.Transport is not ("stdio" or "http")) throw new RpcException("bad_request", "Transport must be stdio or http.");
        if (c.Transport == "stdio" && (string.IsNullOrWhiteSpace(c.Command) || !Path.IsPathFullyQualified(c.Cwd)))
            throw new RpcException("bad_request", "A stdio server requires a command and an absolute working directory.");
        if (c.Transport == "http" && (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new RpcException("bad_request", "An HTTP server requires an http/https URL without embedded credentials.");
        foreach (var (name, source) in c.Env)
            if (name.Length > 128 || name.Contains('=') || name.IndexOf('\0') >= 0 || !ValidEnv(source))
                throw new RpcException("bad_request", "Environment entries must map variable names to source environment variable names.");
        foreach (var (name, source) in c.HeaderEnv)
            if (!Regex.IsMatch(name, "^[!#$%&'*+.^_\x60|~a-zA-Z0-9-]+$") || !ValidEnv(source)
                || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new RpcException("bad_request", "Invalid or reserved HTTP header. Values must name source environment variables.");
        return c;
    }
    public JsonObject Json() => new()
    {
        ["enabled"] = Enabled, ["transport"] = Transport, ["command"] = Command,
        ["args"] = Array(Args), ["cwd"] = Cwd, ["url"] = Url,
        ["env"] = Object(Env), ["headerEnv"] = Object(HeaderEnv),
        ["tools"] = Tools is null ? null : Array(Tools), ["pinned"] = Array(Pinned), ["readOnly"] = Array(ReadOnly),
        ["synonyms"] = Array(Synonyms), ["connectTimeoutMs"] = ConnectTimeoutMs, ["callTimeoutMs"] = CallTimeoutMs,
    };
    public bool Exposes(string name) => Tools is null || Tools.Contains(name, StringComparer.Ordinal);
    public string Secret(string source) => Environment.GetEnvironmentVariable(source) ?? throw new InvalidOperationException($"Environment variable '{source}' is not set.");
    public string Redact(string text)
    {
        foreach (var source in Env.Values.Concat(HeaderEnv.Values))
            if (Environment.GetEnvironmentVariable(source) is { Length: > 0 } secret) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text.Length > 500 ? text[..500] + "…" : text;
    }
    private static bool ValidEnv(string source) => Regex.IsMatch(source, "^[a-zA-Z_][a-zA-Z0-9_]{0,127}$");
    internal static string Str(JsonObject o, string key, string fallback = "") =>
        o[key] is null ? fallback : o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new RpcException("bad_request", key + " must be a string.");
    private static bool Bool(JsonObject o, string key, bool fallback) => o[key] is null ? fallback : o[key]!.GetValue<bool>();
    private static int Int(JsonObject o, string key, int fallback, int min, int max)
    {
        var n = o[key] is null ? fallback : o[key]!.GetValue<int>();
        return n >= min && n <= max ? n : throw new RpcException("bad_request", $"{key} must be between {min} and {max}.");
    }
    private static string[]? Strings(JsonObject o, string key)
    {
        if (o[key] is null) return null;
        if (o[key] is not JsonArray a || a.Count > 10000) throw new RpcException("bad_request", key + " must be a bounded string array.");
        return a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 16384 ? s :
            throw new RpcException("bad_request", key + " must contain strings.")).ToArray();
    }
    private static Dictionary<string, string> Map(JsonObject o, string key)
    {
        if (o[key] is null) return [];
        if (o[key] is not JsonObject m || m.Count > 64) throw new RpcException("bad_request", key + " must be an object with at most 64 entries.");
        return m.ToDictionary(p => p.Key, p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s :
            throw new RpcException("bad_request", key + " values must name environment variables."));
    }
    internal static JsonArray Array(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static JsonObject Object(Dictionary<string, string> values) => new(values.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
}
