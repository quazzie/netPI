using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// The call that arrives at a tool carrying <c>call_tool</c>'s own envelope as its arguments:
/// <c>{"name": "x", "arguments": {…}}</c> where the tool expected <c>{"query": "…"}</c>. <see cref="CallTool.TryUnwrap"/>
/// unwraps what it can prove (the envelope that repeats its own tool); this answers the rest, in the shape that fixes it:
/// a tool's bare "needs a query." cannot be reconciled with arguments that do hold a query, one bracket deeper, so the
/// model repeats the call unchanged (seen on the local models, 2026-10-21 in a webhook chat's six lost web searches).
/// </summary>
internal static class ToolEnvelope
{
    /// <summary>An error that says what to do, when these arguments are a <c>call_tool</c> envelope and the tool does not
    /// take a name and arguments of its own. Null when the arguments are the tool's own.</summary>
    public static string? Check(ToolDefinition def, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !IsEnvelope(args)) return null;
        if (TakesPair(def.Parameters)) return null;   // an rpc-style proxy's own name and arguments: not the envelope
        var shape = Shape(def.Parameters);
        return def.Name + " was reached with call_tool's own envelope as its arguments ({\"name\": …, \"arguments\": {…}}). "
            + "The object under call_tool's \"arguments\" is what " + def.Name + " takes, so the call is "
            + "call_tool {\"name\": \"" + def.Name + "\", \"arguments\": " + shape + "} — one envelope, not two.";
    }

    /// <summary>Only a name and an arguments object, the arguments an object: the envelope, and not a tool's own parameter
    /// pair (a proxy tool that takes a tool name and its arguments declares both, and is never touched).</summary>
    private static bool IsEnvelope(JsonElement args)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in args.EnumerateObject())
        {
            var key = Key(p.Name);
            if (key is not ("name" or "arguments")) return false;
            names.Add(key);
        }
        if (names.Count != 2) return false;
        return Prop(args, "name").ValueKind == JsonValueKind.String
               && Prop(args, "arguments").ValueKind == JsonValueKind.Object;
    }

    private static JsonElement Prop(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (Key(p.Name) == name) return p.Value;
        return default;
    }

    private static string Key(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    /// <summary>The tool declares a name and an arguments parameter of its own (call_tool, an rpc proxy): for it the pair is
    /// the call, not a wrapper that came along.</summary>
    private static bool TakesPair(JsonObject? parameters)
    {
        if (parameters?["properties"] is not JsonObject props) return false;
        var keys = props.Select(p => Key(p.Key)).ToHashSet();
        return keys.Contains("name") && keys.Contains("arguments");
    }

    /// <summary>The tool's own arguments as a call template: its required parameters first, then the rest.</summary>
    private static string Shape(JsonObject? parameters)
    {
        if (parameters?["properties"] is not JsonObject props) return "{…}";
        var required = parameters["required"] is JsonArray req
            ? req.Select(v => v is JsonValue s && s.TryGetValue<string>(out var t) ? t : "").Where(s => s.Length > 0).ToList()
            : [];
        var keys = required.Where(props.ContainsKey).Concat(props.Select(p => p.Key).Where(k => !required.Contains(k))).ToList();
        if (keys.Count == 0) return "{…}";
        return "{" + string.Join(", ", keys.Select(k => "\"" + k + "\": …")) + "}";
    }
}
