using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

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
        Description = "Find external tools and resources. Search summaries, then request detail=schema for one exact tool id before mcp_call; read a resource's uri with mcp_resource.",
        PromptGuidelines = ["For external capabilities, use mcp_search, inspect only the selected schema, then mcp_call with its revision.", "A resource uri from mcp_search is read with mcp_resource."],
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string"},"server":{"type":"string"},"detail":{"type":"string","enum":["summary","schema"]},"limit":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}""")!.AsObject(),
    };
    internal static IReadOnlyList<IAgentTool> Eligible(IPluginContext ctx, ToolContext context) =>
        context.EligibleTools?.Invoke() ?? ToolSelection.Eligible(ctx.Tools,
            ctx.Services.Get<IAgentRuntime>()?.GetBySession(context.SessionId), ctx.Sessions.GetSession(context.SessionId),
            ToolSelection.MaxDepth(ctx.Settings));

    internal static List<RemoteTool> Rank(IEnumerable<RemoteTool> tools, string query, int limit) =>
        Score(tools, query, t => t.Definition.Name + " " + t.RemoteName, t => t.SearchText).Take(limit).Select(s => s.Item).ToList();

    /// <summary>One ranked candidate: a tool, or a resource, scored by the same local rules.</summary>
    internal static List<(double Score, T Item, string Id)> Score<T>(IEnumerable<T> items, string query,
        Func<T, string> exact, Func<T, string> text)
    {
        var words = Tokens(query).Distinct(StringComparer.Ordinal).ToArray();
        var docs = items.Select(i => (Item: i, Exact: exact(i), Words: Tokens(text(i)).ToArray())).ToList();
        var average = docs.Count == 0 ? 1 : docs.Average(d => d.Words.Length);
        var frequencies = words.ToDictionary(w => w, w => docs.Count(d => d.Words.Contains(w, StringComparer.Ordinal)), StringComparer.Ordinal);
        double Score((T Item, string Exact, string[] Words) doc)
        {
            if (doc.Exact.Split(' ').Contains(query.Trim(), StringComparer.Ordinal)) return 10000;
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
        return docs.Select(d => (Score: Score(d), Item: d.Item, Id: d.Exact)).Where(d => d.Score > 0)
            .OrderByDescending(d => d.Score).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }
    private static IEnumerable<string> Tokens(string text) =>
        Regex.Matches(Regex.Replace(text, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value).Where(s => s.Length > 1 && s is not ("the" or "a" or "to" or "of" or "for" or "and" or "tool"));

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var query = a.Str("query")?.Trim() ?? "";
        if (query.Length is 0 or > 500) return ToolResult.Error("query must contain 1–500 characters.");
        var detail = a.Str("detail") ?? "summary";
        if (detail is not ("summary" or "schema")) return ToolResult.Error("detail must be summary or schema.");
        var limit = a.Int("limit") ?? (detail == "schema" ? 1 : 3);
        limit = Math.Clamp(limit, 1, detail == "schema" ? 1 : 5);
        var server = a.Str("server");
        var eligible = Eligible(ctx, context).ToHashSet(ReferenceEqualityComparer.Instance);
        var catalog = manager.Catalog().Where(t => eligible.Contains(t) && (server is null || t.ServerId == server)).ToList();
        var resources = detail == "schema" ? new List<RemoteResource>() : manager.Resources(server);
        // One pool, not two. Scoring tools and resources separately made the kinds incomparable: idf is computed over
        // the documents being ranked, so a rare term in a 77-tool pool always outscored the same term in a 16-resource
        // one, and tools won every query by default. Tools and resources compete here on the same terms.
        var candidates = catalog.Select(t => (Kind: "tool", Id: t.Definition.Name, Exact: t.Definition.Name + " " + t.RemoteName, Text: t.SearchText, Value: (object)t))
            .Concat(resources.Select(r => (Kind: "resource", Id: r.Uri, Exact: r.Uri + " " + r.Name, Text: r.SearchText, Value: (object)r)))
            .ToList();
        var ranked = McpSearchTool.Score(candidates, query, c => c.Exact, c => c.Text)
            .OrderByDescending(s => s.Score).ThenBy(s => s.Item.Id, StringComparer.Ordinal).Take(limit).ToList();
        // No word in common with any tool (a paraphrase, another language's term): with embeddings, the closest by meaning
        // instead of "no match". Only then: a word match stays the ranking it always was.
        var byMeaning = false;
        if (ranked.Count == 0 && candidates.Count > 0 && ctx.Services.Get<IEmbeddingService>() is { Available: true } embedder)
        {
            var near = await NearestAsync(embedder, query, candidates.Select(c => (c.Kind + ":" + c.Id, c.Exact + "\n" + c.Text)).ToList(), ct).ConfigureAwait(false);
            if (near is not null)
            {
                ranked = near.Where(n => n.Score >= MeaningFloor).Take(limit)
                    .Select(n => (Score: (double)n.Score, Item: candidates[n.Index], Id: candidates[n.Index].Exact)).ToList();
                byMeaning = ranked.Count > 0;
            }
        }
        var budget = ToolResultLimit.Fit(ctx.Settings, manager.Limit("mcp.discoveryChars", 4000, 1024, 20000));
        var disclosures = new JsonArray(); var results = new JsonArray();
        var known = DiscoveryState.Current(ctx.Sessions, context.SessionId);
        foreach (var entry in ranked)
        {
            if (entry.Item.Kind == "resource")
            {
                var resource = (RemoteResource)entry.Item.Value;
                if (results.ToJsonString().Length + 400 > budget - 100) break;
                results.Add(resource.Summary(manager.Available(resource.ServerId)));
                continue;
            }
            var tool = (RemoteTool)entry.Item.Value;
            var result = tool.Summary(manager.Available(tool.ServerId));
            result["kind"] = "tool";
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
                        await File.WriteAllTextAsync(path, tool.Definition.Parameters.ToJsonString(NetPiJson.Indented), ct).ConfigureAwait(false);
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
        var answer = new JsonObject { ["results"] = results, ["noMatch"] = results.Count == 0 };
        if (byMeaning) answer["match"] = "meaning: no tool shares a word with the query; these are the closest in meaning";
        return ToolResult.Ok(answer.ToJsonString(), new JsonObject { ["kind"] = DiscoveryState.Kind, ["schemas"] = disclosures });
    }

    /// <summary>Below this cosine (bge-base) a tool is not offered as a meaning match: an unrelated tool is not an answer.</summary>
    internal const float MeaningFloor = 0.55f;
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Cosine of the query against each candidate (its name and search text), best first; candidate vectors are kept per
    /// id and text, so a catalogue is embedded once and a changed tool again. Null on any embedding failure.
    /// </summary>
    private async Task<List<(int Index, float Score)>?> NearestAsync(IEmbeddingService embedder, string query, List<(string Key, string Text)> items, CancellationToken ct)
    {
        try
        {
            string KeyOf((string Key, string Text) i) => i.Key + "\n" + i.Text.GetHashCode(StringComparison.Ordinal);
            List<(string Key, string Text)> missing;
            lock (_gate) missing = items.Where(i => !_vectors.ContainsKey(KeyOf(i))).ToList();
            if (missing.Count > 0)
            {
                var docs = await embedder.EmbedAsync(new EmbeddingRequest { Texts = missing.Select(m => m.Text.Length > 2000 ? m.Text[..2000] : m.Text).ToList(), Background = true }, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_vectors.Count > 4000) _vectors.Clear();
                    for (var i = 0; i < missing.Count; i++) _vectors[KeyOf(missing[i])] = docs.Vectors[i];
                }
            }
            var q = (await embedder.EmbedAsync(new EmbeddingRequest { Texts = [query], Kind = EmbeddingKind.Query }, ct).ConfigureAwait(false)).Vectors[0];
            lock (_gate)
                return items.Select((item, index) => (index, score: _vectors.TryGetValue(KeyOf(item), out var v) ? VectorMath.Dot(q, v) : -1f))
                    .OrderByDescending(x => x.score).Select(x => (x.index, x.score)).ToList();
        }
        catch (EmbeddingException ex)
        {
            ctx.Logger.LogDebug("mcp_search: no meaning match ({Code}): {Message}", ex.Code, ex.Message);
            return null;
        }
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
        var a = new ToolArgs(arguments);
        if (!a.TryGet(out var inner, "arguments")) throw new McpException("mcp_call needs 'arguments': the tool's arguments (an object, {} for none).");
        if (inner.ValueKind != JsonValueKind.Object) throw new McpException("arguments must be an object.");
        var schema = tool.Definition.Parameters;
        var args = JsonNode.Parse(inner.GetRawText()) as JsonObject ?? throw new McpException("arguments must be an object.");
        JsonElement effective;
        try { Schema.Validate(schema, args); effective = NetPiJson.ToElement(args); }
        catch (McpException ex) { effective = Repaired(tool, schema, args, ex); }
        return ValueTask.FromResult(new ResolvedToolCall { ToolName = tool.Definition.Name, Arguments = effective, ServerId = tool.ServerId });
    }
    /// <summary>
    /// The arguments as the server will receive them, after the repair a model needs and the schema allows. A model
    /// that writes JSON as a string inside a typed argument (<c>"list_only":"true"</c>,
    /// <c>"config":"{\"views\":[…]}"</c>) fails the validation above; the discovered schema is then the only authority
    /// that may re-read such a value, and only when the repaired arguments are the ones the schema accepts. Anything
    /// else keeps the original error, so nothing is guessed on the model's behalf.
    /// </summary>
    private JsonElement Repaired(RemoteTool tool, JsonObject schema, JsonObject args, McpException error)
    {
        if (Coercion.Repair(schema, args) is not { } repaired) throw error;
        try { Schema.Validate(schema, repaired); }
        catch (McpException) { throw error; }
        ctx.Logger.LogInformation("MCP {Server}/{Tool}: repaired {Count} argument(s) written as text against the discovered schema",
            tool.ServerId, tool.RemoteName, repaired.Count(p => !JsonNode.DeepEquals(p.Value, args[p.Key])));
        return NetPiJson.ToElement(repaired);
    }
    public ValueTask ValidateAsync(ToolContext context, JsonElement originalArguments, CancellationToken ct)
    { Check(context, originalArguments); return ValueTask.CompletedTask; }
    private RemoteTool Check(ToolContext context, JsonElement args)
    {
        var a = new ToolArgs(args);
        var id = a.Str("id") ?? throw new McpException("mcp_call needs 'id': the exact tool id from mcp_search.");
        var revision = a.Str("revision");
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
