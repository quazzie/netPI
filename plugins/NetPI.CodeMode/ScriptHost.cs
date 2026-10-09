using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.CodeMode;

/// <summary>
/// What a script reaches outside the engine: the chat's tools through <see cref="INestedToolCalls"/>, the tool catalogue,
/// its store, and the live output of the codemode call. Everything crosses as JSON text. It also keeps the record of the
/// script's calls that the codemode result carries for the UI.
/// </summary>
internal sealed class ScriptHost(INestedToolCalls nested, ScriptStore store, Action<string>? output)
{
    /// <summary>The most a tool result hands a script: past it the middle is cut, start and end kept.</summary>
    public const int MaxResultChars = 1024 * 1024;
    /// <summary>How many calls the result's record lists (the rest are counted); arguments are cut to <see cref="MaxRecordedArgs"/>.</summary>
    public const int MaxRecordedCalls = 256;
    public const int MaxRecordedArgs = 2000;
    /// <summary>Added to the error of a call to a tool a script cannot call.</summary>
    public const string UnknownToolHint =
        "In a script, find callable tools with searchTools(query) or ALL_TOOLS; deferred and MCP tools are called by name, " +
        "without tool_search, mcp_search or mcp_call (which a script cannot call).";

    private readonly List<JsonObject> _calls = [];
    private int _callCount;
    private IReadOnlyList<ToolDefinition>? _tools;

    public ScriptStore Store => store;

    /// <summary>The tools a script may call, read once per script.</summary>
    private IReadOnlyList<ToolDefinition> Tools => _tools ??= nested.Tools;

    public void Progress(string line)
    {
        if (output is null) return;
        try { output(line.Length > 2000 ? line[..2000] + "…\n" : line + "\n"); } catch { }
    }

    public string ToolsJson() =>
        new JsonArray([.. Tools.Select(t => (JsonNode?)Entry(t))]).ToJsonString();

    public string SearchJson(string query, int limit) =>
        new JsonArray([.. ToolRanking.Rank(Tools, query).Take(Math.Clamp(limit, 1, 50)).Select(t => (JsonNode?)Entry(t))]).ToJsonString();

    public string DescribeJson(string name)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name) ?? Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return "";
        var o = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
        if (!string.IsNullOrWhiteSpace(tool.Help) && tool.Help != tool.Description) o["help"] = tool.Help;
        if (tool.PromptGuidelines is { Count: > 0 } g) o["guidelines"] = new JsonArray([.. g.Select(x => (JsonNode?)x)]);
        o["readOnly"] = tool.ReadOnly;
        o["parameters"] = tool.Parameters.DeepClone();
        return o.ToJsonString();
    }

    private static JsonObject Entry(ToolDefinition t) => new() { ["name"] = t.Name, ["description"] = t.Description };

    /// <summary>One call: its result as the envelope <c>callTool()</c> resolves to.</summary>
    public async Task<string> CallAsync(string name, JsonElement args, CancellationToken ct)
    {
        var argsText = args.GetRawText();
        Progress("→ " + name + " " + (argsText.Length > 160 ? argsText[..160] + "…" : argsText));
        var started = DateTime.UtcNow;
        var result = await nested.CallAsync(name, args, ct).ConfigureAwait(false);
        Record(name, argsText, result.IsError, (long)(DateTime.UtcNow - started).TotalMilliseconds);

        var content = result.Content ?? "";
        // a name the script cannot call: say where to look, and that the loaders are not needed here
        if (result.IsError && !nested.Tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            content += " " + UnknownToolHint;
        var envelope = new JsonObject { ["content"] = Cap(content), ["isError"] = result.IsError };
        if (result.Details is not null)
        {
            try { envelope["details"] = NetPiJson.ToNode(result.Details); } catch { }
        }
        if (result.Images is { Count: > 0 } images)
            envelope["images"] = new JsonArray([.. images.Select(i => (JsonNode?)new JsonObject { ["mediaType"] = i.MediaType, ["data"] = i.Data })]);
        return envelope.ToJsonString();
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxResultChars) return text;
        var half = MaxResultChars / 2;
        return text[..half] + $"\n…[{text.Length - MaxResultChars:N0} characters omitted]…\n" + text[^half..];
    }

    private void Record(string name, string args, bool isError, long ms)
    {
        lock (_calls)
        {
            _callCount++;
            if (_calls.Count >= MaxRecordedCalls) return;
            _calls.Add(new JsonObject
            {
                ["name"] = name,
                ["arguments"] = args.Length > MaxRecordedArgs ? args[..MaxRecordedArgs] + "…" : args,
                ["isError"] = isError,
                ["durationMs"] = ms,
            });
        }
    }

    /// <summary>The calls for the result's details, and how many there were in all.</summary>
    public (JsonArray Calls, int Count) Calls()
    {
        lock (_calls) return (new JsonArray([.. _calls.Select(c => (JsonNode?)c.DeepClone())]), _callCount);
    }
}

/// <summary>
/// <c>store(key, value)</c> / <c>load(key)</c>: small JSON values a chat's scripts keep across calls, in the session's
/// <c>meta.codemodeStore</c> — so a restart keeps them and a fork takes them along. A script's writes are kept only when it
/// succeeds.
/// </summary>
internal sealed class ScriptStore
{
    public const string MetaKey = "codemodeStore";
    public const int MaxValueChars = 64 * 1024;
    public const int MaxTotalChars = 256 * 1024;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public bool Dirty { get; private set; }

    public static ScriptStore Of(SessionInfo? session)
    {
        var store = new ScriptStore();
        if (session?.Meta?[MetaKey] is JsonObject o)
            foreach (var (key, value) in o)
                if (value is not null) store._values[key] = value.ToJsonString();
        return store;
    }

    public string Load(string key)
    {
        lock (_values) return _values.GetValueOrDefault(key) ?? "";
    }

    /// <summary>Set a value (<paramref name="json"/> null removes it); null when kept, else why not.</summary>
    public string? Set(string key, string? json)
    {
        if (key.Length is 0 or > 200) return "store(key, value): the key must have 1-200 characters";
        lock (_values)
        {
            if (json is null)
            {
                Dirty |= _values.Remove(key);
                return null;
            }
            if (json.Length > MaxValueChars)
                return $"store() keeps small values (at most {MaxValueChars:N0} characters of JSON) such as ids, cursors or summaries; write large data to a file with a tool";
            var total = _values.Where(p => p.Key != key).Sum(p => p.Value.Length + p.Key.Length) + json.Length + key.Length;
            if (total > MaxTotalChars) return $"the store holds at most {MaxTotalChars:N0} characters in all; remove values with store(key, undefined)";
            try { JsonNode.Parse(json); } catch (JsonException) { return "store() takes a JSON value"; }
            _values[key] = json;
            Dirty = true;
            return null;
        }
    }

    public JsonObject ToJson()
    {
        lock (_values)
            return new JsonObject(_values.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonNode.Parse(p.Value))));
    }
}
