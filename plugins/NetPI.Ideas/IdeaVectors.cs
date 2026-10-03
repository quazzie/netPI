using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>An idea and how close it is to a text (the cosine of the best of its chunks).</summary>
public sealed record IdeaScore(JsonObject Idea, double Score);

/// <summary>
/// The backlog's embeddings (idea-61wg9p): one document per idea in the <c>vectors</c> collection — the model, a hash of
/// the embedded text, and the vectors of its chunks (the card, then the sections in pieces) — kept in step with the
/// backlog in the background and searched in process. It only shortlists: whatever reads a score still decides with the
/// rules it had (the pick-or-none decision, the user, the agent). With no <see cref="IEmbeddingService"/>, embeddings
/// switched off or the server failing, every method answers "nothing" fast and the callers behave as before.
/// </summary>
public sealed class IdeaVectors : IDisposable
{
    /// <summary>A section is embedded in pieces of this many characters, each carrying the idea's title.</summary>
    public const int ChunkChars = 1500;
    /// <summary>At most this many chunks per idea, so one huge idea cannot dominate the index or a backfill.</summary>
    public const int MaxChunks = 12;
    /// <summary>How long a backlog write waits before the index follows it, so a burst of edits is one pass.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    /// <summary>Cosine (bge-base, card to card) above which an idea is called similar: 0.80 is about the top 7 % of each
    /// idea's nearest other idea in the 231-idea backlog; the known duplicate pair scored 0.807-0.824 (decisions-lab nuc-plan.md).</summary>
    public const double DefaultSimilarThreshold = 0.80;

    private readonly IPluginContext _ctx;
    private readonly IdeasRepository _repo;
    private readonly IDataCollection _store;
    private readonly SemaphoreSlim _indexing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Dictionary<string, Entry>? _cache;
    private Timer? _debounce;

    private sealed record Entry(string Model, string Hash, float[][] Chunks);

    public IdeaVectors(IPluginContext ctx, IdeasRepository repo)
    {
        _ctx = ctx;
        _repo = repo;
        _store = ctx.Data.Collection("vectors", new CollectionSpec().Text("model"));
    }

    private IEmbeddingService? Service =>
        _ctx.Settings.GetOr("ideas.semantic", true) && _ctx.Services.Get<IEmbeddingService>() is { Model: not null } s ? s : null;

    /// <summary>Embeddings are configured, switched on for ideas and not backing off.</summary>
    public bool Available => Service is { Available: true };

    /// <summary>The backlog changed: re-index after <see cref="Debounce"/> (a burst of writes is one pass).</summary>
    public void Changed()
    {
        if (Service is null) return;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _debounce ??= new Timer(_ => _ = Task.Run(() => ReindexSafeAsync()), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReindexSafeAsync()
    {
        try { await ReindexAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ctx.Logger.LogInformation("Ideas: the embedding index was not updated: {Message}", ex.Message); }
    }

    /// <summary>
    /// Bring the index in step with the backlog: embed every idea whose text or model changed, drop the vectors of ideas
    /// that are gone. Returns what it did; throws <see cref="EmbeddingException"/> when the server fails (the next change
    /// or search tries again).
    /// </summary>
    public async Task<JsonObject> ReindexAsync(CancellationToken ct)
    {
        var service = Service;
        if (service?.Model is not { } model) return new JsonObject { ["enabled"] = false };
        await _indexing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = Load();
            var ideas = _repo.All();
            var wanted = new List<(string Id, string Hash, List<string> Chunks)>();
            var skipped = 0;
            foreach (var idea in ideas)
            {
                if (IdeaOps.Str(idea["id"]) is not { Length: > 0 } id) continue;
                var chunks = Chunks(idea);
                var hash = Hash(model, chunks);
                if (cache.TryGetValue(id, out var have) && have.Model == model && have.Hash == hash) { skipped++; continue; }
                wanted.Add((id, hash, chunks));
            }
            var ids = ideas.Select(i => IdeaOps.Str(i["id"])).Where(i => i is { Length: > 0 }).ToHashSet(StringComparer.Ordinal);
            var removed = 0;
            foreach (var gone in cache.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _store.Delete(gone);
                lock (_gate) _cache?.Remove(gone);
                removed++;
            }
            var indexed = 0;
            // A few ideas per request keeps every request well inside the background timeout.
            foreach (var group in wanted.Chunk(8))
            {
                var texts = group.SelectMany(g => g.Chunks).ToList();
                var result = await service.EmbedAsync(new EmbeddingRequest { Texts = texts, Kind = EmbeddingKind.Document, Background = true }, ct).ConfigureAwait(false);
                var at = 0;
                foreach (var (id, hash, chunks) in group)
                {
                    var vectors = result.Vectors.Skip(at).Take(chunks.Count).ToArray();
                    at += chunks.Count;
                    _store.Put(id, new JsonObject
                    {
                        ["model"] = result.Model, ["dim"] = result.Dimensions, ["hash"] = hash, ["at"] = IdeaOps.Now(),
                        ["chunks"] = new JsonArray(vectors.Select(v => (JsonNode)VectorMath.Pack(v)).ToArray()),
                    });
                    lock (_gate) if (_cache is not null) _cache[id] = new Entry(result.Model, hash, vectors);
                    indexed++;
                }
            }
            if (indexed + removed > 0) _ctx.Logger.LogDebug("Ideas: embedded {Indexed} ideas, removed {Removed} ({Model})", indexed, removed, model);
            return new JsonObject { ["enabled"] = true, ["model"] = model, ["indexed"] = indexed, ["unchanged"] = skipped, ["removed"] = removed };
        }
        finally { _indexing.Release(); }
    }

    /// <summary>
    /// The candidates closest to <paramref name="text"/>, best first, at most <paramref name="limit"/>; null when there is
    /// nothing to rank with (no embeddings, a failing server, no candidate indexed yet). A candidate that is not indexed for
    /// the current model is left out and a re-index is started for it.
    /// </summary>
    public async Task<List<IdeaScore>?> NearestAsync(string text, IReadOnlyList<JsonObject> candidates, int limit, CancellationToken ct,
        EmbeddingKind kind = EmbeddingKind.Query, bool cardsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates.Count == 0 || limit <= 0) return null;
        if (Service is not { Available: true, Model: { } model } service) return null;
        float[] query;
        try { query = (await service.EmbedAsync(new EmbeddingRequest { Texts = [text], Kind = kind }, ct).ConfigureAwait(false)).Vectors[0]; }
        catch (EmbeddingException ex)
        {
            _ctx.Logger.LogDebug("Ideas: no meaning search ({Code}): {Message}", ex.Code, ex.Message);
            return null;
        }
        var cache = Load();
        var scored = new List<IdeaScore>();
        var missing = false;
        foreach (var idea in candidates)
        {
            if (IdeaOps.Str(idea["id"]) is not { Length: > 0 } id || !cache.TryGetValue(id, out var entry) || entry.Model != model) { missing = true; continue; }
            var best = float.MinValue;
            foreach (var chunk in cardsOnly ? entry.Chunks.Take(1) : entry.Chunks) best = Math.Max(best, VectorMath.Dot(query, chunk));
            if (best > float.MinValue) scored.Add(new IdeaScore(idea, Math.Round(best, 3)));
        }
        if (missing) Changed();
        return scored.Count == 0 ? null : scored.OrderByDescending(s => s.Score).Take(limit).ToList();
    }

