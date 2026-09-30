using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.ToolRepair;

/// <summary>Registers <see cref="ToolRepairHook"/>. Setting: <c>toolRepair.enabled</c> (true).</summary>
[NetPiPlugin("netpi.toolrepair", Name = "Tool-call repair", Description = "Converts textual tool calls into real ones", Order = 70)]
public sealed class ToolRepairPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "toolRepair", Title = "Tool repair", Group = "Agents", Order = 40,
            Settings =
            [
                SettingInfo.Bool("toolRepair.enabled", "Run tool calls written as text", true, "e.g. <tool_call>… in an answer instead of a real call."),
            ],
        });
        var settings = context.Settings;
        context.Services.Register<IAgentHook>(new ToolRepairHook(() => settings, context.Logger));
        return Task.CompletedTask;
    }
}

/// <summary>
/// When an assistant message has no native tool calls but its text <i>is</i> tool-call markup, replace it with a copy
/// whose markup is removed and whose calls are real <see cref="ToolCallPart"/>s (runs before the nudge hook).
/// Only a standalone envelope is converted: an answer that also explains, documents or quotes the markup is left as
/// text, because "here is an example" is not an instruction to run it. The nudge hook then asks for a real call.
/// </summary>
public sealed class ToolRepairHook(Func<ISettings?> settings, ILogger? logger = null) : IAgentHook
{
    public int Order => 100;

    public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var s = settings();
        bool enabled;
        try { enabled = s?.Get("toolRepair.enabled", true) ?? true; } catch { enabled = true; }
        if (!enabled) return ValueTask.FromResult<TurnDecision?>(null);

        var repaired = ToolCallRepair.Repair(assistant, turn.Tools);
        if (repaired is null) return ValueTask.FromResult<TurnDecision?>(null);
        logger?.LogInformation("Repaired {Count} textual tool call(s) in session {Session}: {Names}", repaired.ToolCalls.Count(),
            assistant.SessionId, string.Join(", ", repaired.ToolCalls.Select(c => c.Name)));
        return ValueTask.FromResult<TurnDecision?>(TurnDecision.Replace(repaired));
    }
}

public static class ToolCallRepair
{
    /// <summary>
    /// Returns a repaired copy of <paramref name="message"/> or null when there is nothing to repair
    /// (native tool calls present, aborted/error, no markup, markup mixed with prose, or no call names an active tool).
    /// </summary>
    public static ChatMessage? Repair(ChatMessage message, IReadOnlyList<ToolDefinition> tools)
    {
        if (message.Role != MessageRole.Assistant || message.ToolCalls.Any()) return null;
        if (message.StopReason is "aborted" or "error") return null;
        var textParts = message.Parts.OfType<TextPart>().ToList();
        if (!textParts.Any(t => ToolCallTextParser.ContainsMarkup(t.Text))) return null;
        // Every text part has to be a standalone call envelope: one part of prose, a comment or a quoted example
        // makes the whole answer documentation, and documentation is not run.
        if (textParts.Any(t => !ToolCallTextParser.IsCallEnvelope(t.Text))) return null;

        var newParts = new List<MessagePart>(message.Parts.Count + 2);
        var calls = new List<ToolCallPart>();
        foreach (var part in message.Parts)
        {
            if (part is not TextPart tp || !ToolCallTextParser.ContainsMarkup(tp.Text)) { newParts.Add(part); continue; }
            var spans = new List<(int, int)>();
            foreach (var call in ToolCallTextParser.Parse(tp.Text))
            {
                var tool = ResolveTool(call.Name, tools);
                if (tool is null) continue; // unknown tool: leave its markup in the text
                calls.Add(new ToolCallPart
                {
                    Id = "call_" + Ids.Short(12),
                    Name = tool.Name,
                    Arguments = BuildArguments(call, tool).ToJsonString(NetPiJson.Options),
                });
                spans.Add((call.Start, call.End));
            }
            if (spans.Count == 0) { newParts.Add(tp); continue; }
            var cleaned = ToolCallTextParser.RemoveSpans(tp.Text, spans);
            // Provider replay data (e.g. a Responses item id) describes the original text: drop it for the edited part.
            if (!string.IsNullOrWhiteSpace(cleaned)) newParts.Add(new TextPart { Text = cleaned });
        }
        if (calls.Count == 0) return null;
        newParts.AddRange(calls);

        var meta = message.Meta?.DeepClone() as JsonObject ?? [];
        meta["repaired"] = true;
        return new ChatMessage
        {
            Id = message.Id, Seq = message.Seq, SessionId = message.SessionId, Role = message.Role, Parts = newParts,
            CreatedAt = message.CreatedAt, Provider = message.Provider, Model = message.Model, StopReason = "tool_use",
            Usage = message.Usage, DurationMs = message.DurationMs, Compacted = message.Compacted, Meta = meta,
        };
    }

