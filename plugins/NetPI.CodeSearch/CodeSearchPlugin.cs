using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.CodeSearch;

/// <summary>
/// Code search over the session's workspace: <c>code_search</c> answers with the passages (functions, classes, sections)
/// closest to a query, as <c>file:start-end</c> with their signature and text, instead of whole files. The workspace is
/// indexed in the background at its first search (git's file list, files cut at their declarations), kept in step by
/// content hashes, ranked by words (BM25) and, with the Embeddings plugin's model, by meaning, fused by weighted
/// reciprocal rank. Each search counts the tokens it returned against the size of the files its passages came from.
/// </summary>
[NetPiPlugin("netpi.codesearch", Name = "Code search", Description = "code_search: the workspace's passages closest to a query, by words and (with an embedding model) by meaning", Order = 86)]
public sealed class CodeSearchPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "codesearch", Title = "Code search", Group = "Tools", Order = 63,
            Settings =
            [
                SettingInfo.Bool("codesearch.enabled", "Search the workspace's code", true,
                    "The code_search tool: a workspace is indexed in the background at its first search (its files are read, never sent anywhere except to the embedding server when one is configured)."),
                SettingInfo.Bool("codesearch.embed", "Rank by meaning", true,
                    "Embed the passages with the embedding model (embed.model) and rank by meaning as well as by words. Off, or without a model: words only."),
                SettingInfo.Int("codesearch.maxFiles", "Files per workspace", CodeIndex.DefaultMaxFiles, "A workspace with more files is indexed up to this many (in git's order).", 100, 200_000),
                SettingInfo.Int("codesearch.maxFileKb", "Largest file", CodeIndex.DefaultMaxFileKb, "Larger files are left out.", 16, 8192, "KB"),
                SettingInfo.Int("codesearch.maxChunkLines", "Longest passage", CodeIndex.DefaultMaxChunkLines, "A longer declaration is cut into parts of at most this many lines (and 100 characters per line on average).", 20, 400, "lines"),
            ],
        });
        var index = context.Track(new CodeIndex(context));
        var searcher = new CodeSearcher(context, index);
        context.Tools.Register(new CodeSearchTool(context, searcher));

        var scope = new[] { RpcParam.Opt("sessionId"), RpcParam.Opt("root") };
        context.Rpc.Register(new RpcMethod("codesearch.search", "Passages of a workspace closest to a query: { query, sessionId? | root?, limit? (8), path? } → { root, meaning, indexing, passages: [{ path, start, end, signature, text, score, words }], returnedTokens, fileTokens, ms }", ReadOnly: false, // a search refreshes the index and spends embedding calls: it writes
            Params: [RpcParam.Req("query"), .. scope, RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("path")]),
            async (r, rct) =>
            {
                var root = searcher.RootOf(r.Str("sessionId"), r.Str("root"));
                var answer = await searcher.SearchAsync(root, r.Required("query"), r.Int("limit") ?? 8, r.Str("path"), rct).ConfigureAwait(false);
                return answer.Details;
            });
        context.Rpc.Register(new RpcMethod("codesearch.status", "The code index: { sessionId? | root? } → { enabled, model, roots: [{ root, indexing, built, files, passages, embedded, firstBuildMs, … }], storedRoots, storedVectors, totals: { queries, returnedTokens, fileTokens, ratio, avgMs } }", ReadOnly: true,
            Params: scope),
            (r, _) =>
            {
                var roots = r.Str("sessionId") is not null || r.Str("root") is not null
                    ? [index.Get(searcher.RootOf(r.Str("sessionId"), r.Str("root")))]
                    : index.Loaded;
                return Task.FromResult<object?>(new JsonObject
                {
                    ["enabled"] = index.Enabled,
                    ["model"] = context.Settings.GetOr("codesearch.embed", true) ? context.Services.Get<IEmbeddingService>()?.Model : null,
                    ["roots"] = new JsonArray(roots.Select(x => (JsonNode)index.Status(x)).ToArray()),
                    ["storedRoots"] = index.StoredRoots,
                    ["storedVectors"] = index.StoredVectors,
                    ["totals"] = index.Totals(),
                });
            });
        context.Rpc.Register(new RpcMethod("codesearch.reindex", "Bring a workspace's index up to date now and wait for it: { sessionId? | root?, full? (forget it first), sweep? (drop forgotten workspaces and unused vectors) } → the root's status (and { sweep })", ReadOnly: false,
            Params: [.. scope, RpcParam.Opt("full", RpcParamType.Boolean), RpcParam.Opt("sweep", RpcParamType.Boolean)]),
            async (r, rct) =>
            {
                JsonObject? swept = r.Bool("sweep") == true ? index.Sweep(rct) : null;
                if (r.Str("sessionId") is null && r.Str("root") is null)
                    return swept ?? throw new RpcException("bad_request", "Name the workspace: sessionId or root (or sweep: true alone).");
                var root = searcher.RootOf(r.Str("sessionId"), r.Str("root"));
                if (r.Bool("full") == true) index.Forget(root);
                var ri = index.Get(root);
                await index.EnsureFresh(ri, force: true).WaitAsync(rct).ConfigureAwait(false);
                if (ri.Embedding is { } embedding) await embedding.WaitAsync(rct).ConfigureAwait(false);
                var status = index.Status(ri);
                if (swept is not null) status["sweep"] = swept;
                return status;
            });
        index.Start();
        return Task.CompletedTask;
    }
}

