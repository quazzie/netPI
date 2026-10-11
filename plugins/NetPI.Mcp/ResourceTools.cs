using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Mcp;

/// <summary>
/// Finds and reads the resources servers advertise. A query ranks them (<see cref="Rank"/>); a URI reads one, and the
/// URI is the whole gate: a URI the server never listed is not requested, so a link that merely appears in a tool
/// result is never fetched.
/// </summary>
internal sealed class McpResourceTool(IPluginContext ctx, ServerManager manager) : IAgentTool
{
    /// <summary>Most resources one query lists.</summary>
    public const int MaxResults = 5;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "mcp_resource", Label = "MCP resource", Category = "mcp", ReadOnly = true, SummaryArg = "uri",
        Description = "Find and read the resources MCP servers publish (documents, skills, reference files): query lists the closest, uri reads one.",
        Parameters = JsonNode.Parse(
            """
            {"type":"object","properties":{"query":{"type":"string","description":"What the resource is about, in a few words: lists the closest resources."},
             "uri":{"type":"string","description":"Exact uri of a listed resource: reads it."},
             "server":{"type":"string","description":"Server id: only its resources, or the one meant when the same uri is advertised by more than one."}},
             "additionalProperties":false}
            """)!.AsObject(),
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var server = a.Str("server");
        var uri = a.Str("uri")?.Trim();
        if (string.IsNullOrEmpty(uri))
        {
            var query = a.Str("query")?.Trim() ?? "";
            if (query.Length is 0 or > 500) return Task.FromResult(ToolResult.Error("Give a query (1–500 characters) to find a resource, or the uri of one to read."));
            var found = Rank(manager.Resources(server), query).Take(MaxResults)
                .Select(r => (JsonNode)r.Summary(manager.Available(r.ServerId))).ToArray();
            return Task.FromResult(ToolResult.Ok(new JsonObject { ["results"] = new JsonArray(found), ["noMatch"] = found.Length == 0 }.ToJsonString()));
        }
        if (uri.Length > 2048) return Task.FromResult(ToolResult.Error("uri must be an exact resource uri (at most 2048 characters)."));
        var resource = manager.FindResource(uri, server)
            ?? throw new McpException("MCP resource " + uri + " is not advertised by " + (server ?? "any connected server")
                + ". Find it with a query first; only listed resources can be read.");
        ctx.Logger.LogDebug("MCP {Server}: reading resource {Uri}", resource.ServerId, resource.Uri);
        return manager.ReadResourceAsync(resource, ct);
    }

    /// <summary>
    /// The resources that match <paramref name="query"/>, best first: one whose uri or name is the query comes first, the
    /// rest by Okapi BM25 over the uri, name, description and MIME type (<see cref="ToolRanking.Tokenize"/>'s terms).
    /// </summary>
    internal static List<RemoteResource> Rank(IReadOnlyList<RemoteResource> resources, string query)
    {
        var trimmed = query.Trim();
        var terms = ToolRanking.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        var docs = resources.Select(r => ToolRanking.Tokenize(r.SearchText).GroupBy(w => w, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)).ToList();
        var average = Math.Max(1.0, docs.Count == 0 ? 1 : docs.Average(d => d.Values.Sum()));
        const double k1 = 1.2, b = 0.75;
        var scored = new List<(RemoteResource Resource, double Score)>();
        for (var i = 0; i < resources.Count; i++)
        {
            var r = resources[i];
            double score = string.Equals(r.Uri, trimmed, StringComparison.Ordinal) || string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase) ? 10000 : 0;
            foreach (var term in terms)
            {
                if (!docs[i].TryGetValue(term, out var count)) continue;
                var df = docs.Count(d => d.ContainsKey(term));
                var idf = Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
                score += idf * count * (k1 + 1) / (count + k1 * (1 - b + b * docs[i].Values.Sum() / average));
            }
            if (score > 0) scored.Add((r, score));
        }
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Resource.Uri, StringComparer.Ordinal).Select(s => s.Resource)];
    }
}
