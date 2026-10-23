using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Memory;

/// <summary>
/// Memory over past chats (idea-61wg9p phase 3): every chat's text — what the user asked, what the agent answered, the
/// tool calls, failed tools, subagent reports and compaction summaries — is indexed in pieces in the background (and
/// embedded with the Embeddings plugin's model when there is one), and <c>memory_search</c> / <c>memory.search</c> find
/// the chats closest to a question by meaning and by its words: "have we done this before?", "where did ERR_X come
/// up?", across projects or within one. The chats' text goes only to the configured embedding server. Nothing here decides anything: it finds chats, the agent or the user reads them.
/// </summary>
[NetPiPlugin("netpi.memory", Name = "Memory", Description = "Search over past chats by meaning and by words (meaning needs the Embeddings plugin and an embedding model)", Order = 85)]
public sealed class MemoryPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "memory", Title = "Memory", Group = "Knowledge", Order = 20,
            Settings =
            [
                SettingInfo.Bool("memory.enabled", "Search past chats", true,
                    "Every chat is indexed in the background and the memory_search tool finds earlier chats by their words and, with an embedding model (embed.model), by meaning. The text goes only to the embedding server."),
                SettingInfo.Int("memory.maxChunksPerChat", "Pieces per chat", MemoryIndex.DefaultMaxChunks,
                    "How many pieces of ~1,200 characters a chat is embedded as at most (its start, its end and its summaries are kept first). Only pieces whose text changed are embedded again.", 4, 400) with { Advanced = true },
            ],
        });
        var index = context.Track(new MemoryIndex(context));
        context.Tools.Register(new MemorySearchTool(context, index));
        context.Rpc.Register(new RpcMethod("memory.search", "Past chats closest to a query, by meaning and by its words: { query, limit? (5), projectId?, excludeSessionId? } → { available, meaning, chats: [{ sessionId, title, projectId, updatedAt, score, words, snippet }] }", ReadOnly: true, Params: [RpcParam.Req("query"), RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("projectId"), RpcParam.Opt("excludeSessionId")]),
            async (r, rct) =>
        {
            var query = r.Required("query");
            var found = await index.SearchAsync(query, r.Int("limit") ?? 5, r.Str("projectId"), r.Str("excludeSessionId"), rct).ConfigureAwait(false);
            return new JsonObject { ["available"] = found is not null, ["meaning"] = found?.Meaning ?? false, ["chats"] = MemoryIndex.Json(found?.Hits ?? []) };
        });
        context.Rpc.Register(new RpcMethod("memory.reindex", "Embed the chats that changed since they were last embedded, now: { } → { enabled, indexed?, unchanged?, removed?, skipped?, pieces? }", ReadOnly: false, Params: []),
            async (_, rct) => await index.SweepAsync(rct).ConfigureAwait(false));
        // the host publishes { id } as the payload (no session on the envelope), like sessions.delete does
        context.Track(context.Events.Subscribe(EventTypes.SessionDeleted, e => index.Forget(e.As<JsonObject>()?["id"]?.GetValue<string>() ?? e.SessionId)));
        index.Start();
        return Task.CompletedTask;
    }
}

/// <summary>A chat found: <paramref name="Score"/> is the cosine of its best piece (null when it was found by words only), <paramref name="Words"/> how many of the query's words its best-matching piece holds.</summary>
internal sealed record MemoryHit(string SessionId, string Title, string? ProjectId, string UpdatedAt, double? Score, int Words, string Snippet);

/// <summary>A search's chats, best first; <paramref name="Meaning"/> false when the embedding server could not rank them (words only).</summary>
internal sealed record MemorySearch(List<MemoryHit> Hits, bool Meaning, int Terms);