/// <summary>The search both the tool and <c>codesearch.search</c> run, and the answer they format.</summary>
internal sealed class CodeSearcher(IPluginContext ctx, CodeIndex index)
{
    /// <summary>How long a search waits for the refresh it started (the first build of a large workspace takes longer: the answer then says it is still indexing).</summary>
    public static TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>At most this many passages of one file in an answer.</summary>
    public const int PerFile = 3;

    public sealed record Answer(string Text, JsonObject Details, bool Empty);

    /// <summary>
    /// The workspace of a session (bound: its checkout, never a fallback to the project; else its project's folder, else its
    /// folder), or a folder named directly.
    /// </summary>
    public string RootOf(string? sessionId, string? root)
    {
        if (root is { Length: > 0 })
        {
            var full = Path.GetFullPath(root);
            return Directory.Exists(full) ? full : throw new RpcException("not_found", $"Directory not found: {full}");
        }
        var session = sessionId is { Length: > 0 } ? ctx.Sessions.GetSession(sessionId) : null;
        if (session is null) throw new RpcException("not_found", $"Session not found: {sessionId}");
        try
        {
            if (ctx.Services.Get<IWorkspaceResolver>()?.Resolve(session) is { } binding) return binding.Root;
        }
        catch (WorkspaceUnavailableException ex) { throw new RpcException("workspace_unavailable", ex.Message); }
        if (session.ProjectId is { } pid && ctx.Sessions.GetProject(pid)?.Path is { Length: > 0 } path) return path;
        return ctx.Sessions.GetCwd(session);
    }

