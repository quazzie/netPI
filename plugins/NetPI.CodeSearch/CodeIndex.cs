using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.CodeSearch;

/// <summary>A passage of an indexed file, with its words for the ranking (path, signature and text, as <see cref="Lexical.CodeTokens"/>).</summary>
internal sealed class Passage(string path, Chunk chunk, string hash, string[] terms, int[] tf, int length, int fileBytes)
{
    public string Path { get; } = path;
    public Chunk Chunk { get; } = chunk;
    /// <summary>A hash of what is embedded (path, signature, text): the vector's key, shared by every workspace holding the same passage.</summary>
    public string Hash { get; } = hash;
    public string[] Terms { get; } = terms;
    public int[] Tf { get; } = tf;
    public int Length { get; } = length;
    public int FileBytes { get; } = fileBytes;
}

/// <summary>A file as indexed: what it was when it was read (size, write time, content hash) and its passages.</summary>
internal sealed record IndexedFile(string Path, long Size, long WriteTicks, string Hash, List<Passage> Passages);

/// <summary>What a search sees: the passages and their inverted index, immutable, swapped whole after a refresh.</summary>
internal sealed class Snapshot
{
    public static readonly Snapshot Empty = new([]);

    public Passage[] Passages { get; }
    public Dictionary<string, (int[] Ids, int[] Tf)> Postings { get; }
    public double AvgLength { get; }
    public long TextChars { get; }

    public Snapshot(Passage[] passages)
    {
        Passages = passages;
        var postings = new Dictionary<string, (List<int> Ids, List<int> Tf)>(StringComparer.Ordinal);
        long words = 0;
        for (var id = 0; id < passages.Length; id++)
        {
            var p = passages[id];
            words += p.Length;
            TextChars += p.Chunk.Text.Length;
            for (var k = 0; k < p.Terms.Length; k++)
            {
                if (!postings.TryGetValue(p.Terms[k], out var list)) postings[p.Terms[k]] = list = ([], []);
                list.Ids.Add(id);
                list.Tf.Add(p.Tf[k]);
            }
        }
        Postings = postings.ToDictionary(x => x.Key, x => (x.Value.Ids.ToArray(), x.Value.Tf.ToArray()), StringComparer.Ordinal);
        AvgLength = Math.Max(1.0, (double)words / Math.Max(1, passages.Length));
    }

    public long PostingCount => Postings.Values.Sum(p => (long)p.Ids.Length);
}

/// <summary>One workspace root: its files, the snapshot searches read, the refresh that keeps it in step and the counts the status shows.</summary>
internal sealed class RootIndex(string root) : IDisposable
{
    public string Root { get; } = root;
    public readonly SemaphoreSlim Refreshing = new(1, 1);
    public readonly CancellationTokenSource Stop = new();
    public readonly Dictionary<string, IndexedFile> Files = new(StringComparer.Ordinal);
    /// <summary>The content hash each file had when it was last stored (the persistent index), read once per process.</summary>
    public Dictionary<string, string>? Stored;
    public volatile Snapshot Snapshot = Snapshot.Empty;
    public Task? Refresh;
    public Task? Embedding;
    public DateTime LastRefresh = DateTime.MinValue;
    public DateTime LastUsed = DateTime.UtcNow;
    /// <summary>The first full build has finished (until then answers come from what is indexed so far).</summary>
    public volatile bool Built;
    public volatile int Listed, Done;
    public bool Git, Capped;
    public int SkippedLarge, SkippedBinary, SkippedGenerated;
    public long FirstBuildMs = -1, LastRefreshMs;
    public int LastChanged, LastRemoved;
    public string? Error;
    public volatile int EmbedPending;
    public string? EmbedError;

    public void Dispose()
    {
        Stop.Cancel();
    }
}

