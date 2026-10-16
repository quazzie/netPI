using System.Text.Json;

namespace NetPI;

/// <summary>
/// What a stored tool call really is. A deferred tool is called as <c>call_tool { name, arguments }</c>; the runtime unwraps
/// that for execution, but the transcript keeps the call the model made (the provider must see it) and its result under the
/// name <c>call_tool</c>. Every reader that cares which tool ran (checklists, goals, summaries, diagnostics, prompts)
/// asks here instead of comparing <see cref="ToolCallPart.Name"/>: the rule that reads the wrapper exists once.
/// </summary>
public static class ToolCalls
{
    public const string CallTool = "call_tool";

    public static bool IsCallTool(string? name) => string.Equals(name, CallTool, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tool a call really invokes and its raw JSON arguments: the inner ones for a <c>call_tool</c> with a
    /// string <c>name</c>, otherwise the call's own. Never throws.</summary>
    public static (string Name, string Arguments) Effective(ToolCallPart call)
    {
        if (TryUnwrapRaw(call.Name, call.Arguments, out var name, out var args)) return (name, args);
        return (call.Name, call.Arguments);
    }

    public static string EffectiveName(ToolCallPart call) => Effective(call).Name;

    /// <summary>The inner call (same id) when <paramref name="call"/> is a usable <c>call_tool</c>, else null. Names are
    /// not checked against any tool list: the runtime does that when it executes.</summary>
    public static ToolCallPart? Unwrap(ToolCallPart call) =>
        TryUnwrapRaw(call.Name, call.Arguments, out var name, out var args)
            ? new ToolCallPart { Id = call.Id, Name = name, Arguments = args } : null;

    /// <summary>Parses <c>call_tool</c> arguments (an object, or a JSON string holding one): the trimmed inner name and the
    /// inner arguments as raw JSON (<c>{}</c> when absent; a string value is the JSON text itself).</summary>
    public static bool TryUnwrapRaw(string? callName, string? arguments, out string name, out string args)
    {
        name = ""; args = "{}";
        if (!IsCallTool(callName)) return false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return TryResolve(ToolArgs.Unwrap(doc.RootElement), out name, out args);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The same for already parsed <c>call_tool</c> arguments.</summary>
    public static bool TryResolve(JsonElement callArgs, out string name, out string args)
    {
        name = ""; args = "{}";
        if (callArgs.ValueKind != JsonValueKind.Object || !callArgs.TryGetProperty("name", out var n)
            || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())) return false;
        name = n.GetString()!.Trim();
        if (callArgs.TryGetProperty("arguments", out var a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            args = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText();
        return true;
    }

    /// <summary>Stored calls by id, for naming results (<see cref="EffectiveName(ToolResultPart, IReadOnlyDictionary{string, ToolCallPart})"/>).</summary>
    public static Dictionary<string, ToolCallPart> Index(IEnumerable<ChatMessage> messages)
    {
        var map = new Dictionary<string, ToolCallPart>(StringComparer.Ordinal);
        foreach (var m in messages)
            foreach (var c in m.ToolCalls)
                if (c.Id.Length > 0) map[c.Id] = c;
        return map;
    }

    /// <summary>The tool a stored result belongs to: its call's effective name when the call is known, else the result's own.</summary>
    public static string EffectiveName(ToolResultPart result, IReadOnlyDictionary<string, ToolCallPart> calls) =>
        !IsCallTool(result.Name) ? result.Name
        : calls.TryGetValue(result.CallId, out var c) ? EffectiveName(c) : result.Name;

    /// <summary>True when the result is the result of <paramref name="tool"/> (called directly or through <c>call_tool</c>).</summary>
    public static bool IsResultOf(ToolResultPart result, string tool, IReadOnlyDictionary<string, ToolCallPart> calls) =>
        string.Equals(EffectiveName(result, calls), tool, StringComparison.Ordinal);
}
