using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// <c>call_tool { name, arguments }</c>: runs a deferred tool that <c>tool_search</c> showed the schema of. The request's tools
/// never change when a tool is found (a changed tools block means a full prefill of the conversation), so a found tool is not
/// one the backend knows; this one always is, with the same shape on every backend. The batch runner unwraps the call
/// before anything else sees it (<see cref="TryUnwrap"/>): the policy hooks, the workspace guard, the events and the result
/// all belong to the inner tool, so the dispatcher adds no way around them.
/// <para>A missing name is inferred from the arguments (<see cref="Infer"/>): every required property of a tool's schema among
/// them, every key one of its properties, and exactly one such tool. Naming <c>tool_search</c> runs the search with those
/// arguments; naming <c>call_tool</c> without an envelope inside keeps its error.</para>
/// <para>Deferred-tool infrastructure like <c>tool_search</c>: declared exactly when there is something deferred to reach.</para>
/// </summary>
internal sealed class CallTool : IAgentTool, IDeferredToolInfrastructure
{
    public const string ToolName = "call_tool";

    /// <summary>How many nested envelopes <see cref="TryUnwrap"/> follows. Local models repeat the envelope (see the loop),
    /// and one repeat is enough for the shape to reach a third level; deeper than this is not the same habit.</summary>
    public const int MaxNesting = 3;

    public bool Supports(ToolDefinition target) => true;

    public ToolDefinition Definition { get; } = new()
    {
        Name = ToolName, Label = "Call a tool", Category = "general", SummaryArg = "name",
        Description = "Run a tool that tool_search found, by name, with the arguments its schema shows. The \"arguments\" "
            + "object is that tool's own arguments — never another {\"name\": …, \"arguments\": …}. When the name is missing it "
            + "is inferred from the arguments (exactly one tool must take them). "
            + "Pass {\"help\": true} as the arguments to see a tool's manual.",
        PromptGuidelines =
        [
            "A tool found with tool_search is run with call_tool { name, arguments }.",
            "call_tool's arguments are the tool's own arguments, not another { name, arguments }.",
        ],
        Parameters = JsonNode.Parse("""
            {"type":"object","properties":{
              "name":{"type":"string","description":"The tool's name, as tool_search showed it."},
              "arguments":{"type":"object","description":"The tool's arguments, as its schema describes them."}},
             "required":["name"],"additionalProperties":false}
            """)!.AsObject(),
    };