/// <summary>
/// The code index of every workspace searched since the start, kept in memory (BM25 over an inverted index, cosine over
/// the passages' vectors) and persisted through <c>ctx.Data</c>:
/// <list type="bullet">
/// <item><c>files</c>: one document per workspace file, <c>{ root, path, hash, chunks: [passage hashes] }</c> — what the
/// index held, so a restart knows which files changed and which vectors are still used;</item>
/// <item><c>vectors</c>: one per passage and embedding model, <c>{ model, v }</c> under <c>model:hash</c>, shared by every
/// workspace holding the same passage (a worktree of the same repository embeds nothing new);</item>
/// <item><c>roots</c>: <c>{ root, lastUsed }</c> per workspace, so a removed worktree's documents are dropped;</item>
/// <item><c>stats</c>: <c>totals</c>, <c>{ queries, returnedTokens, fileTokens, ms }</c> over every search.</item>
/// </list>
/// The text itself is read from the files at the first search of a process (a few seconds for a repository of a
/// thousand files), never stored.
/// </summary>
internal sealed class CodeIndex : IDisposable
{
    public const int DefaultMaxFiles = 20_000;
    public const int DefaultMaxFileKb = 512;
    public const int DefaultMaxChunkLines = 80;
    /// <summary>How many workspaces are held in memory; one more drops the least recently searched.</summary>
    public const int MaxRoots = 6;
    /// <summary>A search re-checks the files (a listing and a stat each: tens of milliseconds) when the last check is older than this.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(3);
    /// <summary>A workspace not searched for this long (or whose folder is gone) loses its documents at the next sweep.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    /// <summary>The longest text embedded for a passage: the start of it (path, signature, first lines) is what it is about.</summary>
    public const int EmbedChars = 1500;
    private const int EmbedBatch = 64;
    /// <summary>During the first build the snapshot is published this often, so searches answer from what is indexed so far.</summary>
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(1);

    private readonly IPluginContext _ctx;
    private readonly IDataCollection _files, _vectors, _roots, _stats;
    private readonly ConcurrentDictionary<string, RootIndex> _byRoot = new(WorkspacePaths.Comparer);
    private readonly ConcurrentDictionary<string, float[]> _vectorCache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _statsGate = new();
    private string? _cacheModel;
    private Timer? _sweep;

    public CodeIndex(IPluginContext ctx)
    {
        _ctx = ctx;
        _files = ctx.Data.Collection("files", new CollectionSpec().Text("root").Text("hash"));
        _vectors = ctx.Data.Collection("vectors", new CollectionSpec().Text("model"));
        _roots = ctx.Data.Collection("roots", new CollectionSpec().Integer("lastUsed"));
        _stats = ctx.Data.Collection("stats", new CollectionSpec());
    }

    public bool Enabled => _ctx.Settings.GetOr("codesearch.enabled", true);
    private int MaxFiles => Math.Clamp(_ctx.Settings.GetOr("codesearch.maxFiles", DefaultMaxFiles), 100, 200_000);
    private long MaxFileBytes => Math.Clamp(_ctx.Settings.GetOr("codesearch.maxFileKb", DefaultMaxFileKb), 16, 8192) * 1024L;
    private int MaxChunkLines => Math.Clamp(_ctx.Settings.GetOr("codesearch.maxChunkLines", DefaultMaxChunkLines), 20, 400);
    private int MaxChunkChars => MaxChunkLines * 100;

    /// <summary>The embedding service when passages are embedded (the setting is on and a model is configured).</summary>
    private IEmbeddingService? Service =>
        _ctx.Settings.GetOr("codesearch.embed", true) && _ctx.Services.Get<IEmbeddingService>() is { Model: not null } s ? s : null;

    /// <summary>The sweep of forgotten workspaces and unused vectors: a few minutes after the start, then twice a day.</summary>
    public void Start() => _sweep = new Timer(_ => _ = Task.Run(SweepSafeAsync), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(12));

    public static string Key(string root) => WorkspacePaths.Canonical(root);

