using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// <c>tool_search</c>: finds deferred tools (any plugin's, an MCP server's included) by what they do and shows the best
/// matches with their schemas; the model runs one with <c>call_tool</c> (<see cref="CallTool"/>). Nothing is loaded and the
/// request's tools never change: they sit ahead of the conversation, so a changed tools block means the backend prefills the
/// whole conversation again. The idea is pi 1.0's tool_search (idea-8qlb5s).
/// <para>Its description names no tool, so it stays the same while tools come and go (MCP servers connecting) and the
/// request prefix keeps. It is deferred-tool infrastructure: a chat that has no deferred tool is not sent it.</para>
/// </summary>
internal sealed class ToolSearchTool(IPluginContext ctx, AgentRuntime runtime) : IAgentTool, IDeferredToolInfrastructure
{
    public const string ToolName = "tool_search";
    public const int DefaultLimit = 5;
    public const int MaxLimit = 10;

    public bool Supports(ToolDefinition target) => true;

    public ToolDefinition Definition { get; } = new()
    {
        Name = ToolName, Label = "Find tools", Category = "general", ReadOnly = true, SummaryArg = "query",
        Description = "Find tools that are not in your tool list (deferred tools, such as an MCP server's) by what they do. "
            + "The result has each match's schema; run a match with call_tool.",
        PromptGuidelines = ["When none of your tools fits a task, search with tool_search: it shows matching tools with their schemas, and call_tool runs them."],
        Parameters = JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "query":{"type":"string","description":"What the tool should do, in a few words, or a tool name."},
              "limit":{"type":"integer","minimum":1,"maximum":{{{MaxLimit}}},"description":"Most tools to load (default {{{DefaultLimit}}})."}},
             "required":["query"],"additionalProperties":false}
            """)!.AsObject(),
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var query = a.Str("query")?.Trim() ?? "";
        if (query.Length is 0 or > 500) return Task.FromResult(ToolResult.Error("query must contain 1–500 characters."));
        var limit = Math.Clamp(a.Int("limit") ?? DefaultLimit, 1, MaxLimit);

        var session = ctx.Sessions.Require(context.SessionId);
        var eligible = context.EligibleTools?.Invoke() ?? runtime.ToolsFor(runtime.GetBySession(context.SessionId), session);
        var exposure = ToolExposure.For(ctx.Settings, runtime.GetBySession(context.SessionId), session);
        // what a search can offer: deferred tools this chat may call
        var candidates = eligible.Where(t => exposure.IsDeferred(t.Definition)).ToList();
        if (candidates.Count == 0)
            return Task.FromResult(ToolResult.Ok("No tools to search: every tool you can use is already in your tool list.", Details([])));

        // only real matches: a tool that shares a single word with the query does not ride along
        var ranked = ToolRanking.Rank(candidates.Select(t => t.Definition).ToList(), query, MinRelative).Take(limit).ToList();
        if (ranked.Count == 0)
            return Task.FromResult(ToolResult.Ok($"No tool matches \"{query}\" among {candidates.Count} searchable tools. Try other words for what the tool does.", Details([])));
        return Task.FromResult(ToolResult.Ok(Inline(ranked), Details(ranked.Select(d => d.Name))));
    }

    /// <summary>Weakest match kept, as a share of the best score.</summary>
    public const double MinRelative = 0.4;
    /// <summary>Most characters of schemas in one result (the result cap is 20000; past it the tail would be cut off).</summary>
    private const int SchemaBudget = 15000;

    /// <summary>The matches with their schemas, for <c>call_tool</c>; what does not fit the budget is named only.</summary>
    private static string Inline(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.Append("Found ").Append(tools.Count).Append(tools.Count == 1 ? " tool" : " tools")
          .Append(". Run one with call_tool { \"name\": ..., \"arguments\": { ... } }; the arguments follow its schema, and are")
          .Append(" that tool's own arguments — not another { \"name\": ..., \"arguments\": ... }.");
        var left = new List<ToolDefinition>();
        var shown = 0;
        foreach (var d in tools)
        {
            var schema = (d.Parameters ?? new JsonObject()).ToJsonString();
            var block = new StringBuilder("\n\n## ").Append(d.Name).Append('\n').Append(d.Description.Trim()).Append("\nschema: ").Append(schema);
            foreach (var g in d.PromptGuidelines ?? []) block.Append("\n- ").Append(g);
            if (shown > 0 && sb.Length + block.Length > SchemaBudget) { left.Add(d); continue; }
            shown++;
            sb.Append(block);
        }
        if (left.Count > 0)
            sb.Append("\n\nToo long to show here (search for them by name, one at a time):").Append(Lines(left));
        return sb.ToString();
    }

    private static string Lines(IEnumerable<ToolDefinition> tools) =>
        string.Concat(tools.Select(d => "\n- " + d.Name + ": " + FirstLine(d.Description)));

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static JsonObject Details(IEnumerable<string> matches) => new()
    {
        ["kind"] = "tool-search",
        ["matches"] = new JsonArray([.. matches.Select(n => (JsonNode?)n)]),
    };
}
