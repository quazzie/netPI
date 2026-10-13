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

    public bool Supports(ToolDefinition target) => true;

    public ToolDefinition Definition { get; } = new()
    {
        Name = ToolName, Label = "Call a tool", Category = "general", SummaryArg = "name",
        Description = "Run a tool that tool_search found, by name, with the arguments its schema shows. "
            + "Pass {\"help\": true} as the arguments to see a tool's manual.",
        PromptGuidelines = ["A tool found with tool_search is run with call_tool { name, arguments }."],
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
        if (!string.Equals(call.Name, ToolName, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            var (inner, _) = Resolve(ToolArgs.Unwrap(doc.RootElement.Clone()), tools);
            return inner is null ? null : new ToolCallPart { Id = call.Id, Name = inner.Name, Arguments = inner.Arguments };
        }
        catch (JsonException) { return null; }
    }

    private static (ToolCallPart? Inner, string? Error) Resolve(JsonElement args, IReadOnlyList<IAgentTool> tools)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(n.GetString()))
            return (null, "call_tool needs {\"name\": \"<tool>\", \"arguments\": {...}}.");
        var name = n.GetString()!.Trim();
        var tool = tools.FirstOrDefault(t => t.Definition.Name == name)
                   ?? tools.FirstOrDefault(t => string.Equals(t.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return (null, $"Unknown tool '{name}': it is not a tool this chat can call. Find it with tool_search.");
        if (tool is IDeferredToolInfrastructure || tool is CallTool)
            return (null, $"'{name}' is a tool of its own: call it directly, not through call_tool.");
        var raw = "{}";
        if (args.TryGetProperty("arguments", out var a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            raw = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText();
        return (new ToolCallPart { Name = tool.Definition.Name, Arguments = raw }, null);
    }
}