    public async Task<Answer> SearchAsync(string root, string query, int limit, string? path, CancellationToken ct)
    {
        if (!index.Enabled) throw new RpcException("disabled", "Code search is switched off (codesearch.enabled).");
        query = query.Trim();
        if (query.Length == 0) throw new RpcException("bad_request", "Missing 'query'.");
        limit = Math.Clamp(limit, 1, 20);
        var under = Under(root, path);
        var ri = index.Get(root);
        var refresh = index.EnsureFresh(ri);
        try { await refresh.WaitAsync(Wait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // the workspace was let go of (evicted, forgotten, the plugin stopping): answer from what there is
        var result = await index.SearchAsync(ri, query, limit, under, PerFile, ct).ConfigureAwait(false);
        var indexing = !ri.Built || refresh is { IsCompleted: false };
        return Format(ri, query, result, indexing, under);
    }

    /// <summary>A path filter as a folder or file relative to the root, with <c>/</c>; refused outside the workspace.</summary>
    private static string? Under(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() is "." or "./") return null;
        var full = Path.GetFullPath(Path.Combine(root, path.Trim()));
        var rel = Path.GetRelativePath(root, full);
        if (rel == ".") return null;
        if (rel.StartsWith("..") || Path.IsPathRooted(rel))
            throw new RpcException("bad_request", $"path is outside the workspace ({root}): {path}");
        return rel.Replace('\\', '/');
    }

    private Answer Format(RootIndex ri, string query, CodeIndex.Result result, bool indexing, string? under)
    {
        var budget = ToolResultLimit.Fit(ctx.Settings, 16_000);
        var text = new StringBuilder();
        var state = indexing
            ? $"; still indexing: {ri.Done} of {ri.Listed} files read, {result.Passages} passages so far — search again for the rest"
            : "";
        var how = result.Meaning ? "by meaning and words" : "by words only (no embedding model, or its passages are not embedded yet)";
        var where = under is null ? "" : $" under {under}";
        var embedNote = !result.Meaning && index.Embedded(ri) == 0 && ri.Embedding is { IsCompleted: false } ? "; embedding in progress" : "";
        if (result.Hits.Count == 0)
        {
            var none = result.Terms == 0 && !result.Meaning
                ? $"No search words in \"{query}\" (one-letter and common English words are left out) and no meaning search{state}."
                : $"No passage{where} matches \"{query}\" ({how}{state}{embedNote}).";
            return new Answer(none, Details(ri, result, [], 0, 0, indexing), true);
        }
        text.Append(CultureInfo.InvariantCulture, $"{result.Hits.Count} passages{where}, best first, ranked {how}{state}{embedNote}. Read more of a file at the lines shown.\n");
        var shown = new List<CodeIndex.Hit>();
        var listed = new List<CodeIndex.Hit>();
        foreach (var hit in result.Hits)
        {
            var c = hit.Passage.Chunk;
            var head = Header(hit, result.Terms);
            var fence = c.Text.Contains("```") ? "~~~~" : "```";
            var block = $"\n{head}\n{fence}{Fence(hit.Passage.Path)}\n{c.Text}\n{fence}\n";
            if (text.Length + block.Length > budget && shown.Count > 0) { listed.Add(hit); continue; }
            if (text.Length + block.Length > budget)
                block = $"\n{head}\n{fence}{Fence(hit.Passage.Path)}\n{c.Text[..Math.Max(0, budget - text.Length - head.Length - 40)]}\n…\n{fence}\n";
            text.Append(block);
            shown.Add(hit);
        }
        if (listed.Count > 0)
        {
            text.Append("\nMore passages (not shown, over the result limit):\n");
            foreach (var hit in listed) text.Append("- ").Append(Header(hit, result.Terms)).Append('\n');
        }
        var answerText = text.ToString().TrimEnd();
        // the counted metric: tokens returned vs. the tokens of the files the passages came from (bytes / 4 both)
        var returned = (long)Math.Ceiling(Encoding.UTF8.GetByteCount(answerText) / 4.0);
        var files = (long)Math.Ceiling(shown.Concat(listed).GroupBy(h => h.Passage.Path).Sum(g => (double)g.First().Passage.FileBytes) / 4.0);
        index.Count(returned, files, result.SearchMs);
        return new Answer(answerText, Details(ri, result, shown, returned, files, indexing), false);
    }

    private static string Header(CodeIndex.Hit hit, int terms)
    {
        var c = hit.Passage.Chunk;
        var notes = new List<string>();
        if (hit.Cosine is { } cos) notes.Add(cos.ToString("0.00", CultureInfo.InvariantCulture));
        if (hit.Words > 0) notes.Add($"{hit.Words}/{terms} words");
        return $"{hit.Passage.Path}:{c.Start}-{c.End}  {c.Signature}" + (notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : "");
    }

    private static string Fence(string path) => Chunker.Language(path) switch
    {
        "csharp" => "csharp", "js" => Path.GetExtension(path).StartsWith(".t", StringComparison.OrdinalIgnoreCase) ? "ts" : "js",
        "svelte" => "svelte", "python" => "python", "go" => "go", "rust" => "rust", "powershell" => "powershell", "markdown" => "markdown",
        _ => "",
    };

    private static JsonObject Details(RootIndex ri, CodeIndex.Result result, List<CodeIndex.Hit> hits, long returned, long files, bool indexing) => new()
    {
        ["root"] = ri.Root,
        ["meaning"] = result.Meaning,
        ["indexing"] = indexing,
        ["indexed"] = new JsonObject { ["files"] = ri.Files.Count, ["listed"] = ri.Listed, ["passages"] = result.Passages },
        ["passages"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
        {
            ["path"] = h.Passage.Path, ["start"] = h.Passage.Chunk.Start, ["end"] = h.Passage.Chunk.End, ["signature"] = h.Passage.Chunk.Signature,
            ["text"] = h.Passage.Chunk.Text, ["score"] = h.Cosine, ["words"] = h.Words,
        }).ToArray()),
        ["returnedTokens"] = returned,
        ["fileTokens"] = files,
        ["ratio"] = files > 0 ? Math.Round((double)returned / files, 3) : null,
        ["ms"] = result.SearchMs,
    };
}