    public RootIndex Get(string root)
    {
        root = Key(root);
        var index = _byRoot.GetOrAdd(root, r => new RootIndex(r));
        index.LastUsed = DateTime.UtcNow;
        if (_byRoot.Count > MaxRoots)
            foreach (var old in _byRoot.Values.Where(r => r != index && r.Refresh is not { IsCompleted: false }).OrderBy(r => r.LastUsed).Take(_byRoot.Count - MaxRoots).ToList())
                if (_byRoot.TryRemove(old.Root, out var gone)) gone.Dispose();
        return index;
    }

    public IReadOnlyCollection<RootIndex> Loaded => _byRoot.Values.OrderBy(r => r.Root, StringComparer.Ordinal).ToList();

    /// <summary>
    /// A refresh of the root when its last check is older than <see cref="Freshness"/> (or <paramref name="force"/>), else the
    /// one running, else none. The refresh runs on the root's own token, so a search that stops waiting does not stop it.
    /// </summary>
    public Task EnsureFresh(RootIndex index, bool force = false)
    {
        lock (index)
        {
            if (index.Refresh is { IsCompleted: false } running) return running;
            if (!force && DateTime.UtcNow - index.LastRefresh < Freshness) return Task.CompletedTask;
            return index.Refresh = Task.Run(() => RefreshAsync(index, index.Stop.Token));
        }
    }

    /// <summary>Forget everything held for the root (memory and its file documents): the next search builds it anew.</summary>
    public void Forget(string root)
    {
        root = Key(root);
        if (_byRoot.TryRemove(root, out var index)) index.Dispose();
        _files.DeleteWhere(new DataQuery().Eq("root", root));
    }

