using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetPI.Embeddings;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The Embeddings plugin's client (off by default, a short timeout, a back-off) and the Ideas plugin's meaning search on
/// top of it (idea-61wg9p): the index follows the backlog, the tool's list falls back to the closest ideas, add names the
/// ideas a new one resembles, and the commit notice names the open ideas nearest a commit. Without embeddings each path is
/// exactly what it was.
/// </summary>
public static class EmbeddingsTests
{
    /// <summary>A deterministic embedder: a bag of words hashed into 64 dimensions, so texts that share words are close.</summary>
    internal sealed class BagOfWords(string model = "bow-64") : IEmbeddingService
    {
        public string? Model { get; set; } = model;
        public bool Available { get; set; } = true;
        public List<(EmbeddingKind Kind, string Text)> Seen { get; } = [];
        /// <summary>Words that mean another word to this embedder (and to nothing else): meaning without shared words.</summary>
        public Dictionary<string, string> Synonyms { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
        {
            if (!Available || Model is null) throw new EmbeddingException("unavailable", "down");
            lock (Seen) Seen.AddRange(request.Texts.Select(t => (request.Kind, t)));
            return Task.FromResult(new EmbeddingResult(Model, 64, request.Texts
                .Select(t => Vector(string.Join(" ", t.Split(" ").Select(w => Synonyms.GetValueOrDefault(w.Trim(), w))))).ToList()));
        }

        public static float[] Vector(string text)
        {
            var v = new float[64];
            foreach (var w in text.ToLowerInvariant().Split([' ', '\n', ',', '.', ':', '"', '(', ')', '-'], StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 3) v[(int)((uint)w.GetHashCode(StringComparison.Ordinal) % 64)] += 1;
            v[63] += 0.01f; // never all zero
            return VectorMath.Normalize(v);
        }
    }

    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("emb-home"));
        public BagOfWords Embedder { get; } = new();
        public string Dir { get; } = T.TempDir("emb");

        public async Task StartAsync(bool withEmbeddings = true)
        {
            if (withEmbeddings) Ctx.ServicesFake.Register<IEmbeddingService>(Embedder);
            await new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);
        }

        public async Task<string> AddIdea(string title, string summary, string? status = null)
        {
            var idea = new JsonObject { ["title"] = title, ["summary"] = summary };
            if (status is not null) idea["status"] = status;
            var added = (JsonObject)NetPiJson.ToNode(await Ctx.Rpc.InvokeAsync("ideas.add", new JsonObject { ["idea"] = idea }))!;
            return added["id"]!.GetValue<string>();
        }

        public async Task<JsonObject> Reindex() => (JsonObject)NetPiJson.ToNode(await Ctx.Rpc.InvokeAsync("ideas.reindex", new JsonObject()))!;

        public Task<ToolResult> Tool(object args) =>
            Ctx.ToolsFake.Get("ideas")!.ExecuteAsync(new ToolContext
            {
                SessionId = "ses_1", AgentId = "agt_1", CallId = "call_1", Cwd = Dir, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);
    }

