using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// <c>call_tool { name, arguments }</c>: runs a deferred tool that <c>tool_search</c> showed the schema of. The request's tools
/// never change when a tool is found (a changed tools block means a full prefill of the conversation), so a found tool is not
/// one the backend knows; this one always is, with the same shape on every backend. The batch runner unwraps the call
/// before anything else sees it (<see cref="TryUnwrap"/>): the policy hooks, the workspace guard, the events and the result
/// all belong to the inner tool, so the dispatcher adds no way around them.
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
            + "object is that tool's own arguments — never another {\"name\": …, \"arguments\": …}. "
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
        var (_, error) = Resolve(args, tools);
        return Task.FromResult(ToolResult.Error(error ?? "call_tool could not run the tool."));
    }

    /// <summary>The inner call (the tool's own name and arguments, the outer call's id), or null when this is not a usable
    /// <c>call_tool</c> (the tool then runs and explains).</summary>
    public static ToolCallPart? TryUnwrap(ToolCallPart call, IReadOnlyList<IAgentTool> tools)
    {
        if (!ToolCalls.IsCallTool(call.Name) || Envelope(call.Arguments) is not { } outer) return null;
        var current = Resolve(outer.Name, outer.Args, tools).Inner;
        // call_tool { name: "call_tool" | "tool_search", arguments: {…} }: the envelope inside the envelope. These two are
        // never a call's arguments, so the inner one is what the model meant ("call it directly", applied here).
        if (current is null && ToolExposure.IsInfrastructure(outer.Name) && Envelope(outer.Args) is { } inner)
            current = Resolve(inner.Name, inner.Args, tools).Inner;
        for (var depth = 0; current is not null && depth < MaxNesting; depth++)
        {
            if (Envelope(current.Arguments) is not { } nested) break;
            var deeper = Resolve(nested.Name, nested.Args, tools).Inner;
            // The envelope names the tool it is already inside: the model wrote the envelope twice. A tool that really takes
            // a name and an arguments object (an rpc-style proxy) names something else, and its arguments are left alone.
            if (deeper is null || !string.Equals(deeper.Name, current.Name, StringComparison.OrdinalIgnoreCase)) break;
            current = deeper;
        }
        return current is null ? null : new ToolCallPart { Id = call.Id, Name = current.Name, Arguments = current.Arguments };
    }

    /// <summary>A call_tool envelope: its tool name and raw arguments. Reads both shapes the models produce — the plain
    /// <c>{name, arguments}</c>, and the one where the envelope sits one level deep with no <c>name</c> of its own
    /// (<c>{"arguments": {"name": "&lt;tool&gt;", "arguments": {…}}}</c>).</summary>
    private static Env? Envelope(string? arguments)
    {
        if (ToolCalls.TryUnwrapRaw(ToolCalls.CallTool, arguments, out var name, out var args)) return new Env(name, args);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            var top = ToolArgs.Unwrap(doc.RootElement);
            if (top.ValueKind == JsonValueKind.Object && !top.TryGetProperty("name", out _)
                && top.TryGetProperty("arguments", out var nested) && nested.ValueKind == JsonValueKind.Object
                && ToolCalls.TryResolve(nested, out var deepName, out var deepArgs)) return new Env(deepName, deepArgs);
        }
        catch (JsonException) { }
        return null;
    }

    private sealed record Env(string Name, string Args);

    private static (ToolCallPart? Inner, string? Error) Resolve(JsonElement args, IReadOnlyList<IAgentTool> tools) =>
        ToolCalls.TryResolve(ToolArgs.Unwrap(args), out var name, out var raw)
            ? Resolve(name, raw, tools)
            : (null, "call_tool needs {\"name\": \"<tool>\", \"arguments\": {...}}.");

    private static (ToolCallPart? Inner, string? Error) Resolve(string name, string raw, IReadOnlyList<IAgentTool> tools)
    {
        var tool = tools.FirstOrDefault(t => t.Definition.Name == name)
                   ?? tools.FirstOrDefault(t => string.Equals(t.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return (null, $"Unknown tool '{name}': it is not a tool this chat can call. Find it with tool_search.");
        if (tool is IDeferredToolInfrastructure || tool is CallTool)
            return (null, $"'{name}' is a tool of its own: call it directly, not through call_tool.");
        return (new ToolCallPart { Name = tool.Definition.Name, Arguments = raw }, null);
    }
}
