using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Mcp;

internal static class DiscoveryState
{
    public const string Kind = "mcp-discovery";
    public static IReadOnlyDictionary<string, string> Current(ISessionStore sessions, string sessionId)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in sessions.GetContextMessages(sessionId))
            foreach (var result in message.Parts.OfType<ToolResultPart>())
                if (!result.IsError && result.Details is JsonObject details && details["kind"]?.GetValue<string>() == Kind
                    && details["schemas"] is JsonArray schemas)
                    foreach (var schema in schemas.OfType<JsonObject>())
                        if (schema["id"] is JsonValue id && schema["revision"] is JsonValue revision)
                            found[id.GetValue<string>()] = revision.GetValue<string>();
        return found;
    }
}

internal sealed class McpSearchTool(IPluginContext ctx, ServerManager manager) : IAgentTool, IDeferredToolInfrastructure
{
    public bool Supports(ToolDefinition target) => target.Category == "mcp";
    public ToolDefinition Definition { get; } = new()
    {
        Name = "mcp_search", Label = "Find MCP tool", Category = "mcp", ReadOnly = true, SummaryArg = "query",
        Description = "Find external tools. Search summaries, then request detail=schema for one exact tool id before mcp_call.",
        PromptGuidelines = ["For external capabilities, use mcp_search, inspect only the selected schema, then mcp_call with its revision."],
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string"},"server":{"type":"string"},"detail":{"type":"string","enum":["summary","schema"]},"limit":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}""")!.AsObject(),
    };
    internal static IReadOnlyList<IAgentTool> Eligible(IPluginContext ctx, ToolContext context) =>
        context.EligibleTools?.Invoke() ?? ToolSelection.Eligible(ctx.Tools,
            ctx.Services.Get<IAgentRuntime>()?.GetBySession(context.SessionId), ctx.Sessions.GetSession(context.SessionId),
            ctx.Settings.Get("agents.maxDepth", 3));

    internal static List<RemoteTool> Rank(IEnumerable<RemoteTool> tools, string query, int limit)
    {
        var words = Tokens(query).Distinct(StringComparer.Ordinal).ToArray();
        var docs = tools.Select(t => (Tool: t, Words: Tokens(t.SearchText).ToArray())).ToList();
        var average = docs.Count == 0 ? 1 : docs.Average(d => d.Words.Length);
        var frequencies = words.ToDictionary(w => w, w => docs.Count(d => d.Words.Contains(w, StringComparer.Ordinal)), StringComparer.Ordinal);
        double Score((RemoteTool Tool, string[] Words) doc)
        {
            if (doc.Tool.Definition.Name == query || doc.Tool.RemoteName == query) return 10000;
            double score = 0;
            foreach (var word in words)
            {
                var frequency = doc.Words.Count(w => w == word);
                if (frequency == 0) continue;
                var df = frequencies[word];
                var idf = Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
                score += idf * frequency * 2.2 / (frequency + 1.2 * (0.25 + 0.75 * doc.Words.Length / Math.Max(average, 1)));
            }
            return score;
        }
        return docs.Select(d => (d.Tool, Score: Score(d))).Where(d => d.Score > 0)
            .OrderByDescending(d => d.Score).ThenBy(d => d.Tool.Definition.Name, StringComparer.Ordinal).Take(limit).Select(d => d.Tool).ToList();
    }
    private static IEnumerable<string> Tokens(string text) =>
        Regex.Matches(Regex.Replace(text, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value).Where(s => s.Length > 1 && s is not ("the" or "a" or "to" or "of" or "for" or "and" or "tool"));

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()!.Trim() : "";
        if (query.Length is 0 or > 500) return ToolResult.Error("query must contain 1–500 characters.");
        var detail = args.TryGetProperty("detail", out var d) ? d.GetString() : "summary";
        if (detail is not ("summary" or "schema")) return ToolResult.Error("detail must be summary or schema.");
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : detail == "schema" ? 1 : 3;
        limit = Math.Clamp(limit, 1, detail == "schema" ? 1 : 5);
        var server = args.TryGetProperty("server", out var s) ? s.GetString() : null;
        var eligible = Eligible(ctx, context).ToHashSet(ReferenceEqualityComparer.Instance);
        var catalog = manager.Catalog().Where(t => eligible.Contains(t) && (server is null || t.ServerId == server)).ToList();
        var matches = Rank(catalog, query, limit);
        var budget = ToolResultLimit.Fit(ctx.Settings, manager.Limit("mcp.discoveryChars", 4000, 1024, 20000));
        var disclosures = new JsonArray(); var results = new JsonArray();
        var known = DiscoveryState.Current(ctx.Sessions, context.SessionId);
        foreach (var tool in matches)
        {
            var result = tool.Summary(manager.Available(tool.ServerId));
            JsonObject? disclosure = null;
            if (detail == "schema")
            {
                result["revision"] = tool.Definition.Revision;
                if (known.GetValueOrDefault(tool.Definition.Name) == tool.Definition.Revision)
                    result["schema"] = "Already disclosed in this context.";
                else
                {
                    result["schema"] = tool.Definition.Parameters.DeepClone();
                    result["description"] = tool.Definition.Help;
                    if (result.ToJsonString().Length + results.ToJsonString().Length > budget - 300)
                    {
                        var directory = Path.Combine(ctx.Paths.TempDir, "mcp-schemas");
                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, tool.Definition.Revision + ".json");
                        await File.WriteAllTextAsync(path, tool.Definition.Parameters.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
                        result.Remove("schema"); result["description"] = tool.Definition.Description;
                        result["schemaFile"] = path; result["schemaChars"] = tool.Definition.ParametersChars;
                        result["instruction"] = "Read this complete schema file before constructing arguments.";
                    }
                    disclosure = new JsonObject { ["id"] = tool.Definition.Name, ["revision"] = tool.Definition.Revision, ["serverId"] = tool.ServerId };
                }
            }
            if (results.ToJsonString().Length + result.ToJsonString().Length > budget - 100) break;
            results.Add(result);
            if (disclosure is not null) disclosures.Add(disclosure);
        }
        return ToolResult.Ok(new JsonObject { ["results"] = results, ["noMatch"] = results.Count == 0 }.ToJsonString(),
            new JsonObject { ["kind"] = DiscoveryState.Kind, ["schemas"] = disclosures });
    }
}

