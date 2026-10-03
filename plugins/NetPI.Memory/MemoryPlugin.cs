using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Memory;

/// <summary>
/// Semantic memory over past chats (idea-61wg9p phase 3): every chat's text — what the user asked, what the agent
/// answered, the compaction summaries — is embedded in chunks in the background (the Embeddings plugin), and
/// <c>memory_search</c> / <c>memory.search</c> find the chats closest in meaning to a question: "have we done this
/// before?", across projects or within one. Off without an embedding model; the chats' text goes only to the
/// configured embedding server. Nothing here decides anything: it finds chats, the agent or the user reads them.
/// </summary>
[NetPiPlugin("netpi.memory", Name = "Memory", Description = "Semantic search over past chats (needs the Embeddings plugin and an embedding model)", Order = 85)]
public sealed class MemoryPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "memory", Title = "Memory", Group = "Tools", Order = 62,
            Settings =
            [
                SettingInfo.Bool("memory.enabled", "Search past chats by meaning", true,
                    "With an embedding model (embed.model) every chat is embedded in the background and the memory_search tool finds earlier chats by meaning. The text goes only to the embedding server."),
                SettingInfo.Int("memory.maxChunksPerChat", "Pieces per chat", MemoryIndex.DefaultMaxChunks,
                    "How many pieces of ~1,200 characters a chat is embedded as (its start, its end and its summaries are kept first).", 4, 200),
            ],
        });
        var index = context.Track(new MemoryIndex(context));
        context.Tools.Register(new MemorySearchTool(context, index));
        context.Rpc.RegisterReadOnly("memory.search", async (r, rct) =>
        {
            var query = r.Required("query");
            var hits = await index.SearchAsync(query, r.Int("limit") ?? 5, r.Str("projectId"), r.Str("excludeSessionId"), rct).ConfigureAwait(false);
            return new JsonObject { ["available"] = hits is not null, ["chats"] = MemoryIndex.Json(hits ?? []) };
        }, "Past chats closest in meaning: { query, limit? (5), projectId?, excludeSessionId? } → { available, chats: [{ sessionId, title, projectId, updatedAt, score, snippet }] }");
        context.Rpc.Register("memory.reindex", async (_, rct) => await index.SweepAsync(rct).ConfigureAwait(false),
            "Embed the chats that changed since they were last embedded, now: { } → { enabled, indexed?, unchanged?, removed? }");
        context.Track(context.Events.Subscribe(EventTypes.SessionDeleted, e => index.Forget(e.SessionId)));
        index.Start();
        return Task.CompletedTask;
    }
}

internal sealed record MemoryHit(string SessionId, string Title, string? ProjectId, string UpdatedAt, double Score, string Snippet);

/// <summary>The chats' embeddings: one document per chat in <c>chats</c>, kept in step by a sweep every few minutes.</summary>
internal sealed class MemoryIndex : IDisposable
{
    public const int DefaultMaxChunks = 24;
    public const int ChunkChars = 1200;
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly IPluginContext _ctx;
    private readonly IDataCollection _store;
    private readonly SemaphoreSlim _sweeping = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Dictionary<string, Entry>? _cache;
    private Timer? _timer;

    private sealed record Entry(string Model, string Rev, string Title, string? ProjectId, string UpdatedAt, string[] Snippets, float[][] Vectors);

    public MemoryIndex(IPluginContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Data.Collection("chats", new CollectionSpec().Text("model").Text("projectId"));
    }

    private IEmbeddingService? Service =>
        _ctx.Settings.GetOr("memory.enabled", true) && _ctx.Services.Get<IEmbeddingService>() is { Model: not null } s ? s : null;

    public void Start()
    {
        // The first sweep a little after start (the other plugins come up first), then every five minutes.
        _timer = new Timer(_ => _ = Task.Run(SweepSafeAsync), null, TimeSpan.FromSeconds(30), Every);
    }

    private async Task SweepSafeAsync()
    {
        try { await SweepAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ctx.Logger.LogInformation("Memory: the chat index was not updated: {Message}", ex.Message); }
    }