    /// <summary>Exact name, case-insensitive, <c>.</c>/<c>-</c> mapped to <c>_</c>, or the last dotted segment ("functions.read").</summary>
    public static ToolDefinition? ResolveTool(string name, IReadOnlyList<ToolDefinition> tools)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var hit = tools.FirstOrDefault(t => t.Name == name)
                  ?? tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var mapped = new string(name.Select(c => c is '.' or '-' or ' ' or '/' or ':' ? '_' : c).ToArray());
        hit = tools.FirstOrDefault(t => string.Equals(t.Name, mapped, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var dot = name.LastIndexOfAny(['.', ':', '/']);
        return dot >= 0 && dot < name.Length - 1 ? ResolveTool(name[(dot + 1)..], tools) : null;
    }

    public static JsonObject BuildArguments(TextToolCall call, ToolDefinition tool)
    {
        var props = tool.Parameters["properties"] as JsonObject;
        var args = call.JsonArguments is { } j ? (JsonObject)j.DeepClone() : new JsonObject();
        foreach (var (key, value) in call.Parameters)
        {
            JsonObject? schema = null;
            var name = key;
            if (props is not null) schema = FindProperty(props, key, out name);
            args[name] = Coerce(value, schema);
        }
        return args;
    }

    private static JsonObject? FindProperty(JsonObject props, string key, out string realKey)
    {
        realKey = key;
        if (props[key] is JsonObject exact) return exact;
        foreach (var (k, v) in props)
        {
            if (v is JsonObject o && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { realKey = k; return o; }
        }
        return null;
    }

    /// <summary>Convert a textual parameter value to the JSON type its schema asks for (strings otherwise).</summary>
    public static JsonNode? Coerce(string value, JsonObject? schema)
    {
        var types = SchemaTypes(schema);
        if (types.Count == 0 || types.Contains("string")) return JsonValue.Create(value);
        var t = value.Trim();
        foreach (var type in types)
        {
            switch (type)
            {
                case "integer":
                    if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var di) && di == Math.Floor(di) && Math.Abs(di) < 9e15)
                        return JsonValue.Create((long)di);
                    break;
                case "number":
                    if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln)) return JsonValue.Create(ln);
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
                    break;
                case "boolean":
                    if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("yes", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
                    if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
                    break;
                case "array":
                    if (TryParseJson(t) is JsonArray arr) return arr;
                    break;
                case "object":
                    if (TryParseJson(t) is JsonObject obj) return obj;
                    break;
                case "null":
                    if (t is "null" or "") return null;
                    break;
            }
        }
        return JsonValue.Create(value);
    }

    private static List<string> SchemaTypes(JsonObject? schema)
    {
        var list = new List<string>();
        if (schema is null) return list;
        void AddFrom(JsonNode? typeNode)
        {
            switch (typeNode)
            {
                case JsonValue v when v.TryGetValue<string>(out var s): list.Add(s); break;
                case JsonArray a:
                    foreach (var x in a) if (x is JsonValue xv && xv.TryGetValue<string>(out var xs)) list.Add(xs);
                    break;
            }
        }
        AddFrom(schema["type"]);
        foreach (var key in new[] { "anyOf", "oneOf" })
            if (schema[key] is JsonArray alts)
                foreach (var alt in alts) if (alt is JsonObject ao) AddFrom(ao["type"]);
        if (list.Count == 0 && schema["enum"] is JsonArray en && en.Count > 0 && en.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)))
            list.Add("string");
        return list.Distinct().ToList();
    }

    private static JsonNode? TryParseJson(string s)
    {
        try { return JsonNode.Parse(s, null, new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
    }
}