    /// <summary>
    /// List the root's files and re-read the ones whose size or write time changed; a file whose content hash is unchanged
    /// keeps its passages. Deleted files are dropped. During the first build the snapshot is published every few hundred
    /// files, so a search can answer from what is indexed so far.
    /// </summary>
    private async Task RefreshAsync(RootIndex index, CancellationToken ct)
    {
        await index.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try
        {
            if (!Directory.Exists(index.Root)) throw new DirectoryNotFoundException($"The workspace folder is gone: {index.Root}");
            index.Stored ??= _files.Find(new DataQuery().Eq("root", index.Root))
                .ToDictionary(d => d.Doc["path"]?.GetValue<string>() ?? "", d => d.Doc["hash"]?.GetValue<string>() ?? "", StringComparer.Ordinal);
            TouchRoot(index.Root);
            var (listed, git, capped) = await FileSource.ListAsync(index.Root, MaxFiles, ct).ConfigureAwait(false);
            index.Listed = listed.Count;
            index.Git = git;
            index.Capped = capped;
            int changed = 0, large = 0, binary = 0, generated = 0, done = 0;
            var maxBytes = MaxFileBytes;
            var (maxLines, maxChars) = (MaxChunkLines, MaxChunkChars);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dirty = false;
            var published = watch.Elapsed;
            foreach (var rel in listed)
            {
                ct.ThrowIfCancellationRequested();
                index.Done = ++done;
                var info = new FileInfo(Path.Combine(index.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
                try
                {
                    // a link may lead out of the workspace: never read through one
                    if (!info.Exists || info.LinkTarget is not null) continue;
                    if (info.Length > maxBytes) { large++; continue; }
                    seen.Add(rel);
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    index.Files.TryGetValue(rel, out var have);
                    if (have is not null && have.Size == info.Length && have.WriteTicks == ticks) continue;
                    var bytes = await File.ReadAllBytesAsync(info.FullName, ct).ConfigureAwait(false);
                    if (FileSource.IsBinary(bytes)) { binary++; seen.Remove(rel); Drop(index, rel); continue; }
                    var hash = Hash(bytes);
                    if (have is not null && have.Hash == hash) { index.Files[rel] = have with { Size = info.Length, WriteTicks = ticks }; continue; }
                    var text = Encoding.UTF8.GetString(bytes);
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                    if (FileSource.IsGenerated(text)) { generated++; seen.Remove(rel); Drop(index, rel); continue; }
                    var passages = Chunker.Split(rel, text, maxLines, maxChars).Select(c => Make(rel, c, bytes.Length)).ToList();
                    index.Files[rel] = new IndexedFile(rel, info.Length, ticks, hash, passages);
                    if (!index.Stored.TryGetValue(rel, out var stored) || stored != hash)
                    {
                        _files.Put(DocKey(index.Root, rel), new JsonObject
                        {
                            ["root"] = index.Root, ["path"] = rel, ["hash"] = hash,
                            ["chunks"] = new JsonArray(passages.Select(p => (JsonNode)p.Hash).ToArray()),
                        });
                        index.Stored[rel] = hash;
                    }
                    changed++;
                    dirty = true;
                    if (!index.Built && watch.Elapsed - published > PublishEvery)
                    {
                        Publish(index);
                        published = watch.Elapsed;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    seen.Remove(rel); // unreadable now (locked, gone): left out until it can be read
                }
            }
            var removed = 0;
            foreach (var gone in index.Files.Keys.Where(k => !seen.Contains(k)).ToList()) { Drop(index, gone); removed++; dirty = true; }
            foreach (var gone in index.Stored.Keys.Where(k => !seen.Contains(k)).ToList()) { _files.Delete(DocKey(index.Root, gone)); index.Stored.Remove(gone); }
            if (dirty || !index.Built) Publish(index);
            (index.SkippedLarge, index.SkippedBinary, index.SkippedGenerated) = (large, binary, generated);
            (index.LastChanged, index.LastRemoved) = (changed, removed);
            index.LastRefreshMs = watch.ElapsedMilliseconds;
            if (!index.Built)
            {
                index.FirstBuildMs = watch.ElapsedMilliseconds;
                _ctx.Logger.LogInformation("Code search: indexed {Root}: {Files} files, {Passages} passages in {Ms} ms", index.Root, index.Files.Count, index.Snapshot.Passages.Length, watch.ElapsedMilliseconds);
            }
            index.Built = true;
            index.Error = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            index.Error = ex.Message;
            _ctx.Logger.LogWarning("Code search: {Root} was not indexed: {Message}", index.Root, ex.Message);
        }
        finally
        {
            index.LastRefresh = DateTime.UtcNow;
            index.Refreshing.Release();
        }
        StartEmbedding(index);
    }

    private static void Drop(RootIndex index, string rel) => index.Files.Remove(rel);

    private static void Publish(RootIndex index) =>
        index.Snapshot = new Snapshot(index.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).SelectMany(f => f.Passages).ToArray());

    private static Passage Make(string rel, Chunk chunk, int fileBytes)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var t in Lexical.CodeTokens(rel + "\n" + chunk.Signature + "\n" + chunk.Text))
        {
            tf[t] = tf.GetValueOrDefault(t) + 1;
            length++;
        }
        return new Passage(rel, chunk, Hash(Encoding.UTF8.GetBytes(EmbedText(rel, chunk))), tf.Keys.ToArray(), tf.Values.ToArray(), length, fileBytes);
    }