    /// <summary>Embed every chat whose messages or model changed since it was embedded; drop the ones that are gone.</summary>
    public async Task<JsonObject> SweepAsync(CancellationToken ct)
    {
        if (Service is not { Model: { } model } service) return new JsonObject { ["enabled"] = false };
        if (!service.Available) return new JsonObject { ["enabled"] = true, ["available"] = false };
        await _sweeping.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = Load();
            var sessions = _ctx.Sessions.ListSessions(new SessionQuery { IncludeArchived = true })
                .Where(s => s.Kind == "chat").ToList();
            var maxChunks = Math.Clamp(_ctx.Settings.GetOr("memory.maxChunksPerChat", DefaultMaxChunks), 4, 200);
            int indexed = 0, unchanged = 0, removed = 0;
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var rev = $"{session.MessageCount}:{session.UpdatedAt.ToUnixTimeSeconds()}:{maxChunks}";
                if (cache.TryGetValue(session.Id, out var have) && have.Model == model && have.Rev == rev) { unchanged++; continue; }
                var chunks = Chunks(session, _ctx.Sessions.GetMessages(session.Id), maxChunks);
                if (chunks.Count == 0) { unchanged++; continue; }
                var result = await service.EmbedAsync(new EmbeddingRequest { Texts = chunks, Background = true }, ct).ConfigureAwait(false);
                var entry = new Entry(result.Model, rev, session.Title, session.ProjectId, session.UpdatedAt.ToString("O"),
                    chunks.Select(c => c.Length > 400 ? c[..400] + "…" : c).ToArray(), result.Vectors.ToArray());
                _store.Put(session.Id, new JsonObject
                {
                    ["model"] = entry.Model, ["rev"] = rev, ["title"] = entry.Title, ["projectId"] = entry.ProjectId, ["updatedAt"] = entry.UpdatedAt,
                    ["snippets"] = new JsonArray(entry.Snippets.Select(s => (JsonNode)s).ToArray()),
                    ["vectors"] = new JsonArray(entry.Vectors.Select(v => (JsonNode)VectorMath.Pack(v)).ToArray()),
                });
                lock (_gate) if (_cache is not null) _cache[session.Id] = entry;
                indexed++;
            }
            var live = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in cache.Keys.Where(k => !live.Contains(k) && _ctx.Sessions.GetSession(k) is null).ToList())
            {
                Forget(gone);
                removed++;
            }
            if (indexed + removed > 0) _ctx.Logger.LogInformation("Memory: embedded {Indexed} chats, removed {Removed} ({Model})", indexed, removed, model);
            return new JsonObject { ["enabled"] = true, ["model"] = model, ["indexed"] = indexed, ["unchanged"] = unchanged, ["removed"] = removed };
        }
        finally { _sweeping.Release(); }
    }

    public void Forget(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try { _store.Delete(sessionId); } catch (Exception ex) { _ctx.Logger.LogDebug("Memory: {Session} not removed: {Message}", sessionId, ex.Message); }
        lock (_gate) _cache?.Remove(sessionId);
    }

    /// <summary>The chats closest to the query, best chunk per chat; null without embeddings.</summary>
    public async Task<List<MemoryHit>?> SearchAsync(string query, int limit, string? projectId, string? excludeSessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || Service is not { Available: true, Model: { } model } service) return null;
        float[] q;
        try { q = (await service.EmbedAsync(new EmbeddingRequest { Texts = [query], Kind = EmbeddingKind.Query }, ct).ConfigureAwait(false)).Vectors[0]; }
        catch (EmbeddingException ex)
        {
            _ctx.Logger.LogDebug("Memory: no search ({Code}): {Message}", ex.Code, ex.Message);
            return null;
        }
        var hits = new List<MemoryHit>();
        foreach (var (id, entry) in Load())
        {
            if (entry.Model != model || id == excludeSessionId) continue;
            if (projectId is { Length: > 0 } && entry.ProjectId != projectId) continue;
            var best = -1f;
            var at = 0;
            for (var i = 0; i < entry.Vectors.Length; i++)
            {
                var s = VectorMath.Dot(q, entry.Vectors[i]);
                if (s > best) { best = s; at = i; }
            }
            if (best > -1) hits.Add(new MemoryHit(id, entry.Title, entry.ProjectId, entry.UpdatedAt, Math.Round(best, 3), entry.Snippets.ElementAtOrDefault(at) ?? ""));
        }
        return hits.OrderByDescending(h => h.Score).Take(Math.Clamp(limit, 1, 20)).ToList();
    }

    /// <summary>
    /// A chat as pieces to embed: the title with its first request, then its compaction summaries, then the rest of the
    /// conversation (user and agent text, tools by name only) — the first and last pieces kept when it has to be cut.
    /// </summary>
    internal static List<string> Chunks(SessionInfo session, IReadOnlyList<ChatMessage> messages, int maxChunks)
    {
        var summaries = new List<string>();
        var lines = new List<string>();
        foreach (var m in messages)
        {
            var text = m.Text.Trim();
            if (text.Length == 0) continue;
            switch (m.Role)
            {
                case MessageRole.Summary: summaries.Add("Summary: " + Clip(text, 3000)); break;
                case MessageRole.User: lines.Add("User: " + Clip(text, 1500)); break;
                case MessageRole.Assistant: lines.Add("Agent: " + Clip(text, 800)); break;
            }
        }
        if (lines.Count == 0 && summaries.Count == 0) return [];
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length > ChunkChars) { pieces.Add(current.ToString()); current.Clear(); }
            current.AppendLine(line.Length > ChunkChars ? line[..ChunkChars] : line);
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        var title = string.IsNullOrWhiteSpace(session.Title) ? "" : $"Chat: {session.Title}\n";
        var head = pieces.Count > 0 ? title + pieces[0] : title;
        var rest = pieces.Skip(1).ToList();
        var room = Math.Max(0, maxChunks - 1 - summaries.Count);
        if (rest.Count > room) rest = [.. rest.Take(room / 2), .. rest.Skip(rest.Count - (room - room / 2))];
        var chunks = new List<string> { head.Trim() };
        chunks.AddRange(summaries.Take(maxChunks - 1).Select(s => title + s));
        chunks.AddRange(rest.Select(r => title + r.Trim()));
        return chunks.Where(c => c.Length > 0).Take(maxChunks).ToList();
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private Dictionary<string, Entry> Load()
    {
        lock (_gate)
        {
            if (_cache is not null) return new Dictionary<string, Entry>(_cache, StringComparer.Ordinal);
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var doc in _store.Find())
            {
                var vectors = (doc.Doc["vectors"] as JsonArray ?? []).Select(v => VectorMath.Unpack(v?.GetValue<string>())).ToArray();
                if (vectors.Length == 0 || vectors.Any(v => v is null)) continue;
                map[doc.Key] = new Entry(Str(doc.Doc["model"]), Str(doc.Doc["rev"]), Str(doc.Doc["title"]), doc.Doc["projectId"]?.GetValue<string>(),
                    Str(doc.Doc["updatedAt"]), (doc.Doc["snippets"] as JsonArray ?? []).Select(s => Str(s)).ToArray(), vectors!);
            }
            _cache = map;
            return new Dictionary<string, Entry>(map, StringComparer.Ordinal);
        }
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static JsonArray Json(IEnumerable<MemoryHit> hits) => new(hits.Select(h => (JsonNode)new JsonObject
    {
        ["sessionId"] = h.SessionId, ["title"] = h.Title, ["projectId"] = h.ProjectId, ["updatedAt"] = h.UpdatedAt, ["score"] = h.Score, ["snippet"] = h.Snippet,
    }).ToArray());

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Dispose();
    }
}