    private static Task<LocalWeb> FakeServer(List<JsonObject> seen, int delayMs = 0, int status = 200) => LocalWeb.StartAsync(app =>
        app.MapPost("/v1/embeddings", async (HttpContext http) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(http.Request.Body))!;
            lock (seen) seen.Add(body);
            if (delayMs > 0) await Task.Delay(delayMs, http.RequestAborted);
            if (status != 200)
            {
                http.Response.StatusCode = status;
                await http.Response.WriteAsJsonAsync(new { error = new { type = "model_not_found", message = "nope" } });
                return;
            }
            var texts = (JsonArray)body["input"]!;
            // Out of order on purpose: the client must place each vector by its index.
            var data = texts.Select((t, i) => (JsonNode)new JsonObject
            {
                ["object"] = "embedding", ["index"] = i,
                ["embedding"] = new JsonArray(BagOfWords.Vector(t!.GetValue<string>()).Select(x => (JsonNode)JsonValue.Create(x * 3)).ToArray()),
            }).Reverse().ToArray();
            await http.Response.WriteAsJsonAsync(new JsonObject { ["object"] = "list", ["model"] = body["model"]?.DeepClone(), ["data"] = new JsonArray(data) });
        }));

    private static async Task<IEmbeddingService> StartClient(FakePluginContext ctx, string url, string model = "bge-base-en-v1.5")
    {
        ctx.SettingsFake.Set("embed.baseUrl", JsonValue.Create(url));
        if (model.Length > 0) ctx.SettingsFake.Set("embed.model", JsonValue.Create(model));
        await new EmbeddingsPlugin().StartAsync(ctx, CancellationToken.None);
        return ctx.Services.Get<IEmbeddingService>()!;
    }

    public static void Register(TestRunner r)
    {
        r.Add("embeddings: off without embed.model — Available is false, nothing is sent, a call says why", async () =>
        {
            var seen = new List<JsonObject>();
            await using var server = await FakeServer(seen);
            var ctx = new FakePluginContext(T.TempDir("emb-off"));
            var client = await StartClient(ctx, server.Url, model: "");
            Check.True(client.Model is null);
            Check.False(client.Available);
            var ex = await Check.ThrowsAsync<EmbeddingException>(() => client.EmbedAsync(new EmbeddingRequest { Texts = ["x"] }, CancellationToken.None));
            Check.Equal("off", ex.Code);
            Check.Equal(0, seen.Count);
        });

        r.Add("embeddings: posts /v1/embeddings with the model; a query gets embed.queryPrefix, a document none; vectors normalized, in index order", async () =>
        {
            var seen = new List<JsonObject>();
            await using var server = await FakeServer(seen);
            var ctx = new FakePluginContext(T.TempDir("emb-post"));
            ctx.SettingsFake.Set("embed.queryPrefix", JsonValue.Create("Q: "));
            var client = await StartClient(ctx, server.Url + "/v1");   // a base URL ending in /v1 works too
            var docs = await client.EmbedAsync(new EmbeddingRequest { Texts = ["alpha beta gamma", "delta epsilon"] }, CancellationToken.None);
            Check.Equal("bge-base-en-v1.5", docs.Model);
            Check.Equal(64, docs.Dimensions);
            Check.True(Math.Abs(VectorMath.Dot(docs.Vectors[0], docs.Vectors[0]) - 1) < 1e-4, "normalized");
            Check.True(VectorMath.Dot(docs.Vectors[0], BagOfWords.Vector("alpha beta gamma")) > 0.999, "first text first, although the server sent it last");
            Check.Equal("alpha beta gamma", seen[0]["input"]![0]!.GetValue<string>());
            Check.Equal("bge-base-en-v1.5", seen[0]["model"]!.GetValue<string>());
            await client.EmbedAsync(new EmbeddingRequest { Texts = ["find me"], Kind = EmbeddingKind.Query }, CancellationToken.None);
            Check.Equal("Q: find me", seen[1]["input"]![0]!.GetValue<string>());
        });

        r.Add("embeddings: batches of embed.batchSize texts", async () =>
        {
            var seen = new List<JsonObject>();
            await using var server = await FakeServer(seen);
            var ctx = new FakePluginContext(T.TempDir("emb-batch"));
            ctx.SettingsFake.Set("embed.batchSize", JsonValue.Create(2));
            var client = await StartClient(ctx, server.Url);
            var result = await client.EmbedAsync(new EmbeddingRequest { Texts = ["a1 x", "b2 y", "c3 z", "d4 w", "e5 v"] }, CancellationToken.None);
            Check.Equal(5, result.Vectors.Count);
            Check.Equal(3, seen.Count);
            Check.Equal("e5 v", seen[2]["input"]![0]!.GetValue<string>());
        });

        r.Add("embeddings: a server that holds the request times out at embed.timeoutMs, then the client backs off without sending", async () =>
        {
            var seen = new List<JsonObject>();
            await using var server = await FakeServer(seen, delayMs: 5000);
            var ctx = new FakePluginContext(T.TempDir("emb-slow"));
            ctx.SettingsFake.Set("embed.timeoutMs", JsonValue.Create(200));
            var client = await StartClient(ctx, server.Url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Check.ThrowsAsync<EmbeddingException>(() => client.EmbedAsync(new EmbeddingRequest { Texts = ["slow"] }, CancellationToken.None));
            Check.Equal("timeout", ex.Code);
            Check.True(sw.ElapsedMilliseconds < 2500, $"gave up in {sw.ElapsedMilliseconds} ms");
            Check.False(client.Available, "backing off");
            var again = await Check.ThrowsAsync<EmbeddingException>(() => client.EmbedAsync(new EmbeddingRequest { Texts = ["again"] }, CancellationToken.None));
            Check.Equal("unavailable", again.Code);
            Check.Equal(1, seen.Count);
            var status = (JsonObject)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("embed.status", new JsonObject()))!;
            Check.False(status["available"]!.GetValue<bool>());
            Check.Equal(1L, status["failures"]!.GetValue<long>());
            Check.Contains(status["lastError"]!.GetValue<string>(), "200 ms");
        });

        r.Add("embeddings: a server error keeps its code; embed.texts answers through the RPC", async () =>
        {
            var seen = new List<JsonObject>();
            await using var bad = await FakeServer(seen, status: 404);
            var ctx = new FakePluginContext(T.TempDir("emb-err"));
            var client = await StartClient(ctx, bad.Url);
            var ex = await Check.ThrowsAsync<EmbeddingException>(() => client.EmbedAsync(new EmbeddingRequest { Texts = ["x"] }, CancellationToken.None));
            Check.Equal("model_not_found", ex.Code);
            Check.Contains(ex.Message, "HTTP 404");

            await using var good = await FakeServer(seen);
            var ctx2 = new FakePluginContext(T.TempDir("emb-rpc"));
            await StartClient(ctx2, good.Url);
            var answer = (JsonObject)NetPiJson.ToNode(await ctx2.Rpc.InvokeAsync("embed.texts", new JsonObject { ["texts"] = new JsonArray("one two", "three four") }))!;
            Check.Equal(2, ((JsonArray)answer["vectors"]!).Count);
            Check.Equal(64, answer["dim"]!.GetValue<int>());
        });

        r.Add("embeddings: VectorMath packs and unpacks exactly; dot of different lengths is 0", () =>
        {
            var v = new float[] { 0.5f, -1.25f, 3e-7f, 42f, 0f };
            Check.Equal(string.Join(",", v), string.Join(",", VectorMath.Unpack(VectorMath.Pack(v))!));
            Check.True(VectorMath.Unpack("not base64!") is null);
            Check.Equal(0f, VectorMath.Dot(new float[3], new float[4]));
            var a = Enumerable.Range(0, 37).Select(i => (float)i).ToArray();
            Check.Equal(a.Sum(x => x * x), VectorMath.Dot(a, a));
            return Task.CompletedTask;
        });

        r.Add("ideas embeddings: the index follows the backlog — only a changed idea is embedded again, a deleted one leaves, a new model re-embeds all", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var a = await env.AddIdea("Collapsing a process row leaks a timer", "The Work tab keeps a timer per collapsed row.");
            var b = await env.AddIdea("Radio buffer top-up", "Songs per batch should refill when raised.");
            var first = await env.Reindex();
            Check.Equal(2, first["indexed"]!.GetValue<int>());
            var again = await env.Reindex();
            Check.Equal(0, again["indexed"]!.GetValue<int>());
            Check.Equal(2, again["unchanged"]!.GetValue<int>());

            await env.Ctx.Rpc.InvokeAsync("ideas.update", new JsonObject { ["id"] = b, ["patch"] = new JsonObject { ["summary"] = "Top up the queue when the batch size grows." } });
            env.Embedder.Seen.Clear();
            var edited = await env.Reindex();
            Check.Equal(1, edited["indexed"]!.GetValue<int>());
            Check.True(env.Embedder.Seen.All(s => s.Text.Contains("Radio buffer top-up")), "only the edited idea was embedded");
            Check.True(env.Embedder.Seen.All(s => s.Kind == EmbeddingKind.Document));

            await env.Ctx.Rpc.InvokeAsync("ideas.delete", new JsonObject { ["id"] = a });
            Check.Equal(1, (await env.Reindex())["removed"]!.GetValue<int>());

            env.Embedder.Model = "bow-64-v2";
            Check.Equal(1, (await env.Reindex())["indexed"]!.GetValue<int>());
        });

        r.Add("ideas embeddings: list with a query no idea fully contains names the closest ideas; without embeddings the answer is unchanged", async () =>
        {
            foreach (var withEmbeddings in new[] { true, false })
            {
                var env = new Env();
                await env.StartAsync(withEmbeddings);
                await env.AddIdea("Collapsing a process row leaks a timer", "The Work tab keeps a timer per collapsed process row.");
                await env.AddIdea("Radio buffer top-up", "Songs per batch should refill when raised.");
                if (withEmbeddings) await env.Reindex();
                var result = await env.Tool(new { action = "list", query = "timer leaking folding" });
                Check.Contains(result.Content, "No matching ideas");
                if (withEmbeddings)
                {
                    Check.Contains(result.Content, "Closest by meaning");
                    var closest = (JsonArray)((JsonObject)result.Details!)["closest"]!;
                    Check.Equal("Collapsing a process row leaks a timer", closest[0]!["title"]!.GetValue<string>());
                }
                else Check.NotContains(result.Content, "Closest by meaning");
            }
        });

        r.Add("ideas embeddings: add names the existing ideas a new one resembles (a warning, the idea is added)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.AddIdea("Session tab: all groups collapsible", "Pinned and Today open by default, the other groups collapsible in the session tab.");
            await env.AddIdea("Radio buffer top-up", "Songs per batch should refill when raised.");
            await env.Reindex();
            var result = await env.Tool(new { action = "add", title = "Session tab: collapsible groups", summary = "Make all groups in the session tab collapsible, Pinned and Today open." });
            Check.Contains(result.Content, "Added idea");
            Check.Contains(result.Content, "resembles existing ideas");
            Check.Contains(result.Content, "Session tab: all groups collapsible");
            Check.NotContains(result.Content, "Radio buffer");
            var unrelated = await env.Tool(new { action = "add", title = "Frigate prompt shorter", summary = "Shorter object descriptions for cameras." });
            Check.NotContains(unrelated.Content, "resembles");
        });

        r.Add("ideas embeddings: ideas.similar ranks by meaning; available false without embeddings", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var timer = await env.AddIdea("Collapsing a process row leaks a timer", "The Work tab keeps a timer per collapsed process row.");
            await env.AddIdea("Radio buffer top-up", "Songs per batch should refill when raised.");
            await env.Reindex();
            var found = (JsonObject)NetPiJson.ToNode(await env.Ctx.Rpc.InvokeAsync("ideas.similar", new JsonObject { ["text"] = "a leaking timer on collapsed rows", ["limit"] = 1 }))!;
            Check.True(found["available"]!.GetValue<bool>());
            Check.Equal(timer, found["ideas"]![0]!["id"]!.GetValue<string>());

            var off = new Env();
            await off.StartAsync(withEmbeddings: false);
            await off.AddIdea("x idea", "y");
            var none = (JsonObject)NetPiJson.ToNode(await off.Ctx.Rpc.InvokeAsync("ideas.similar", new JsonObject { ["text"] = "x" }))!;
            Check.False(none["available"]!.GetValue<bool>());
        });

        r.Add("ideas embeddings: a failing embedder means no ranking, not an error", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.AddIdea("Collapsing a process row leaks a timer", "Timer per row.");
            await env.Reindex();
            env.Embedder.Available = false;
            var result = await env.Tool(new { action = "list", query = "timer leaks collapse" });
            Check.False(result.IsError);
            Check.NotContains(result.Content, "Closest by meaning");
        });

        r.Add("notice: the commit message comes from -m (several, quoted) or from git's [branch hash] line", () =>
        {
            Check.Equal("Work tab: a fix\nsecond para", IdeaCommitNoticeHook.CommitMessage("git add -A; git commit -m \"Work tab: a fix\" -m 'second para'", null));
            Check.Equal("say \\\"hi\\\"", IdeaCommitNoticeHook.CommitMessage("git commit -m \"say \\\"hi\\\"\"", null));
            Check.Equal("Ideas: meaning search", IdeaCommitNoticeHook.CommitMessage("git commit -F msg.txt", "[netpi/embeddings 1a2b3c4] Ideas: meaning search\n 3 files changed"));
            Check.True(IdeaCommitNoticeHook.CommitMessage("git merge feature", "Merge made by the 'ort' strategy.") is null);
            return Task.CompletedTask;
        });

        r.Add("notice: more than three open ideas → the notice names the nearest ones; no answer → the plain notice", async () =>
        {
            var ctx = new FakePluginContext();
            var project = ctx.SessionsFake.CreateProject("NetPI", Path.Combine(ctx.Paths.Home, "repo"));
            var session = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = project.Id });
            var open = Enumerable.Range(1, 6).Select(i => new JsonObject { ["id"] = $"idea-{i:000000}", ["title"] = $"Idea number {i}" }).ToList();
            string? asked = null;
            string? askedProject = null;
            List<IdeaScore>? answer = [new IdeaScore(open[4], 0.81), new IdeaScore(open[1], 0.77)];
            var hook = new IdeaCommitNoticeHook(() => ctx.Settings, _ => open, (text, pid, _) =>
            {
                asked = text;
                askedProject = pid;
                return Task.FromResult<List<IdeaScore>?>(answer);
            });
            async Task<TurnDecision?> CommitAndAsk()
            {
                var run = T.Run(ctx, T.Model(), session);
                run.Project = project;
                run.Cwd = project.Path;
                var turn = T.Turn(run, tools: [T.Tool("bash"), T.Tool("ideas")]);
                await hook.OnAfterToolCallAsync(turn, new ToolCallPart { Id = "c1", Name = "bash", Arguments = new JsonObject { ["command"] = "git commit -m \"Work tab: stop the timer\"" }.ToJsonString() },
                    new ToolResultPart { CallId = "c1", Name = "bash", Content = "[master abc1234] Work tab: stop the timer", Details = new JsonObject { ["exitCode"] = 0 } });
                return await hook.OnAfterModelCallAsync(turn, T.Assistant("done"));
            }
            var d = await CommitAndAsk();
            Check.Equal("Work tab: stop the timer", asked);
            Check.Equal(project.Id, askedProject);
            Check.Contains(d!.Text, "closest to it in meaning");
            Check.Contains(d.Text, "idea-000005 \"Idea number 5\"; idea-000002 \"Idea number 2\"");
            Check.Contains(d.Text, "possibly none of them");

            answer = null;
            var plain = await CommitAndAsk();
            Check.NotContains(plain!.Text, "closest");
            Check.Contains(plain.Text, "If it finished one of that project's open ideas");
        });

        r.Add("ideas embeddings: the commit description is the message and the changed paths, not git's header lines", () =>
        {
            const string show = "commit 1a2b3c4d5e\nAuthor:     Tommy <t@x>\nAuthorDate: Sat Oct 3 18:00:00 2026 +0200\nCommit:     Tommy <t@x>\nCommitDate: Sat Oct 3 18:00:00 2026 +0200\n\n" +
                                "    Ideas: link reads the commit body\n\n    The question now sees the body and the files.\n\n" +
                                " plugins/NetPI.Ideas/IdeaCommitCheck.cs | 40 +++++---\n docs/PLUGIN-IDEAS.md | 3 +\n 2 files changed\n\ndiff --git a/x b/x\n+secret patch line";
            var text = IdeaCommitCheck.CommitDescription(show)!;
            Check.True(text.StartsWith("Ideas: link reads the commit body", StringComparison.Ordinal), text);
            Check.Contains(text, "The question now sees the body and the files.");
            Check.Contains(text, "Changed: plugins/NetPI.Ideas/IdeaCommitCheck.cs, docs/PLUGIN-IDEAS.md");
            Check.NotContains(text, "Author");
            Check.NotContains(text, "secret patch line");
            Check.Equal("abc", IdeaOps.ClipEnds("abc", 10));
            var ends = IdeaOps.ClipEnds(new string('a', 50) + new string('z', 50), 20);
            Check.True(ends.StartsWith("aaaaaaaaaa", StringComparison.Ordinal) && ends.EndsWith("zzzzzzzzzz", StringComparison.Ordinal), ends);
            return Task.CompletedTask;
        });

        r.Add("ideas embeddings: above 51 ideas the ranking fuses words and meaning, so a paraphrased idea is in the first window", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            for (var i = 0; i < 60; i++) await env.AddIdea($"Filler topic number {i} about widgets", $"Widgets and gadgets item {i}.");
            var target = await env.AddIdea("Collapsing a process row leaks a timer", "The Work tab keeps a timer per collapsed process row.");
            await env.Reindex();
            var repo = IdeasRepository.Open(env.Ctx.Data, env.Ctx.Access, env.Ctx.Log, env.Ctx.Paths.Home);
            using var vectors = new IdeaVectors(env.Ctx, repo);
            var open = repo.OpenIdeas(null);
            // "widgets" is in every filler idea and no word of the text is in the target, so shared words alone put the
            // target last; to the embedder "folding", "drips" and "clock" mean collapsing, leaks and timer.
            const string text = "widgets: folding drips clock";
            foreach (var (w, m) in new[] { ("folding", "collapsing"), ("drips", "leaks"), ("clock", "timer") }) env.Embedder.Synonyms[w] = m;
            Check.True(IdeaMatch.Ranked(open, text).FindIndex(i => i["id"]!.GetValue<string>() == target) >= IdeaMatch.MaxOptions, "words alone push it past the first window");
            var ranked = (await vectors.RankAsync(text, open, CancellationToken.None))!;
            Check.True(ranked.FindIndex(i => i["id"]!.GetValue<string>() == target) < IdeaMatch.MaxOptions, "fused: inside the first window");
            Check.True(await vectors.RankAsync(text, open.Take(10).ToList(), CancellationToken.None) is null, "51 or fewer: nothing to rank");
        });

        r.Add("memory: a chat is embedded in pieces (start, summaries, end kept); search finds the chat by meaning, not the current one", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("memory"));
            var embedder = new BagOfWords();
            ctx.ServicesFake.Register<IEmbeddingService>(embedder);
            var radio = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "Radio buffer" });
            ctx.SessionsFake.AppendMessage(radio.Id, ChatMessage.UserText("the radio runs out of buffered songs when the batch size is raised"));
            ctx.SessionsFake.AppendMessage(radio.Id, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "Top up the queue whenever songs per batch grows." }] });
            var other = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "Timer leak" });
            ctx.SessionsFake.AppendMessage(other.Id, ChatMessage.UserText("the work tab leaks a timer per collapsed process row"));
            var current = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "Now" });
            ctx.SessionsFake.AppendMessage(current.Id, ChatMessage.UserText("buffered songs batch radio queue"));
            await new NetPI.Memory.MemoryPlugin().StartAsync(ctx, CancellationToken.None);
            var swept = (JsonObject)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("memory.reindex", new JsonObject()))!;
            Check.Equal(3, swept["indexed"]!.GetValue<int>());
            Check.Equal(0, ((JsonObject)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("memory.reindex", new JsonObject()))!)["indexed"]!.GetValue<int>(), "nothing changed: nothing re-embedded");
            var found = (JsonObject)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("memory.search",
                new JsonObject { ["query"] = "radio buffered songs batch", ["excludeSessionId"] = current.Id, ["limit"] = 2 }))!;
            Check.True(found["available"]!.GetValue<bool>());
            Check.Equal(radio.Id, found["chats"]![0]!["sessionId"]!.GetValue<string>());
            Check.NotContains(found.ToJsonString(), current.Id);

            var long_ = Enumerable.Range(0, 200).Select(i => ChatMessage.UserText($"line {i} " + new string('x', 300))).ToList();
            var chunks = NetPI.Memory.MemoryIndex.Chunks(new SessionInfo { Title = "Long" }, long_, 8);
            Check.Equal(8, chunks.Count);
            Check.Contains(chunks[0], "line 0 ");
            Check.Contains(chunks[^1], "line 199 ");

            embedder.Model = null;
            var off = (JsonObject)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("memory.search", new JsonObject { ["query"] = "radio" }))!;
            Check.False(off["available"]!.GetValue<bool>());
            ctx.Unload();
        });
    }
}
