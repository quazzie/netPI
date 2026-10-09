using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// <c>tool_search</c>: finds deferred tools (any plugin's, an MCP server's included) the session has not loaded yet and loads
/// the matches into <c>meta.toolsLoaded</c> (<see cref="LoadedTools"/>), so the next model call declares them with their own
/// schemas and the model calls them directly. The idea is pi 1.0's tool_search (idea-8qlb5s).
/// <para>Its description names no tool, so it stays the same while tools come and go (MCP servers connecting) and the
/// request prefix keeps. It is deferred-tool infrastructure: a chat that has no deferred tool is not sent it.</para>
/// <para>The load is a change to the session's meta, so the next call gets the "tools" notice with the new tools'
/// guidelines and the cause <c>tool-search</c>; a fork keeps the list, and a tool switched off for the chat stays off.</para>
/// </summary>
internal sealed class ToolSearchTool(IPluginContext ctx, AgentRuntime runtime) : IAgentTool, IDeferredToolInfrastructure
{
    public const string ToolName = "tool_search";
    public const int DefaultLimit = 5;
    public const int MaxLimit = 10;
    /// <summary>
    /// Most tools one chat keeps loaded: each one is in every request from then on. Past it the model is told to make do,
    /// instead of the request growing without bound.
    /// </summary>
    public const int MaxLoaded = 40;

    public bool Supports(ToolDefinition target) => true;

    public ToolDefinition Definition { get; } = new()
    {
        Name = ToolName, Label = "Find tools", Category = "general", ReadOnly = true, SummaryArg = "query",
        Description = "Find tools that are not loaded yet (deferred tools, such as an MCP server's) by what they do, and load the matches: "
            + "they are declared to you from your next call, with their full schemas, and you call them directly.",
        PromptGuidelines = ["When none of your tools fits a task, search with tool_search: it loads matching tools, callable from your next call."],
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
        var loaded = exposure.Loaded;
        // what a search can offer: deferred tools this chat may call, not the gateways (mcp_call) that reach them another way
        var candidates = eligible.Where(t => exposure.IsDeferred(t.Definition) && t is not IIndirectAgentTool).ToList();
        if (candidates.Count == 0)
            return Task.FromResult(ToolResult.Ok("No tools to search: every tool you can use is already declared to you.",
                Details([], [])));

        var ranked = ToolRanking.Rank(candidates.Select(t => t.Definition).ToList(), query).Take(limit).ToList();
        if (ranked.Count == 0)
            return Task.FromResult(ToolResult.Ok($"No tool matches \"{query}\" among {candidates.Count} searchable tools. Try other words for what the tool does.",
                Details([], [])));

        var fresh = ranked.Where(d => !loaded.Contains(d.Name)).ToList();
        // a loaded name whose tool is gone (its MCP server is down) keeps its place for when it is back, but takes no room
        var room = Math.Max(0, MaxLoaded - candidates.Count(t => loaded.Contains(t.Definition.Name)));
        var loading = fresh.Take(room).ToList();
        LoadedTools.Add(ctx.Sessions, context.SessionId, loading.Select(d => d.Name));

        var sb = new StringBuilder();
        if (loading.Count > 0)
            sb.Append("Loaded ").Append(loading.Count).Append(loading.Count == 1 ? " tool" : " tools")
              .Append(", callable from your next call:").Append(Lines(loading));
        var already = ranked.Where(d => loaded.Contains(d.Name)).ToList();
        if (already.Count > 0)
            sb.Append(sb.Length > 0 ? "\n" : "").Append("Already loaded:").Append(Lines(already));
        var skipped = fresh.Skip(loading.Count).ToList();
        if (skipped.Count > 0)
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"Not loaded, this chat already has the most loaded tools ({MaxLoaded}):").Append(Lines(skipped));
        return Task.FromResult(ToolResult.Ok(sb.ToString(),
            Details(loading.Select(d => d.Name), ranked.Select(d => d.Name))));
    }

    private static string Lines(IEnumerable<ToolDefinition> tools) =>
        string.Concat(tools.Select(d => "\n- " + d.Name + ": " + FirstLine(d.Description)));

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static JsonObject Details(IEnumerable<string> loaded, IEnumerable<string> matches) => new()
    {
        ["kind"] = "tool-search",
        ["loaded"] = new JsonArray([.. loaded.Select(n => (JsonNode?)n)]),
        ["matches"] = new JsonArray([.. matches.Select(n => (JsonNode?)n)]),
    };
}