/// <summary><c>memory_search</c>: the agent's way to find earlier chats about the same thing.</summary>
internal sealed class MemorySearchTool(IPluginContext ctx, MemoryIndex index) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "memory_search",
        Label = "Search past chats",
        Category = "memory",
        ReadOnly = true,
        SummaryArg = "query",
        Description = "Find earlier chats about something by meaning (\"have we done this before?\", \"how did we fix X\"): the closest chats with a snippet.",
        PromptGuidelines = ["Before larger work or when the user refers to an earlier discussion, memory_search finds past chats about it; quote what you use."],
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string","description":"what you are looking for, in your own words"},"limit":{"type":"integer","minimum":1,"maximum":10},"project":{"type":"string","description":"\"all\" (default: the session's project)"}},"required":["query"]}""")!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var query = a.Str("query")?.Trim() ?? "";
        if (query.Length == 0) return ToolResult.Error("Missing 'query'.");
        string? projectId = null;
        if (a.Str("project") is not "all")
            projectId = context.Project?.Id ?? ctx.Sessions.GetSession(context.SessionId)?.ProjectId;
        var hits = await index.SearchAsync(query, a.Int("limit") ?? 5, projectId, context.SessionId, ct).ConfigureAwait(false);
        if (hits is null) return ToolResult.Error("Memory search is not available: no embedding model (embed.model) or the embedding server is not answering.");
        if (hits.Count == 0) return ToolResult.Ok("No earlier chats are indexed yet" + (projectId is null ? "." : " in this project (project \"all\" searches every project)."));
        var text = new StringBuilder($"{hits.Count} earlier chats closest in meaning (score = cosine; below about 0.6 they are probably not about it):\n");
        foreach (var h in hits)
            text.Append("- ").Append(h.SessionId).Append(" \"").Append(h.Title).Append("\" (").Append(h.UpdatedAt[..Math.Min(10, h.UpdatedAt.Length)])
                .Append(", ").Append(h.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append("): ")
                .Append(h.Snippet.Replace('\n', ' ')).Append('\n');
        return ToolResult.Ok(text.ToString().TrimEnd(), new JsonObject { ["chats"] = MemoryIndex.Json(hits) });
    }
}