    /// <summary>Reached only when the call could not be unwrapped: says why.</summary>
    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var tools = context.EligibleTools?.Invoke() ?? [];
        var (_, error) = ResolveInner(args.GetRawText(), tools);
        return Task.FromResult(ToolResult.Error(error ?? "call_tool could not run the tool."));
    }

    /// <summary>The inner call (the tool's own name and arguments, the outer call's id), or null when this is not a usable
    /// <c>call_tool</c> (the tool then runs and explains).</summary>
    public static ToolCallPart? TryUnwrap(ToolCallPart call, IReadOnlyList<IAgentTool> tools)
    {
        if (!ToolCalls.IsCallTool(call.Name)) return null;
        var (inner, _) = ResolveInner(call.Arguments, tools);
        return inner is null ? null : new ToolCallPart { Id = call.Id, Name = inner.Name, Arguments = inner.Arguments };
    }

    /// <summary>
    /// The inner call a <c>call_tool</c>'s arguments are, and the answer when they are not a usable call: the named envelope,
    /// the envelope inside an envelope (the infrastructure's two are never a call's arguments: with no envelope inside,
    /// <c>tool_search</c> is the call's tool and <c>call_tool</c> keeps its error), and the nameless envelope
    /// <see cref="Infer"/> resolves from its arguments.
    /// </summary>
    private static (ToolCallPart? Inner, string? Error) ResolveInner(string? arguments, IReadOnlyList<IAgentTool> tools)
    {
        if (Envelope(arguments) is not { } outer) return (null, NeedsShape);
        ToolCallPart? current;
        string? error;
        if (outer.Name is null)
            (current, error) = Infer(outer.Args, tools);
        else if (ToolExposure.IsInfrastructure(outer.Name) && Envelope(outer.Args) is { Name: not null } inner)
            (current, error) = Resolve(inner.Name, inner.Args, tools);
        else (current, error) = Resolve(outer.Name, outer.Args, tools);
        for (var depth = 0; current is not null && depth < MaxNesting; depth++)
        {
            if (Envelope(current.Arguments) is not { Name: not null } nested) break;
            var deeper = Resolve(nested.Name, nested.Args, tools).Inner;
            // The envelope names the tool it is already inside: the model wrote the envelope twice. A tool that really takes
            // a name and an arguments object (an rpc-style proxy) names something else, and its arguments are left alone.
            if (deeper is null || !string.Equals(deeper.Name, current.Name, StringComparison.OrdinalIgnoreCase)) break;
            current = deeper;
        }
        if (current is not null) return (current, null);
        return (null, error ?? NeedsShape);
    }

    private const string NeedsShape = "call_tool needs {\"name\": \"<tool>\", \"arguments\": {...}}.";

    /// <summary>A call_tool envelope: its tool name and raw arguments. Reads the shapes the models produce — the plain
    /// <c>{name, arguments}</c>, the one where the envelope sits one level deep with no <c>name</c> of its own
    /// (<c>{"arguments": {"name": "&lt;tool&gt;", "arguments": {…}}}</c>), and the nameless one
    /// (<c>{"arguments": {…}}</c>, a <c>null</c> name: <see cref="Infer"/> names the tool from the arguments).</summary>
    private static Env? Envelope(string? arguments)
    {
        if (ToolCalls.TryUnwrapRaw(ToolCalls.CallTool, arguments, out var name, out var args)) return new Env(name, args);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            var top = ToolArgs.Unwrap(doc.RootElement);
            if (top.ValueKind == JsonValueKind.Object && !top.TryGetProperty("name", out _)
                && top.TryGetProperty("arguments", out var nested) && nested.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                var inner = ToolArgs.Unwrap(nested);
                if (inner.ValueKind != JsonValueKind.Object) return null;
                if (ToolCalls.TryResolve(inner, out var deepName, out var deepArgs)) return new Env(deepName, deepArgs);
                return new Env(null, nested.ValueKind == JsonValueKind.String ? nested.GetString() ?? "{}" : nested.GetRawText());
            }
        }
        catch (JsonException) { }
        return null;
    }

    private sealed record Env(string? Name, string Args);

    /// <summary>
    /// The tool a nameless envelope's arguments belong to: every required property of a tool's schema is among them, and every
    /// key is one of the schema's properties (names compared the lenient way, a <c>help</c> key ignored). Exactly one such
    /// tool is the call; several are named in the answer, and with none, <c>tool_search</c> finds the tool. An empty
    /// <c>arguments</c> never infers.
    /// </summary>
    private static (ToolCallPart? Inner, string? Error) Infer(string raw, IReadOnlyList<IAgentTool> tools)
    {
        HashSet<string> keys;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var args = ToolArgs.Unwrap(doc.RootElement);
            if (args.ValueKind != JsonValueKind.Object) return (null, NeedsShape);
            keys = args.EnumerateObject()
                .Where(p => !string.Equals(p.Name, "help", StringComparison.OrdinalIgnoreCase))
                .Select(p => Norm(p.Name)).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException) { return (null, NeedsShape); }
        if (keys.Count == 0) return (null, "No tool takes these arguments; find it with tool_search.");
        var matches = new List<string>();
        foreach (var tool in tools)
        {
            if (tool is IDeferredToolInfrastructure) continue;   // tool_search and call_tool are never the inference
            if (Fits(tool.Definition.Parameters, keys)) matches.Add(tool.Definition.Name);
        }
        if (matches.Count == 1) return (new ToolCallPart { Name = matches[0], Arguments = raw }, null);
        if (matches.Count == 0) return (null, "No tool takes these arguments; find it with tool_search.");
        return (null, $"call_tool needs \"name\". Tools that take these arguments: {string.Join(", ", matches.OrderBy(n => n, StringComparer.Ordinal))}.");
    }

    /// <summary>Every required property among <paramref name="keys"/>, and every key one of the schema's properties.</summary>
    private static bool Fits(JsonObject parameters, HashSet<string> keys)
    {
        var properties = (parameters["properties"] as JsonObject) is { } props
            ? props.Select(p => Norm(p.Key)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var required = (parameters["required"] as JsonArray) is { } req
            ? req.Where(n => n is JsonValue v && v.TryGetValue<string>(out _)).Select(n => Norm(n!.GetValue<string>())).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return required.All(keys.Contains) && keys.All(properties.Contains);
    }

    /// <summary>Lenient names, the way <see cref="ToolArgs"/> compares them: case, '_' and '-' ignored.</summary>
    private static string Norm(string name)
    {
        Span<char> buf = name.Length <= 256 ? stackalloc char[256] : new char[name.Length];
        var n = 0;
        foreach (var c in name)
            if (c != '_' && c != '-' && c != ' ') buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    private static (ToolCallPart? Inner, string? Error) Resolve(string name, string raw, IReadOnlyList<IAgentTool> tools)
    {
        var tool = tools.FirstOrDefault(t => t.Definition.Name == name)
                   ?? tools.FirstOrDefault(t => string.Equals(t.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return (null, $"Unknown tool '{name}': it is not a tool this chat can call. Find it with tool_search.");
        if (tool is CallTool)
            return (null, $"'{name}' is a tool of its own: call it directly, not through call_tool.");
        // tool_search is infrastructure, but a call naming it is the search itself: it runs with those arguments
        return (new ToolCallPart { Name = tool.Definition.Name, Arguments = raw }, null);
    }

    /// <summary>
    /// The stored call carries the inner name, so every reader (<see cref="ToolCalls"/>, the web UI's <c>effectiveCall</c>)
    /// sees the tool that ran: a call whose arguments already name the tool is kept as the model wrote it; the rest — the
    /// nameless envelope, an inferred name — is stored in the one shape the readers parse.
    /// </summary>
    internal static bool StoreName(ToolCallPart stored, ToolCallPart inner)
    {
        if (ToolCalls.TryUnwrapRaw(ToolCalls.CallTool, stored.Arguments, out _, out _)) return false;
        var arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(inner.Arguments) ? "{}" : inner.Arguments) ?? new JsonObject();
        stored.Arguments = new JsonObject { ["name"] = inner.Name, ["arguments"] = arguments }.ToJsonString();
        return true;
    }
}