/// <summary>The chats' embeddings: one document per chat in <c>chats</c>, kept in step by a sweep every few minutes.</summary>
internal sealed class MemoryIndex : IDisposable
{
    public const int DefaultMaxChunks = 64;
    public const int ChunkChars = 1200;
    /// <summary>A message longer than this is cut (a pasted log or file is not a conversation); up to here it is embedded whole, in several pieces.</summary>
    public const int MaxMessageChars = 6000;
    /// <summary>A tool call (its short arguments) or a failed tool's answer as one line of at most this many characters.</summary>
    public const int MaxToolChars = 300;
    /// <summary>Part of every entry's revision: a change in what a chat is made into re-embeds every chat once.</summary>
    private const string Format = "v2";
    /// <summary>Sessions read per page: the store's list has a window (100 by default), so the sweep pages through all of them.</summary>
    public const int SessionPage = 100;
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly IPluginContext _ctx;
    private readonly IDataCollection _store;
    private readonly SemaphoreSlim _sweeping = new(1, 1);
    /// <summary>Chats that failed to index, by the revision that failed: warned once, retried only after the chat changes.</summary>
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Dictionary<string, Entry>? _cache;
    private readonly Dictionary<string, Body> _bodies = new(StringComparer.Ordinal);
    private long _bodyBytes;
    private Timer? _timer;
    /// <summary>The timer's sweep in flight: Dispose waits for it (bounded) so no write lands after the plugin is gone.</summary>
    private Task? _inFlight;

    /// <summary>
    /// Most bytes of chat bodies (texts at 2 bytes a character, vectors at 4 a component) kept in memory: 64 MB. The store is
    /// the index, so a chat whose body did not fit is read from it when a search reaches it, and the memory stays bounded
    /// however many chats there are.
    /// </summary>
    internal const long MaxCachedBytes = 64L * 1024 * 1024;
    internal long CacheBudget { get; set; } = MaxCachedBytes;
    internal long CachedBytes { get { lock (_gate) return _bodyBytes; } }

    /// <summary>What the sweep and the filters need of a chat; always in memory, a few hundred bytes.</summary>
    private sealed record Entry(string Model, string Rev, string Title, string? ProjectId, string UpdatedAt);

    /// <summary>
    /// A chat's pieces: their texts, their content hashes (a piece whose text is unchanged keeps its vector), vectors (none
    /// without an embedding model) and word counts (the word ranking's piece lengths).
    /// </summary>
    private sealed record Body(string[] Texts, string[] Hashes, float[][] Vectors)
    {
        public int[] Lengths { get; } = Texts.Select(Lexical.Count).ToArray();
        public long Bytes => Texts.Sum(t => 2L * t.Length) + Vectors.Sum(v => 4L * v.Length);
    }

    public MemoryIndex(IPluginContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Data.Collection("chats", new CollectionSpec().Text("model").Text("projectId"));
    }

    private bool Enabled => _ctx.Settings.GetOr("memory.enabled", true);

    private IEmbeddingService? Service => Enabled && _ctx.Services.Get<IEmbeddingService>() is { Model: not null } s ? s : null;