internal sealed class McpCallTool(IPluginContext ctx, ServerManager manager) : IIndirectAgentTool, IDeferredToolInfrastructure
{
    public bool Supports(ToolDefinition target) => target.Category == "mcp";
    public ToolDefinition Definition { get; } = new()
    {
        Name = "mcp_call", Label = "MCP", Category = "mcp", SummaryArg = "id",
        Description = "Call an external tool after mcp_search disclosed its schema. Supply its exact id, revision and arguments.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"},"revision":{"type":"string"},"arguments":{"type":"object"}},"required":["id","revision","arguments"],"additionalProperties":false}""")!.AsObject(),
    };
    public ValueTask<ResolvedToolCall> ResolveAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var tool = Check(context, arguments);
        var inner = arguments.GetProperty("arguments");
        if (inner.ValueKind != JsonValueKind.Object) throw new McpException("arguments must be an object.");
        Schema.Validate(tool.Definition.Parameters, JsonNode.Parse(inner.GetRawText()));
        return ValueTask.FromResult(new ResolvedToolCall { ToolName = tool.Definition.Name, Arguments = inner.Clone(), ServerId = tool.ServerId });
    }
    public ValueTask ValidateAsync(ToolContext context, JsonElement originalArguments, CancellationToken ct)
    { Check(context, originalArguments); return ValueTask.CompletedTask; }
    private RemoteTool Check(ToolContext context, JsonElement args)
    {
        var id = args.GetProperty("id").GetString()!;
        var revision = args.GetProperty("revision").GetString();
        var tool = manager.Find(id) ?? throw new McpException("MCP tool no longer exists. Search again.");
        if (!McpSearchTool.Eligible(ctx, context).Any(t => ReferenceEquals(t, tool))) throw new McpException("This MCP tool is disabled or outside this agent's tool selection.");
        if (revision != tool.Definition.Revision) throw new McpException("MCP schema revision changed. Inspect its schema again.");
        if (DiscoveryState.Current(ctx.Sessions, context.SessionId).GetValueOrDefault(id) != revision)
            throw new McpException("Inspect this tool with mcp_search detail=schema in this chat before calling it.");
        return tool;
    }
    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
        Task.FromResult(ToolResult.Error("Indirect calls must execute through the NetPI runtime."));
}
