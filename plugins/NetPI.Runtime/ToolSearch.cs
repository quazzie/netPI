using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
        var loaded = LoadedTools.Of(session);
        // what a search can offer: deferred tools this chat may call, not the gateways (mcp_call) that reach them another way
        var candidates = eligible.Where(t => t.Definition.Deferred && t is not IIndirectAgentTool).ToList();
        if (candidates.Count == 0)
            return Task.FromResult(ToolResult.Ok("No tools to search: every tool you can use is already declared to you.",
                Details([], [])));

        var ranked = Rank(candidates.Select(t => t.Definition).ToList(), query).Take(limit).ToList();
        if (ranked.Count == 0)
            return Task.FromResult(ToolResult.Ok($"No tool matches \"{query}\" among {candidates.Count} searchable tools. Try other words for what the tool does.",
                Details([], [])));

        var fresh = ranked.Where(d => !loaded.Contains(d.Name)).ToList();
        // a loaded name whose tool is gone (its MCP server is down) keeps its place for when it is back, but takes no room
        var room = Math.Max(0, MaxLoaded - candidates.Count(t => loaded.Contains(t.Definition.Name)));
        var loading = fresh.Take(room).ToList();
        if (loading.Count > 0)
            ctx.Sessions.UpdateSession(context.SessionId, s =>
            {
                var names = LoadedTools.Of(s);
                foreach (var d in loading) names.Add(d.Name);
                s.Meta ??= new JsonObject();
                s.Meta[LoadedTools.MetaKey] = new JsonArray([.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
            });

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

    // ---------------------------------------------------------------- ranking

    /// <summary>
    /// The definitions that match <paramref name="query"/>, best first: a query that is a tool's exact name comes first, the
    /// rest by Okapi BM25 (k1 1.2, b 0.75) over each tool's <see cref="Document"/>. Ties keep name order.
    /// </summary>
    internal static List<ToolDefinition> Rank(IReadOnlyList<ToolDefinition> tools, string query)
    {
        var trimmed = query.Trim();
        var exact = tools.Where(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) return exact;

        var docs = tools.Select(t => Tokenize(Document(t)).GroupBy(w => w, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)).ToList();
        var lengths = docs.Select(d => d.Values.Sum()).ToList();
        var average = Math.Max(1.0, lengths.Count == 0 ? 1 : lengths.Average());
        const double k1 = 1.2, b = 0.75;
        var idf = terms.ToDictionary(term => term, term =>
        {
            var df = docs.Count(d => d.ContainsKey(term));
            return Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
        }, StringComparer.Ordinal);

        var scored = new List<(ToolDefinition Tool, double Score)>();
        for (var i = 0; i < tools.Count; i++)
        {
            double score = 0;
            foreach (var term in terms)
            {
                if (!docs[i].TryGetValue(term, out var count)) continue;
                var norm = k1 * (1 - b + b * lengths[i] / average);
                score += idf[term] * count * (k1 + 1) / (count + norm);
            }
            if (score > 0) scored.Add((tools[i], score));
        }
        var ranked = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Tool.Name, StringComparer.Ordinal).Select(s => s.Tool);
        return [.. exact, .. ranked.Where(t => !exact.Contains(t))];
    }

    /// <summary>
    /// What a tool is found by: its name (and with <c>_</c> as spaces), label, category, description and the longer help,
    /// and its parameters' names and descriptions.
    /// </summary>
    internal static string Document(ToolDefinition tool)
    {
        var parts = new List<string> { tool.Name, tool.Name.Replace('_', ' '), tool.Label ?? "", tool.Category, tool.Description, tool.Help ?? "" };
        SchemaText(tool.Parameters, parts, 0);
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    private static void SchemaText(JsonNode? schema, List<string> parts, int depth)
    {
        if (schema is not JsonObject o || depth > 6) return;
        if (o["description"] is JsonValue d && d.TryGetValue<string>(out var text)) parts.Add(text);
        if (o["properties"] is JsonObject properties)
            foreach (var (name, property) in properties)
            {
                parts.Add(name);
                SchemaText(property, parts, depth + 1);
            }
        SchemaText(o["items"], parts, depth + 1);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (o[key] is JsonArray variants)
                foreach (var variant in variants) SchemaText(variant, parts, depth + 1);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the",
        "this", "to", "with", "tool", "tools",
    };

    /// <summary>Lowercase terms split at camelCase and non-alphanumerics, without stop words, in a naive singular form.</summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var split = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
        split = Regex.Replace(split, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        foreach (Match m in Regex.Matches(split.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant))
            if (!StopWords.Contains(m.Value)) yield return Stem(m.Value);
    }

    /// <summary>So <c>issues</c> finds <c>issue</c> and <c>searches</c> finds <c>search</c>.</summary>
    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && Regex.IsMatch(term, "(ches|shes|sses|xes|zes)$")) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }
}