    /// <summary>
    /// The candidates in the order a pick-one decision should see them when there are more than one decision can offer
    /// (<see cref="IdeaMatch.MaxOptions"/>): the word ranking and the meaning ranking fused (reciprocal rank, k = 60), so an
    /// idea described in other words is no longer stuck behind fifty that share a word with the text. Null when nothing
    /// needs ranking or there are no embeddings: the caller keeps <see cref="IdeaMatch.Ranked"/>.
    /// </summary>
    public async Task<List<JsonObject>?> RankAsync(string text, IReadOnlyList<JsonObject> candidates, CancellationToken ct)
    {
        if (candidates.Count <= IdeaMatch.MaxOptions) return null;
        var near = await NearestAsync(text, candidates, candidates.Count, ct).ConfigureAwait(false);
        if (near is null) return null;
        var words = IdeaMatch.Ranked(candidates, text);
        var score = new Dictionary<JsonObject, double>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < words.Count; i++) score[words[i]] = 1.0 / (60 + i + 1);
        for (var i = 0; i < near.Count; i++) score[near[i].Idea] = score.GetValueOrDefault(near[i].Idea) + 1.0 / (60 + i + 1);
        return words.OrderByDescending(i => score.GetValueOrDefault(i)).ToList();
    }

    /// <summary>The ideas the new one resembles (card to card, at or above <c>ideas.similarThreshold</c>), best first.</summary>
    public async Task<List<IdeaScore>> SimilarToAsync(JsonObject idea, int limit, CancellationToken ct)
    {
        var id = IdeaOps.Str(idea["id"]);
        var others = _repo.All().Where(i => IdeaOps.Str(i["id"]) != id).ToList();
        var near = await NearestAsync(Card(idea), others, limit, ct, EmbeddingKind.Document, cardsOnly: true).ConfigureAwait(false);
        var threshold = Math.Clamp(_ctx.Settings.GetOr("ideas.similarThreshold", DefaultSimilarThreshold), 0.5, 0.99);
        return near?.Where(s => s.Score >= threshold).ToList() ?? [];
    }

    /// <summary>The texts an idea is embedded as: the card (title, summary, tags) first, then its sections in pieces.</summary>
    public static List<string> Chunks(JsonObject idea)
    {
        var chunks = new List<string> { Card(idea) };
        var title = IdeaOps.Str(idea["title"]) ?? "";
        foreach (var section in (idea["sections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var body = $"{IdeaOps.Str(section["title"])}\n{IdeaOps.Str(section["content"])}".Trim();
            for (var at = 0; at < body.Length && chunks.Count < MaxChunks; at += ChunkChars)
                chunks.Add($"{title}\n{body.Substring(at, Math.Min(ChunkChars, body.Length - at))}");
        }
        return chunks;
    }

    public static string Card(JsonObject idea)
    {
        var tags = IdeaOps.ParseTags(idea["tags"]);
        return $"{IdeaOps.Str(idea["title"])}\n{IdeaOps.Str(idea["summary"])}" + (tags.Count > 0 ? $"\nTags: {string.Join(", ", tags)}" : "");
    }

    private static string Hash(string model, List<string> chunks) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\u0000" + string.Join("\u0000", chunks))))[..32];

    /// <summary>The stored vectors, read once and then kept in step by the writes above.</summary>
    private Dictionary<string, Entry> Load()
    {
        lock (_gate)
        {
            if (_cache is not null) return new Dictionary<string, Entry>(_cache, StringComparer.Ordinal);
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var doc in _store.Find())
            {
                var chunks = (doc.Doc["chunks"] as JsonArray ?? []).Select(c => VectorMath.Unpack(IdeaOps.Str(c))).ToArray();
                if (chunks.Length == 0 || chunks.Any(c => c is null)) continue;
                map[doc.Key] = new Entry(IdeaOps.Str(doc.Doc["model"]) ?? "", IdeaOps.Str(doc.Doc["hash"]) ?? "", chunks!);
            }
            _cache = map;
            return new Dictionary<string, Entry>(map, StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