/// <summary><c>code_search</c>: the agent's way to find where something is implemented or explained without reading whole files.</summary>
internal sealed class CodeSearchTool(IPluginContext ctx, CodeSearcher searcher) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "code_search",
        Label = "Search code",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "query",
        Description = "Search the session's workspace by meaning and by words (identifiers, error texts, file names): the best-matching passages — " +
                      "functions, classes, doc sections — each as path:start-end with its signature and text, instead of whole files. " +
                      "Indexed in the background at the first search; the answer says when indexing is still going on.",
        PromptGuidelines = ["To find where something is implemented or explained, code_search returns the relevant passages with their lines; read further only where they point. grep remains the tool for an exact string."],
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string","description":"what you are looking for: words, identifiers, a description of the behavior"},"limit":{"type":"integer","minimum":1,"maximum":20,"description":"passages (default 8)"},"path":{"type":"string","description":"only files under this folder or this file (relative to the workspace); an absolute path outside the workspace searches that folder instead"}},"required":["query"]}""")!.AsObject(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var query = a.Str("query")?.Trim() ?? "";
        if (query.Length == 0) return ToolResult.Error("Missing 'query'.");
        string root;
        try { root = RootOf(context); }
        catch (WorkspaceUnavailableException ex) { return ToolResult.Error(ex.Message); }
        try
        {
            var path = a.Str("path");
            // Read-only: an absolute path outside the workspace searches that folder (or the file's folder, filtered to the file) as its own root.
            if (path is { Length: > 0 } && Path.IsPathRooted(path.Trim()))
            {
                var full = Path.GetFullPath(path.Trim());
                var rel = Path.GetRelativePath(root, full);
                if (rel.StartsWith("..") || Path.IsPathRooted(rel))
                {
                    if (Directory.Exists(full)) { root = full; path = null; }
                    else if (File.Exists(full)) { root = Path.GetDirectoryName(full)!; path = Path.GetFileName(full); }
                    else return ToolResult.Error($"Not found: {full}");
                }
            }
            var answer = await searcher.SearchAsync(root, query, a.Int("limit") ?? 8, path, ct).ConfigureAwait(false);
            return ToolResult.Ok(answer.Text, answer.Details);
        }
        catch (RpcException ex) { return ToolResult.Error(ex.Message); }
    }

    /// <summary>The session's workspace: the binding the run carries, else the one the resolver names (bound sessions never fall back), else the project folder, else the working folder.</summary>
    private string RootOf(ToolContext context)
    {
        if (context.Workspace() is { } bound) return bound.Root;
        if (ctx.Sessions.GetSession(context.SessionId) is { } session && ctx.Services.Get<IWorkspaceResolver>()?.Resolve(session) is { } binding) return binding.Root;
        return context.Project?.Path is { Length: > 0 } project ? project : context.Cwd;
    }
}
