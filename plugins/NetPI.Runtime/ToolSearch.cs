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
        var loaded = exposure.Loaded;
        // what a search can offer: deferred tools this chat may call
        var candidates = eligible.Where(t => exposure.IsDeferred(t.Definition)).ToList();
        if (candidates.Count == 0)
            return Task.FromResult(ToolResult.Ok("No tools to search: every tool you can use is already declared to you.",
                Details([], [])));

        // only real matches: a tool that shares a single word with the query does not ride along
        var ranked = ToolRanking.Rank(candidates.Select(t => t.Definition).ToList(), query, MinRelative).Take(limit).ToList();
        if (ranked.Count == 0)
            return Task.FromResult(ToolResult.Ok($"No tool matches \"{query}\" among {candidates.Count} searchable tools. Try other words for what the tool does.",
                Details([], [])));
        if (exposure.Mode == ToolExposure.Inline)
        {
            // the request's tools never change (that would be a full prefill): the schemas are in this result, and the tools
            // are run with call_tool. Recorded as loaded for the chat's tool list, not declared.
            LoadedTools.Add(ctx.Sessions, context.SessionId, ranked.Select(d => d.Name), exposure.Mode);
            return Task.FromResult(ToolResult.Ok(Inline(ranked), Details(ranked.Select(d => d.Name), ranked.Select(d => d.Name))));
        }

        var fresh = ranked.Where(d => !loaded.Contains(d.Name)).ToList();
        // a loaded name whose tool is gone (its MCP server is down) keeps its place for when it is back, but takes no room
        var room = Math.Max(0, MaxLoaded - candidates.Count(t => loaded.Contains(t.Definition.Name)));
        var loading = fresh.Take(room).ToList();
        LoadedTools.Add(ctx.Sessions, context.SessionId, loading.Select(d => d.Name), exposure.Mode);

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

    /// <summary>Weakest match kept, as a share of the best score.</summary>
    public const double MinRelative = 0.4;
    /// <summary>Most characters of schemas in one result (the result cap is 20000; past it the tail would be cut off).</summary>
    private const int SchemaBudget = 15000;

    /// <summary>The matches with their schemas, for <c>call_tool</c>; what does not fit the budget is named only.</summary>
    private static string Inline(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.Append("Found ").Append(tools.Count).Append(tools.Count == 1 ? " tool" : " tools")
          .Append(". Run one with call_tool { \"name\": ..., \"arguments\": { ... } }; the arguments follow its schema.");
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

    private static JsonObject Details(IEnumerable<string> loaded, IEnumerable<string> matches) => new()
    {
        ["kind"] = "tool-search",
        ["loaded"] = new JsonArray([.. loaded.Select(n => (JsonNode?)n)]),
        ["matches"] = new JsonArray([.. matches.Select(n => (JsonNode?)n)]),
    };
}