    /// <summary>What a passage is embedded as: its path, its signature and the start of its text.</summary>
    public static string EmbedText(string rel, Chunk chunk)
    {
        var text = $"{rel}\n{chunk.Signature}\n{chunk.Text}";
        return text.Length > EmbedChars ? text[..EmbedChars] : text;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes), 0, 12);

    private static string DocKey(string root, string rel) => root + "\n" + rel;

    private static string VectorKey(string model, string hash) => model + ":" + hash;

    private void TouchRoot(string root)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var have = _roots.Get(root);
        if (have?["lastUsed"]?.GetValue<long>() is { } last && now - last < 3600) return;
        _roots.Put(root, new JsonObject { ["root"] = root, ["lastUsed"] = now });
    }

    // ------------------------------------------------------------------ embedding

    private void StartEmbedding(RootIndex index)
    {
        if (Service is null) return;
        lock (index)
        {
            if (index.Embedding is { IsCompleted: false }) return;
            index.Embedding = Task.Run(() => EmbedSafeAsync(index, index.Stop.Token));
        }
    }

    private async Task EmbedSafeAsync(RootIndex index, CancellationToken ct)
    {
        try { await EmbedAsync(index, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            index.EmbedError = ex.Message;
            _ctx.Logger.LogInformation("Code search: passages of {Root} were not embedded: {Message}", index.Root, ex.Message);
        }
    }

    /// <summary>The model the vector cache holds; a change of model empties it (vectors of two models are not comparable).</summary>
    private string? CacheModel(string? model)
    {
        lock (_vectorCache)
        {
            if (model is not null && model != _cacheModel) { _vectorCache.Clear(); _cacheModel = model; }
            return _cacheModel;
        }
    }

    /// <summary>
    /// Give every passage of the snapshot a vector: from the cache, else from the stored vectors (another workspace or an
    /// earlier run embedded the same passage), else from the embedding server in batches in the background. A failure stops
    /// the round; the next refresh tries again.
    /// </summary>
    public async Task EmbedAsync(RootIndex index, CancellationToken ct)
    {
        var service = Service;
        if (service?.Model is not { } model) return;
        CacheModel(model);
        index.EmbedError = null;
        var missing = new List<Passage>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in index.Snapshot.Passages)
        {
            var key = VectorKey(model, p.Hash);
            if (_vectorCache.ContainsKey(key) || !queued.Add(p.Hash)) continue;
            if (VectorMath.Unpack(_vectors.Get(key)?["v"]?.GetValue<string>()) is { } stored) _vectorCache[key] = stored;
            else missing.Add(p);
        }
        index.EmbedPending = missing.Count;
        for (var at = 0; at < missing.Count; at += EmbedBatch)
        {
            ct.ThrowIfCancellationRequested();
            if (!service.Available) { index.EmbedError = "the embedding server is not answering (backing off)"; return; }
            var batch = missing.Skip(at).Take(EmbedBatch).ToList();
            var result = await service.EmbedAsync(new EmbeddingRequest { Texts = batch.Select(p => EmbedText(p.Path, p.Chunk)).ToList(), Background = true }, ct).ConfigureAwait(false);
            if (CacheModel(null) != model) return; // the model changed under the batch
            for (var k = 0; k < batch.Count; k++)
            {
                var key = VectorKey(model, batch[k].Hash);
                _vectorCache[key] = result.Vectors[k];
                _vectors.Put(key, new JsonObject { ["model"] = model, ["v"] = VectorMath.Pack(result.Vectors[k]) });
            }
            index.EmbedPending = Math.Max(0, missing.Count - at - batch.Count);
        }
    }

    /// <summary>How many of the snapshot's passages have a vector of the configured model.</summary>
    public int Embedded(RootIndex index)
    {
        if (Service?.Model is not { } model || CacheModel(null) != model) return 0;
        return index.Snapshot.Passages.Count(p => _vectorCache.ContainsKey(VectorKey(model, p.Hash)));
    }

    // ------------------------------------------------------------------ search

    public sealed record Hit(Passage Passage, double? Cosine, int Words, double Score);

    public sealed record Result(List<Hit> Hits, bool Meaning, int Terms, int Passages, long SearchMs);

    /// <summary>
    /// The passages closest to the query: BM25 over the inverted index (identifiers whole and in parts, the path and the
    /// signature count as words of the passage) fused by weighted reciprocal rank with the cosine of the passages' vectors
    /// when the embedding server answers. At most <paramref name="perFile"/> passages of one file, none overlapping.
    /// <paramref name="under"/> keeps the files under a relative folder or file.
    /// </summary>
    public async Task<Result> SearchAsync(RootIndex index, string query, int limit, string? under, int perFile, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var snap = index.Snapshot;
        var terms = Lexical.CodeTerms(query);
        bool Allowed(Passage p) => under is not { Length: > 0 } || p.Path.Equals(under, WorkspacePaths.Comparison) || p.Path.StartsWith(under.TrimEnd('/') + "/", WorkspacePaths.Comparison);

        // words
        var score = new Dictionary<int, (double Score, int Words)>();
        foreach (var term in terms)
        {
            if (!snap.Postings.TryGetValue(term, out var post)) continue;
            var idf = Lexical.Idf(snap.Passages.Length, post.Ids.Length);
            for (var k = 0; k < post.Ids.Length; k++)
            {
                var id = post.Ids[k];
                var p = snap.Passages[id];
                var (s, w) = score.GetValueOrDefault(id);
                score[id] = (s + Lexical.Bm25(idf, post.Tf[k], p.Length, snap.AvgLength), w + 1);
            }
        }
        var words = score.Where(x => Allowed(snap.Passages[x.Key])).OrderByDescending(x => x.Value.Score).ThenBy(x => x.Key).Take(300)
            .Select(x => new Lexical.Hit(x.Key.ToString(), x.Value.Score, 0, x.Value.Words)).ToList();

        // meaning
        var cosine = new Dictionary<int, double>();
        var meaning = false;
        if (Service is { Available: true, Model: { } model } service && CacheModel(null) == model)
        {
            try
            {
                var q = (await service.EmbedAsync(new EmbeddingRequest { Texts = [query], Kind = EmbeddingKind.Query }, ct).ConfigureAwait(false)).Vectors[0];
                for (var id = 0; id < snap.Passages.Length; id++)
                    if (_vectorCache.TryGetValue(VectorKey(model, snap.Passages[id].Hash), out var v) && Allowed(snap.Passages[id]))
                        cosine[id] = VectorMath.Dot(q, v);
                meaning = cosine.Count > 0;
            }
            catch (EmbeddingException ex) { _ctx.Logger.LogDebug("Code search: no meaning search ({Code}): {Message}", ex.Code, ex.Message); }
        }
        var byMeaning = cosine.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(300).Select(x => x.Key.ToString());
        var fused = Lexical.Fuse(Lexical.Ranks(byMeaning), Lexical.Ranks(words));

        var hits = new List<Hit>();
        var perFileCount = new Dictionary<string, List<Chunk>>(StringComparer.Ordinal);
        foreach (var (key, fusedScore) in fused)
        {
            if (hits.Count >= limit) break;
            var id = int.Parse(key);
            var p = snap.Passages[id];
            var taken = perFileCount.TryGetValue(p.Path, out var list) ? list : perFileCount[p.Path] = [];
            if (taken.Count >= perFile || taken.Any(c => c.Start <= p.Chunk.End && p.Chunk.Start <= c.End)) continue;
            taken.Add(p.Chunk);
            hits.Add(new Hit(p, cosine.TryGetValue(id, out var c) ? Math.Round(c, 3) : null, score.GetValueOrDefault(id).Words, fusedScore));
        }
        return new Result(hits, meaning, terms.Count, snap.Passages.Length, watch.ElapsedMilliseconds);
    }

    // ------------------------------------------------------------------ the counted metric

    /// <summary>Add one search to the totals: the tokens it returned and the tokens of the files its passages came from (bytes / 4 both).</summary>
    public void Count(long returnedTokens, long fileTokens, long ms)
    {
        lock (_statsGate)
            _ctx.Data.Transaction(() =>
            {
                var t = _stats.Get("totals") ?? new JsonObject();
                long N(string k) => t[k]?.GetValue<long>() ?? 0;
                _stats.Put("totals", new JsonObject
                {
                    ["queries"] = N("queries") + 1, ["returnedTokens"] = N("returnedTokens") + returnedTokens,
                    ["fileTokens"] = N("fileTokens") + fileTokens, ["ms"] = N("ms") + ms,
                });
            });
    }

    public JsonObject Totals()
    {
        var t = _stats.Get("totals") ?? new JsonObject();
        long N(string k) => t[k]?.GetValue<long>() ?? 0;
        var (q, r, f) = (N("queries"), N("returnedTokens"), N("fileTokens"));
        return new JsonObject
        {
            ["queries"] = q, ["returnedTokens"] = r, ["fileTokens"] = f,
            ["ratio"] = f > 0 ? Math.Round((double)r / f, 3) : null,
            ["avgMs"] = q > 0 ? Math.Round((double)N("ms") / q, 1) : null,
        };
    }

    // ------------------------------------------------------------------ status and the sweep

    public JsonObject Status(RootIndex index)
    {
        var snap = index.Snapshot;
        var service = Service;
        return new JsonObject
        {
            ["root"] = index.Root,
            ["indexing"] = index.Refresh is { IsCompleted: false },
            ["built"] = index.Built,
            ["git"] = index.Git,
            ["listed"] = index.Listed,
            ["done"] = index.Done,
            ["files"] = index.Files.Count,
            ["capped"] = index.Capped,
            ["skipped"] = new JsonObject { ["large"] = index.SkippedLarge, ["binary"] = index.SkippedBinary, ["generated"] = index.SkippedGenerated },
            ["passages"] = snap.Passages.Length,
            ["textChars"] = snap.TextChars,
            ["terms"] = snap.Postings.Count,
            ["postings"] = snap.PostingCount,
            ["firstBuildMs"] = index.FirstBuildMs >= 0 ? index.FirstBuildMs : null,
            ["lastRefreshMs"] = index.LastRefreshMs,
            ["lastChanged"] = index.LastChanged,
            ["lastRemoved"] = index.LastRemoved,
            ["model"] = service?.Model,
            ["embedded"] = Embedded(index),
            ["embedding"] = index.Embedding is { IsCompleted: false },
            ["embedPending"] = index.EmbedPending,
            ["embedError"] = index.EmbedError,
            ["error"] = index.Error,
        };
    }

    public int StoredRoots => (int)_roots.Count();

    public long StoredVectors => _vectors.Count();

    private async Task SweepSafeAsync()
    {
        try { await Task.Run(() => Sweep(_stop.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ctx.Logger.LogInformation("Code search: the sweep failed: {Message}", ex.Message); }
    }

    /// <summary>
    /// Drop the documents of workspaces whose folder is gone or that nobody searched for <see cref="Retention"/>, then the
    /// vectors no file document refers to any more (or of another model than the configured one).
    /// </summary>
    public JsonObject Sweep(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.Add(-Retention).ToUnixTimeSeconds();
        var roots = 0;
        foreach (var doc in _roots.Find())
        {
            ct.ThrowIfCancellationRequested();
            var last = doc.Doc["lastUsed"]?.GetValue<long>() ?? 0;
            if (Directory.Exists(doc.Key) && last >= cutoff) continue;
            if (_byRoot.ContainsKey(doc.Key)) continue;
            _files.DeleteWhere(new DataQuery().Eq("root", doc.Key));
            _roots.Delete(doc.Key);
            roots++;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in _files.Find())
            foreach (var h in doc.Doc["chunks"] as JsonArray ?? []) if (h?.GetValue<string>() is { } s) used.Add(s);
        var model = Service?.Model;
        var drop = new List<string>();
        for (var offset = 0; ; offset += 500)
        {
            ct.ThrowIfCancellationRequested();
            var page = _vectors.Find(new DataQuery { Limit = 500, Offset = offset });
            foreach (var d in page)
            {
                var at = d.Key.LastIndexOf(':');
                var docModel = at > 0 ? d.Key[..at] : "";
                if (at < 0 || !used.Contains(d.Key[(at + 1)..]) || (model is not null && docModel != model)) drop.Add(d.Key);
            }
            if (page.Count < 500) break;
        }
        foreach (var k in drop) _vectors.Delete(k);
        if (roots + drop.Count > 0) _ctx.Logger.LogInformation("Code search: dropped {Roots} forgotten workspaces and {Vectors} unused vectors", roots, drop.Count);
        return new JsonObject { ["roots"] = roots, ["vectors"] = drop.Count };
    }

    public void Dispose()
    {
        _stop.Cancel();
        _sweep?.Dispose();
        foreach (var r in _byRoot.Values) r.Dispose();
        _byRoot.Clear();
    }
}
