using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetPI.Decide;

namespace NetPI.Aux.Tests;

public static class DecideTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("decide-home"));
        public string Dir { get; } = T.TempDir("decide");

        public async Task StartAsync(string url)
        {
            Ctx.SettingsFake.Set("providers.aiproxy.baseUrl", JsonValue.Create(url + "/v1"));
            await new DecidePlugin().StartAsync(Ctx, CancellationToken.None);
        }

        public Task<ToolResult> Run(object args) =>
            Ctx.ToolsFake.Get("decide")!.ExecuteAsync(new ToolContext
            {
                SessionId = "ses_1", AgentId = "agt_1", CallId = "call_1", Cwd = Dir, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);
    }

    /// <summary>A fake Kev: "ERR" lines are errors (sure), "maybe" lines are unsure; records every request body.</summary>
    private static Task<LocalWeb> FakeKev(List<JsonObject> seen, int status = 200, object? error = null) => LocalWeb.StartAsync(app =>
    {
        app.MapPost("/v1/systemone", async (HttpContext http) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(http.Request.Body))!;
            lock (seen) seen.Add(body);
            if (status != 200)
            {
                http.Response.StatusCode = status;
                http.Response.Headers["X-Request-Id"] = "req_42";
                await http.Response.WriteAsJsonAsync(error ?? new { error = new { message = "model is not loaded (unloaded).", code = "model_not_loaded" } });
                return;
            }
            var state = body["state"]!.GetValue<string>();
            var unsure = state.Contains("maybe");
            var answers = new JsonObject();
            foreach (var (id, q) in (JsonObject)body["questions"]!)
            {
                answers[id] = q!["type"]!.GetValue<string>() switch
                {
                    "noul" => new JsonObject { ["type"] = "noul", ["noul"] = unsure ? 0.55 : state.Contains("ERR") ? 0.97 : 0.04 },
                    "choice" => new JsonObject { ["type"] = "choice", ["choice"] = state.Contains("ERR") ? "error" : "info", ["confidence"] = unsure ? 0.2 : 0.9 },
                    _ => new JsonObject { ["type"] = "score", ["score"] = 1.5, ["confidence"] = 0.8 },
                };
            }
            // The server reports usage beside `answers`, not inside it: the only place cached_tokens exists, and the
            // whole cost argument for these calls depends on it being readable.
            await http.Response.WriteAsJsonAsync(new JsonObject
            {
                ["model"] = body["model"]!.GetValue<string>(), ["answers"] = answers,
                ["usage"] = new JsonObject { ["prompt_tokens"] = 4096, ["cached_tokens"] = 4000, ["completion_tokens"] = 3 },
            });
        });
    });

    public static void Register(TestRunner r)
    {
        r.Add("decide: shared admission is reused by a same-model caller; explicit bulk model is used only for batches", async () =>
        {
            var seen = new List<JsonObject>();
            await using var endpoint = await FakeKev(seen);
            var env = new Env(); await env.StartAsync(endpoint.Url);
            var model = new ModelInfo { Provider = "fake", Id = "local", IsLocal = true };
            env.Ctx.ModelsFake.Models.Add(model);
            var scheduler = new FakeAgentScheduler(); env.Ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
            using var held = await scheduler.AcquireAsync(new AgentSlotRequest { Key = model.Ref, AgentId = "caller" }, CancellationToken.None);
            var service = env.Ctx.Services.Get<IDecisionService>()!;
            var answer = await service.EvaluateAsync(new DecisionRequest
            {
                Model = model.Ref, HeldModel = model.Ref, ExistingLease = held,
                Body = new JsonObject { ["state"] = "ERR", ["questions"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "yes_no", ["question"] = "Is this an error?" } } },
            }, CancellationToken.None);
            Check.Equal(1, scheduler.Acquired.Count, "no second slot acquisition");
            Check.False(held.IsReleased, "the caller still owns its slot");
            Check.Equal("local", seen.Single()["model"]!.Str(), "wire model uses the backend ID");
            Check.True(answer["error"]!["noul"]!.GetValue<double>() > 0.9);
            env.Ctx.SettingsFake.Set("decide.bulkModel", JsonValue.Create("bulk"));
            env.Ctx.SettingsFake.Set("decide.bulkThreshold", JsonValue.Create(2));
            await env.Run(new { items = new[] { "a", "b" }, questions = new { error = "Is this an error?" } });
            Check.True(seen.Skip(1).All(b => b["model"]!.Str() == "bulk"));
            await env.Run(new { model = "explicit", items = new[] { "a", "b" }, questions = new { error = "Is this an error?" } });
            Check.True(seen.TakeLast(2).All(b => b["model"]!.Str() == "explicit"));
        });
        r.Add("decide: a file's lines each get every question; friendly questions become TypeSafe's; unsure lines are listed", async () =>
        {
            var seen = new List<JsonObject>();
            await using var kev = await FakeKev(seen);
            var env = new Env();
            await env.StartAsync(kev.Url);
            Check.True(env.Ctx.ToolsFake.Get("decide")!.Definition is { Category: "decide", ReadOnly: true });

            File.WriteAllLines(Path.Combine(env.Dir, "app.log"), ["INF started", "", "ERR disk full", "WRN maybe slow"]);
            var res = await env.Run(new
            {
                file = "app.log",
                questions = new
                {
                    level = new { type = "choice", question = "Which level?", options = new[] { "info", "error" } },
                    act = new { type = "yes_no", question = "A human must act." },
                    risk = new { type = "score", question = "How risky?", levels = new[] { "none", "some", "high" } },
                },
            });
            Check.False(res.IsError, res.Content);
            Check.Equal(3, seen.Count);
            Check.True(seen.All(b => b["model"]!.GetValue<string>() == "qwen3.8-27b"), "default model");
            var q = (JsonObject)seen[0]["questions"]!;
            Check.Equal("choice", q["level"]!["type"]!.GetValue<string>());
            Check.Equal("info", q["level"]!["criteria"]!["info"]!.GetValue<string>());
            Check.Equal("noul", q["act"]!["type"]!.GetValue<string>());
            Check.Equal("A human must act.", q["act"]!["instructions"]!.GetValue<string>());
            Check.Equal(3, q["risk"]!["criteria"]!.AsArray().Count);

            Check.Contains(res.Content, "qwen3.8-27b: 3 items × 3 questions");
            Check.Contains(res.Content, "level: info 2, error 1");
            Check.Contains(res.Content, "act: yes 2, no 1");
            Check.Contains(res.Content, "(mean score 1.50)");
            Check.Contains(res.Content, "[2] level=error 0.90 · act=yes 0.97");   // p(yes), not the distance from 0.5
            Check.Contains(res.Content, "[1] level=info 0.90 · act=no 0.96");     // p(no) = 1 − 0.04
            Check.Contains(res.Content, "[3] ? level=info 0.20");
            var d = NetPiJson.ToElement(res.Details);
            Check.Equal(1, d.GetProperty("unsure").GetInt32());
            Check.Equal("ERR disk full", d.GetProperty("items")[1].GetProperty("text").GetString());
            // What it cost, read from the server and totalled over the three items: 4000 of each item's 4096 prompt
            // tokens came from its cache.
            var usage = d.GetProperty("usage");
            Check.Equal(12288, usage.GetProperty("promptTokens").GetInt32());
            Check.Equal(12000, usage.GetProperty("cachedTokens").GetInt32());
            Check.Equal(9, usage.GetProperty("completionTokens").GetInt32());
            Check.Equal(0.9766, usage.GetProperty("cacheHitRate").GetDouble(), "12000 of 12288 prompt tokens came from its cache");
        });

        r.Add("decide: a repeated item is decided once; with embeddings a near-identical one shares the answer too", async () =>
        {
            foreach (var withEmbeddings in new[] { false, true })
            {
                var seen = new List<JsonObject>();
                await using var kev = await FakeKev(seen);
                var env = new Env();
                if (withEmbeddings) env.Ctx.ServicesFake.Register<IEmbeddingService>(new EmbeddingsTests.BagOfWords());
                await env.StartAsync(kev.Url);
                // Line 3 repeats line 1; line 4 is line 2 with other punctuation (the same words: cosine 1 to a bag of words).
                var res = await env.Run(new
                {
                    items = new[] { "ERR disk full on /data", "INF backup started", "ERR disk full on /data", "INF: backup started." },
                    questions = new { act = new { type = "yes_no", question = "Does a human need to act?" } },
                });
                Check.False(res.IsError, res.Content);
                Check.Equal(withEmbeddings ? 2 : 3, seen.Count, "decisions asked");
                Check.Contains(res.Content, "4 items × 1 question");
                Check.Contains(res.Content, withEmbeddings ? "(2 decided; 2 repeat an earlier item and share its answer)" : "(3 decided; 1 repeat");
                Check.Contains(res.Content, "[3] (= [1]) act=yes 0.97");
                var items = NetPiJson.ToElement(res.Details).GetProperty("items");
                Check.Equal(1, items[2].GetProperty("sameAs").GetInt32());
                if (withEmbeddings) Check.Equal(2, items[3].GetProperty("sameAs").GetInt32());
            }
        });

        r.Add("decide: errors keep the gateway's code and request id, with a hint for an unloaded model; bad questions are refused", async () =>
        {
            var seen = new List<JsonObject>();
            await using var kev = await FakeKev(seen, 404);
            var env = new Env();
            await env.StartAsync(kev.Url);

            var res = await env.Run(new { text = "rm -rf /", questions = new { danger = "This deletes data." }, model = "kev-4b" });
            Check.True(res.IsError);
            Check.Contains(res.Content, "kev-4b: HTTP 404 model_not_loaded");
            Check.Contains(res.Content, "(request req_42)");
            Check.Contains(res.Content, "Load kev-4b from AiHub");

            var bad = await env.Run(new { text = "x", questions = new { pick = new { type = "choice", options = new[] { "only" } } } });
            Check.True(bad.IsError);
            Check.Contains(bad.Content, "needs at least 2 options");
            Check.True((await env.Run(new { questions = new { a = "b" } })).IsError, "no items");
        });

        r.Add("decide: a llama.cpp error with a numeric code is reported, not thrown", async () =>
        {
            var seen = new List<JsonObject>();
            await using var kev = await FakeKev(seen, 500, new { error = new { code = 500, message = "model name=kev-9b failed to load", type = "server_error" } });
            var env = new Env();
            await env.StartAsync(kev.Url);

            var res = await env.Run(new { text = "x", questions = new { a = "Is it bad?" }, model = "kev-9b" });
            Check.True(res.IsError);
            Check.Contains(res.Content, "kev-9b: HTTP 500 http_500: model name=kev-9b failed to load");
        });

        r.Add("decide: the RPC decide.ask returns the raw answers", async () =>
        {
            var seen = new List<JsonObject>();
            await using var kev = await FakeKev(seen);
            var env = new Env();
            await env.StartAsync(kev.Url);
            var answers = (JsonObject)(await env.Ctx.Rpc.InvokeAsync("decide.ask",
                new { state = "ERR boom", questions = new { bad = new { type = "yes_no", question = "Is it bad?" } } }))!;
            Check.Equal(0.97, answers["bad"]!["noul"]!.GetValue<double>());
        });

        r.Add("decide: the RPC decide.decision posts NInfer's /v1/decision as given and keeps errors with their request id", async () =>
        {
            var seen = new List<JsonObject>();
            var fail = false;
            await using var ninfer = await LocalWeb.StartAsync(app => app.MapPost("/v1/decision", async (HttpContext http) =>
            {
                var body = (JsonObject)(await JsonNode.ParseAsync(http.Request.Body))!;
                lock (seen) seen.Add(body);
                if (fail)
                {
                    http.Response.StatusCode = 500;
                    http.Response.Headers["X-Request-Id"] = "gw-7";
                    await http.Response.WriteAsJsonAsync(new { error = new { message = "selected pressure target could not be sealed", type = "internal_error" } });
                    return;
                }
                await http.Response.WriteAsJsonAsync(new JsonObject
                {
                    ["object"] = "decision",
                    ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = new JsonObject { ["A"] = 0.9, ["B"] = 0.1 }, ["mass"] = 0.99 }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 120, ["cached_tokens"] = 100 },
                });
            }));
            var env = new Env();
            await env.StartAsync(ninfer.Url);

            var answer = (JsonObject)(await env.Ctx.Rpc.InvokeAsync("decide.decision", new
            {
                messages = new[] { new { role = "system", content = "Pick one." } },
                branches = new[] { new { id = "pick", content = "A or B?", labels = new[] { "A", "B" } } },
                share_state = false,
            }))!;
            Check.Equal(0.9, answer["branches"]![0]!["probabilities"]!["A"]!.GetValue<double>());
            Check.True(answer["ms"] is not null, "ms added");
            var sent = seen.Single();
            Check.Equal("qwen3.8-27b", sent["model"]!.GetValue<string>());
            Check.False(sent["enable_thinking"]!.GetValue<bool>());
            Check.False(sent["share_state"]!.GetValue<bool>());
            Check.Equal("A or B?", sent["branches"]![0]!["content"]!.GetValue<string>());

            fail = true;
            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.Rpc.InvokeAsync("decide.decision", new
            {
                messages = new[] { new { role = "user", content = "x" } },
                branches = new[] { new { content = "?", labels = new[] { "A", "B" } } },
            }));
            Check.Contains(ex.Message, "selected pressure target could not be sealed");
            Check.Contains(ex.Message, "(request gw-7)");
        });
    }
}