    public void Start()
    {
        // The first sweep a little after start (the other plugins come up first), then every five minutes.
        _timer = new Timer(_ => Fire(), null, TimeSpan.FromSeconds(30), Every);
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _inFlight = Task.Run(SweepSafeAsync);
        }
    }

    private async Task SweepSafeAsync()
    {
        try { await SweepAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ctx.Logger.LogInformation("Memory: the chat index was not updated: {Message}", ex.Message); }
    }

    /// <summary>
    /// Index every chat whose messages or model changed since it was indexed; drop the ones that are gone. Without an
    /// embedding model the chats' pieces are kept as text only (the word search still finds them); with a model whose
    /// server is not answering the sweep waits for the next round.
    /// </summary>
    public async Task<JsonObject> SweepAsync(CancellationToken ct)
    {
        if (!Enabled) return new JsonObject { ["enabled"] = false };
        // an unload ends every sweep between two chats, an RPC-started one too (not only the timer's, which has _stop's token)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = linked.Token;
        var service = Service;
        if (service is { Available: false }) return new JsonObject { ["enabled"] = true, ["available"] = false };
        var model = service?.Model ?? "";
        await _sweeping.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = Load();
            var sessions = AllChats();
            var maxChunks = Math.Clamp(_ctx.Settings.GetOr("memory.maxChunksPerChat", DefaultMaxChunks), 4, 400);
            int indexed = 0, unchanged = 0, removed = 0, skipped = 0, pieces = 0;
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var rev = $"{Format}:{session.MessageCount}:{session.UpdatedAt.ToUnixTimeSeconds()}:{maxChunks}";
                if (_failed.TryGetValue(session.Id, out var failedRev) && failedRev == rev) { skipped++; continue; }
                try
                {
                    cache.TryGetValue(session.Id, out var have);
                    // Without a model a chat that did not change keeps the vectors it has, for the day the model is back.
                    if (have is not null && have.Rev == rev && (have.Model == model || service is null)) { unchanged++; continue; }
                    var chunks = Chunks(session, _ctx.Sessions.GetMessages(session.Id), maxChunks);
                    if (chunks.Count == 0) { unchanged++; continue; }
                    // Only the pieces whose text is new are embedded: a chat that grew by a message re-embeds its last piece or two.
                    var hashes = chunks.Select(Hash).ToArray();
                    var known = new Dictionary<string, float[]>(StringComparer.Ordinal);
                    if (have is not null && have.Model == model && BodyOf(session.Id) is { } old)
                        for (var i = 0; i < Math.Min(old.Hashes.Length, old.Vectors.Length); i++) known.TryAdd(old.Hashes[i], old.Vectors[i]);
                    var missing = chunks.Where((_, i) => !known.ContainsKey(hashes[i])).Distinct(StringComparer.Ordinal).ToList();
                    if (service is not null && missing.Count > 0)
                    {
                        var result = await service.EmbedAsync(new EmbeddingRequest { Texts = missing, Background = true }, ct).ConfigureAwait(false);
                        for (var k = 0; k < missing.Count; k++) known[Hash(missing[k])] = result.Vectors[k];
                        pieces += missing.Count;
                    }
                    // Stored under the configured model name: the one the next sweep and the search compare with.
                    var entry = new Entry(model, rev, session.Title, session.ProjectId, session.UpdatedAt.ToString("O"));
                    var body = new Body(chunks.ToArray(), hashes, service is null ? [] : hashes.Select(h => known[h]).ToArray());
                    _store.Put(session.Id, new JsonObject
                    {
                        ["model"] = entry.Model, ["rev"] = rev, ["title"] = entry.Title, ["projectId"] = entry.ProjectId, ["updatedAt"] = entry.UpdatedAt,
                        ["texts"] = new JsonArray(body.Texts.Select(s => (JsonNode)s).ToArray()),
                        ["hashes"] = new JsonArray(body.Hashes.Select(s => (JsonNode)s).ToArray()),
                        ["vectors"] = new JsonArray(body.Vectors.Select(v => (JsonNode)VectorMath.Pack(v)).ToArray()),
                    });
                    Remember(session.Id, entry, body);
                    indexed++;
                    _failed.Remove(session.Id);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A chat that cannot be embedded (unreadable messages, a model that refuses it) is skipped and named:
                    // the sweep keeps going, and the answer says how many were left out.
                    skipped++;
                    _failed[session.Id] = rev;
                    _ctx.Logger.LogWarning("Memory: the chat {Session} \"{Title}\" was not indexed: {Message}", session.Id, session.Title, ex.Message);
                }
            }
            var live = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in cache.Keys.Where(k => !live.Contains(k) && _ctx.Sessions.GetSession(k) is null).ToList())
            {
                Forget(gone);
                removed++;
            }
            if (indexed + removed > 0) _ctx.Logger.LogInformation("Memory: embedded {Indexed} chats ({Pieces} new pieces), removed {Removed} ({Model})", indexed, pieces, removed, model);
            return new JsonObject { ["enabled"] = true, ["model"] = service?.Model, ["indexed"] = indexed, ["unchanged"] = unchanged, ["removed"] = removed, ["skipped"] = skipped, ["pieces"] = pieces };
        }
        finally { _sweeping.Release(); }
    }

    /// <summary>
    /// Every chat, archived ones included, page by page: a list query answers a window (<c>Limit</c>, 100 when unset),
    /// so one unpaged read embeds only the most recently updated chats. A page that brings nothing new ends the walk
    /// (a store that ignores the window would otherwise never end it).
    /// </summary>
    private List<SessionInfo> AllChats()
    {
        var chats = new List<SessionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; ; offset += SessionPage)
        {
            var page = _ctx.Sessions.ListSessions(new SessionQuery { IncludeArchived = true, Offset = offset, Limit = SessionPage });
            var fresh = 0;
            foreach (var s in page)
            {
                if (!seen.Add(s.Id)) continue;
                fresh++;
                if (s.Kind == "chat") chats.Add(s);
            }
            if (page.Count < SessionPage || fresh == 0) break;
        }
        return chats;
    }

    public void Forget(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try { _store.Delete(sessionId); } catch (Exception ex) { _ctx.Logger.LogDebug("Memory: {Session} not removed: {Message}", sessionId, ex.Message); }
        lock (_gate)
        {
            _cache?.Remove(sessionId);
            Unkeep(sessionId);
        }
    }

    /// <summary>Keep a chat just written; its body only while the budget has room (the store has it either way).</summary>
    private void Remember(string sessionId, Entry entry, Body body)
    {
        lock (_gate)
        {
            if (_cache is null) return;   // not loaded yet: the load reads it from the store
            _cache[sessionId] = entry;
            Unkeep(sessionId);
            Keep(sessionId, body);
        }
    }

    private void Keep(string sessionId, Body body)
    {
        var bytes = body.Bytes;
        if (_bodyBytes + bytes > CacheBudget) return;
        _bodies[sessionId] = body;
        _bodyBytes += bytes;
    }

    private void Unkeep(string sessionId)
    {
        if (_bodies.Remove(sessionId, out var old)) _bodyBytes -= old.Bytes;
    }

    /// <summary>A chat's pieces: from memory, else from the store (null when gone or unreadable).</summary>
    private Body? BodyOf(string sessionId)
    {
        lock (_gate)
            if (_bodies.TryGetValue(sessionId, out var kept)) return kept;
        return _store.Get(sessionId) is { } doc ? Parse(doc)?.Body : null;
    }

    /// <summary>
    /// The chats closest to the query: one ranking by meaning (cosine of the chat's best piece), one by words (BM25 of its
    /// best piece: exact identifiers, error codes, file names), fused by reciprocal rank. Without an answering embedding
    /// server it ranks by words alone (<see cref="MemorySearch.Meaning"/> false); null when memory is off or there is
    /// neither a meaning nor a word to search with.
    /// </summary>
    public async Task<MemorySearch?> SearchAsync(string query, int limit, string? projectId, string? excludeSessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || !Enabled) return null;
        float[]? q = null;
        string? model = null;
        if (Service is { Available: true, Model: { } m } service)
        {
            try
            {
                q = (await service.EmbedAsync(new EmbeddingRequest { Texts = [query], Kind = EmbeddingKind.Query }, ct).ConfigureAwait(false)).Vectors[0];
                model = m;
            }
            catch (EmbeddingException ex) { _ctx.Logger.LogDebug("Memory: no meaning search ({Code}): {Message}", ex.Code, ex.Message); }
        }
        var terms = Lexical.Terms(query);
        if (q is null && terms.Count == 0) return null;
        var entries = Load().Where(e => e.Key != excludeSessionId && (projectId is not { Length: > 0 } || e.Value.ProjectId == projectId)).ToList();

        var meaning = new Dictionary<string, (double Score, int At)>(StringComparer.Ordinal);
        if (q is not null)
            foreach (var (id, entry) in entries)
            {
                if (entry.Model != model || BodyOf(id) is not { Vectors.Length: > 0 } body) continue;
                var best = float.MinValue;
                var at = 0;
                for (var i = 0; i < body.Vectors.Length; i++)
                {
                    var s = VectorMath.Dot(q, body.Vectors[i]);
                    if (s > best) { best = s; at = i; }
                }
                meaning[id] = (best, at);
            }
        // one body at a time: the ranking reads each chat once, so a store-read body is let go as soon as it is counted
        var words = Lexical.Rank(terms, entries.Select(e => BodyOf(e.Key) is { } b ? (e.Key, b.Texts, b.Lengths) : (e.Key, Array.Empty<string>(), Array.Empty<int>())));

        var fused = Lexical.Fuse(Lexical.Ranks(meaning.OrderByDescending(x => x.Value.Score).Select(x => x.Key)), Lexical.Ranks(words));
        var byId = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var lexical = words.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var hits = new List<MemoryHit>();
        foreach (var (id, _) in fused.Take(Math.Clamp(limit, 1, 20)))
        {
            var entry = byId[id];
            var texts = BodyOf(id)?.Texts ?? [];
            double? score = meaning.TryGetValue(id, out var mm) ? Math.Round(mm.Score, 3) : null;
            var snippet = lexical.TryGetValue(id, out var w)
                ? Lexical.Window(texts[w.At], terms, 400)
                : Snippet(texts.ElementAtOrDefault(mm.At));
            hits.Add(new MemoryHit(id, entry.Title, entry.ProjectId, entry.UpdatedAt, score, w?.Words ?? 0, snippet));
        }
        return new MemorySearch(hits, q is not null, terms.Count);
    }

    /// <summary>
    /// A chat as pieces to embed: the title with its first request, then its compaction summaries, then the rest of the
    /// conversation — user and agent text (each message whole up to <see cref="MaxMessageChars"/>), the subagents'
    /// reports, every tool call with its short arguments (commands, paths, queries) and the text of failed tools — the
    /// first and last pieces kept when it has to be cut. Pieces are packed from the start, so a chat that grows changes
    /// only its last piece and the others keep their vectors.
    /// </summary>
    internal static List<string> Chunks(SessionInfo session, IReadOnlyList<ChatMessage> messages, int maxChunks)
    {
        var summaries = new List<string>();
        var lines = new List<string>();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.Summary: Split(summaries, "Summary: ", m.Text); break;
                case MessageRole.User: Split(lines, "User: ", m.Text); break;
                case MessageRole.Assistant:
                    Split(lines, "Agent: ", m.Text);
                    foreach (var call in m.ToolCalls) lines.Add(CallLine(call));
                    break;
                case MessageRole.Tool:
                    foreach (var r in m.ToolResults)
                        if (r.IsError && r.Content.Trim() is { Length: > 0 } error) lines.Add(Clip($"Tool error ({r.Name}): {OneLine(error)}", MaxToolChars));
                    break;
                case MessageRole.Notice when m.MetaString("kind") is "agent-result" or "agent-message":
                    Split(lines, "Agent report: ", m.Text);
                    break;
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

    /// <summary>A message as lines of at most <see cref="ChunkChars"/>, cut at a space or line break where one is near.</summary>
    private static void Split(List<string> into, string label, string text)
    {
        text = Clip(text.Trim(), MaxMessageChars);
        var room = ChunkChars - label.Length;
        while (text.Length > 0)
        {
            var cut = text.Length;
            if (cut > room)
            {
                var space = text.LastIndexOfAny([' ', '\n'], room - 1, room / 2);
                cut = space > 0 ? space : room;
            }
            into.Add(label + text[..cut].TrimEnd());
            text = text[cut..].TrimStart();
        }
    }

    /// <summary>A tool call as one line: its name and its short string arguments (a file's new content or a long script is left out).</summary>
    private static string CallLine(ToolCallPart call)
    {
        var args = new List<string>();
        try
        {
            // JsonDocument, not JsonNode: models sometimes emit an object with a repeated key, which JsonNode rejects.
            using var doc = JsonDocument.Parse(call.Arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim() is { Length: > 0 and <= 200 } t) args.Add(OneLine(t));
        }
        catch (JsonException) { }
        return Clip(args.Count == 0 ? $"Tool {call.Name}" : $"Tool {call.Name}: {string.Join(" ", args)}", MaxToolChars);
    }

    private static string OneLine(string s) => s.Replace("\r", "").Replace('\n', ' ').Trim();

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static string Snippet(string? text) => text is null ? "" : Clip(text, 400);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 12);

    private Dictionary<string, Entry> Load()
    {
        lock (_gate)
        {
            if (_cache is not null) return new Dictionary<string, Entry>(_cache, StringComparer.Ordinal);
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var doc in _store.Find())
            {
                if (Parse(doc.Doc) is not { } chat) continue;
                map[doc.Key] = chat.Entry;
                Keep(doc.Key, chat.Body);
            }
            _cache = map;
            return new Dictionary<string, Entry>(map, StringComparer.Ordinal);
        }
    }

    /// <summary>A stored chat; null when a vector cannot be read (the sweep then indexes the chat again).</summary>
    private static (Entry Entry, Body Body)? Parse(JsonObject doc)
    {
        var vectors = (doc["vectors"] as JsonArray ?? [])
            .Select(v => v is JsonValue jv && jv.TryGetValue<string>(out var packed) ? VectorMath.Unpack(packed) : null).ToArray();
        if (vectors.Any(v => v is null)) return null;
        var entry = new Entry(Str(doc["model"]), Str(doc["rev"]), Str(doc["title"]), doc["projectId"] is JsonValue p && p.TryGetValue<string>(out var pid) ? pid : null, Str(doc["updatedAt"]));
        return (entry, new Body(Strings(doc["texts"]), Strings(doc["hashes"]), vectors!));
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string[] Strings(JsonNode? n) => (n as JsonArray ?? []).Select(Str).ToArray();

    public static JsonArray Json(IEnumerable<MemoryHit> hits) => new(hits.Select(h => (JsonNode)new JsonObject
    {
        ["sessionId"] = h.SessionId, ["title"] = h.Title, ["projectId"] = h.ProjectId, ["updatedAt"] = h.UpdatedAt, ["score"] = h.Score, ["words"] = h.Words, ["snippet"] = h.Snippet,
    }).ToArray());

    public void Dispose()
    {
        Task? sweep;
        lock (_gate)
        {
            _stop.Cancel();
            sweep = _inFlight;
        }
        _timer?.Dispose();
        // A sweep writes to the chats collection; give it a moment to finish instead of racing the unload.
        try { sweep?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
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
        Description = "Find earlier chats about something by meaning and by exact words (\"have we done this before?\", \"how did we fix X\", an error code, a file or symbol name): the closest chats with a snippet.",
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
        var found = await index.SearchAsync(query, a.Int("limit") ?? 5, projectId, context.SessionId, ct).ConfigureAwait(false);
        if (found is null) return ToolResult.Error("Memory search is not available: memory is off (memory.enabled), or the embedding server is not answering and the query has no word to search for.");
        var hits = found.Hits;
        var scope = projectId is null ? "." : " in this project (project \"all\" searches every project).";
        if (hits.Count == 0)
            return ToolResult.Ok(found.Meaning ? "No earlier chats are indexed yet" + scope : "No earlier chat holds these words" + scope + " (no meaning search: no embedding model, or its server is not answering.)");
        var text = new StringBuilder(found.Meaning
            ? $"{hits.Count} earlier chats, ranked by meaning and by the query's words together (score = cosine, below about 0.6 probably not about it; words = how many of the {found.Terms} search words one piece holds):\n"
            : $"{hits.Count} earlier chats holding the query's words (words only: no embedding model, or its server is not answering):\n");
        foreach (var h in hits)
        {
            text.Append("- ").Append(h.SessionId).Append(" \"").Append(h.Title).Append("\" (").Append(h.UpdatedAt[..Math.Min(10, h.UpdatedAt.Length)]);
            if (h.Score is { } score) text.Append(", ").Append(score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (h.Words > 0) text.Append(", ").Append(h.Words).Append('/').Append(found.Terms).Append(" words");
            text.Append("): ").Append(h.Snippet.Replace('\n', ' ')).Append('\n');
        }
        return ToolResult.Ok(text.ToString().TrimEnd(), new JsonObject { ["chats"] = MemoryIndex.Json(hits) });
    }
}
