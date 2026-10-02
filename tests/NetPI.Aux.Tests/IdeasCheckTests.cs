using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The checks that read a conversation: what they are asked, when they may run, and what they leave behind
/// (docs/plans/2026-09-29-ideas-flow-storage-handoff.md, assignment B). Every test here is about a decision or a
/// failure the old flow got wrong.
/// </summary>
public static class IdeasCheckTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public string ProjectDir { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public string Backlog => Path.Combine(Ctx.Paths.Home, "ideas.json");
        public List<JsonObject> Asked { get; } = [];

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            Ctx.ModelsFake.VerifierResponder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "{\"verified\":true,\"confidence\":0.95,\"reason\":\"The scripted conversation contains this unfinished plan\"}" }] };
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public Task StartAsync() => new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        /// <summary>A decision that answers with the given probabilities and records what it was asked.</summary>
        /// <summary>A scheduler stand-in: counts what took a slot and how long it waited.</summary>
        public FakeScheduler Scheduler { get; } = new();

        public void Decide(Func<JsonObject, Dictionary<string, double>> answer) =>
            Ctx.RpcFake.Register("decide.decision", (req, _) =>
            {
                var body = JsonObject.Create(req.Params.Clone())!;
                Asked.Add(body);
                var probs = new JsonObject();
                foreach (var (k, v) in answer(body)) probs[k] = v;
                return Task.FromResult<object?>(new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = probs }) });
            });

        /// <summary>A decision whose answer is not the shape we asked for.</summary>
        public void DecideRaw(Func<JsonObject, object?> answer) =>
            Ctx.RpcFake.Register("decide.decision", (req, _) =>
            {
                Asked.Add(JsonObject.Create(req.Params.Clone())!);
                return Task.FromResult(answer(JsonObject.Create(req.Params.Clone())!));
            });

        public void Says(string text) => Ctx.ModelsFake.Responder = _ => new ChatMessage
        {
            Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = text }],
        };

        /// <summary>Two user turns with answers, so there is a conversation to check.</summary>
        public void Talk(string user = "please look at the nudge counter", string answer = "On it.")
        {
            Ctx.SessionsFake.AppendMessage(Session.Id, ChatMessage.UserText(user));
            Ctx.SessionsFake.AppendMessage(Session.Id, new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = answer }],
            });
        }

        public async Task<JsonArray> Cards() => (JsonArray)(await Rpc("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray();

        /// <summary>Wait until there are <paramref name="count"/> cards (the check runs behind the closed tab).</summary>
        public async Task<JsonArray> WaitForCards(int count, int ms = 5000)
        {
            for (var waited = 0; waited < ms; waited += 25)
            {
                var cards = await Cards();
                if (cards.Count == count) return cards;
                await Task.Delay(25);
            }
            throw new AssertException($"expected {count} card(s), got {(await Cards()).Count}");
        }

        /// <summary>One conversation's check mark, as the store holds it.</summary>
        public JsonObject Mark(string sessionId) =>
            IdeasRepository.Open(Ctx.Data, Ctx.Access, Ctx.Log, Ctx.Paths.Home).Checks()
                .FirstOrDefault(m => m["sessionId"]!.Str() == sessionId)
            ?? new JsonObject { ["state"] = "none" };

        /// <summary>Wait until the check for this conversation reached a state that is not in flight.</summary>
        public async Task<JsonObject> WaitForMark(string sessionId, int ms = 5000)
        {
            for (var waited = 0; waited < ms; waited += 25)
            {
                var state = Mark(sessionId)["state"]?.Str();
                if (state is "done" or "failed") return Mark(sessionId);
                await Task.Delay(25);
            }
            throw new AssertException("the check never finished");
        }

        public List<string> IdeaTitles()
        {
            if (!File.Exists(Backlog)) return [];
            return ((JsonArray?)JsonNode.Parse(File.ReadAllText(Backlog))!["ideas"] ?? []).Select(i => i!["title"].Str()).ToList();
        }

        public async Task<List<string>> SessionsOn(string id)
        {
            var idea = (JsonObject)(await Rpc("ideas.get", new JsonObject { ["id"] = id }));
            return (idea["sessions"] as JsonArray ?? []).Select(s => s!["sessionId"].Str()).ToList();
        }
    }

    /// <summary>
    /// A scheduler that hands out <see cref="Busy"/> slots and records what waited for one. Mirrors the real one in
    /// what the Ideas plugin depends on: a key per pool, a slot that must be released, and a bounded wait.
    /// </summary>
    private sealed class FakeScheduler : IAgentScheduler
    {
        public int Busy { get; set; } = 1;
        public int Taken { get; set; }
        public int Waits { get; set; }
        public List<int> Priorities { get; } = [];
        public List<string> Labels { get; } = [];
        public bool Grant { get; set; } = true;

        private sealed class Slot(Action release) : IAgentSlot
        {
            public string Key { get; } = "fake";
            public string AgentId { get; } = "ideas";
            public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
            public bool IsReleased { get; private set; }
            public void Dispose() { IsReleased = true; release(); }
        }

        public string Resolve(ModelInfo model) => model.Ref;
        public string Resolve(ModelInfo model, string? agent) => model.Ref;
        public IReadOnlyList<AgentSlots> Snapshot() => [];
        public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease)
        {
            lock (this)
            {
                if (!Grant || Taken >= Busy) { lease = null; return false; }
                Taken++;
                Priorities.Add(request.Priority);
                Labels.Add(request.Label ?? "");
                lease = new Slot(() => { Taken--; });
                return true;
            }
        }
        public async ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
        {
            for (var waited = 0; ; waited += 20)
            {
                lock (this)
                {
                    if (Grant && Taken < Busy)
                    {
                        Taken++;
                        Waits++;
                        Priorities.Add(request.Priority);
                        Labels.Add(request.Label ?? "");
                        return new Slot(() => { Taken--; });
                    }
                    Waits++;
                }
                await Task.Delay(20, ct);
            }
        }
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas check: a final response still waits for the runtime and does not offer a plan the agent saved", async () =>
        {
            var env = new Env();
            var runtime = new FakeAgentRuntime();
            var agent = new AgentInfo { Id = "working", SessionId = env.Session.Id, Status = AgentStatus.Running };
            runtime.Agents.Add(agent);
            env.Ctx.ServicesFake.Register<IAgentRuntime>(runtime);
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Talk();
            env.Talk();
            env.Says("SAVE\nSaved by the agent\nThis plan is already in the backlog.");
            var previousStep = IdeaSaveCheck.DeferStep;
            IdeaSaveCheck.DeferStep = TimeSpan.FromMilliseconds(20);
            try
            {
                var result = await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
                Check.Equal("running", result["reason"].Str(), "a final assistant message does not mean the run has finished");
                Check.Equal(0, env.Ctx.ModelsFake.Requests.Count);
                await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id,
                    ["idea"] = new JsonObject { ["title"] = "Saved by the agent", ["summary"] = "Already recorded before returning idle." } });
                agent.Status = AgentStatus.Idle;
                await env.WaitForMark(env.Session.Id);
                Check.Equal(1, env.Ctx.ModelsFake.Requests.Count);
                Check.Equal(0, (await env.Cards()).Count, "no duplicate save card");
            }
            finally { IdeaSaveCheck.DeferStep = previousStep; env.Ctx.Unload(); }
        });

        r.Add("ideas check: the attach decision is asked about the conversation, not only the option list", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            var idea = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Nudge reset" } }))["id"].Str()!;
            env.Talk("the nudge keeps nudging after a good answer, please reset the counter");
            env.Talk("and make it configurable");
            env.Decide(_ => new() { ["A"] = 0.93, ["B"] = 0.06, ["C"] = 0.01 });
            env.Says("NOTHING");

            await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
            for (var waited = 0; waited < 200 && (await env.SessionsOn(idea)).Count == 0; waited += 25) await Task.Delay(25);
            Check.Equal(1, (await env.SessionsOn(idea)).Count, "the chat is attached");

            Check.Equal(1, env.Asked.Count, "one decision");
            var asked = env.Asked[0].ToJsonString();
            Check.Contains(asked, "the nudge keeps nudging after a good answer", "the conversation is in the request");
            Check.Contains(asked, "make it configurable", "including its later turns");
            env.Ctx.Unload();
        });

        r.Add("ideas check: 'none' wins and the save check still runs (the old flow threw before it)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            var idea = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Nudge reset" } }))["id"].Str()!;
            env.Talk();
            env.Talk();
            env.Decide(_ => new() { ["A"] = 0.05, ["B"] = 0.02, ["C"] = 0.93 }); // "none" is the last letter
            env.Says("SAVE\nUnsaved plan\nThe chat left this behind");

            var res = await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
            Check.Equal("started", res["reason"].Str());
            var cards = await env.WaitForCards(1);
            Check.Equal("Unsaved plan", cards[0]!["title"].Str());
            Check.Equal(0, (await env.SessionsOn(idea)).Count, "nothing was attached to an idea the chat was not about");
            env.Ctx.Unload();
        });

        r.Add("ideas check: an answer we cannot read is no crash, and the save check still runs", async () =>
        {
            foreach (var answer in new Func<JsonObject, object?>[]
            {
                _ => null,
                _ => new JsonObject(),
                _ => new JsonObject { ["branches"] = new JsonArray() },
                _ => new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick" }) },
                _ => new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = new JsonObject() }) },
                _ => new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = new JsonObject { ["Z"] = 0.99, ["Y"] = 0.01 } }) },
            })
            {
                var env = new Env();
                await env.StartAsync();
                env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
                await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Nudge reset" } });
                env.Talk();
                env.Talk();
                env.DecideRaw(answer);
                env.Says("SAVE\nStill offered\nThe save check is independent of the decision");

                var res = await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
                Check.Equal("started", res["reason"].Str());
                var cards = await env.WaitForCards(1);
                Check.Equal("Still offered", cards[0]!["title"].Str());
                env.Ctx.Unload();
            }
        });

        r.Add("ideas check: a check that failed is tried again, and gives up after a few tries", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Talk();
            env.Talk();
            env.Decide(_ => new() { ["A"] = 0.9, ["B"] = 0.05, ["C"] = 0.05 });

            var boom = 0;
            env.Ctx.ModelsFake.Responder = _ =>
            {
                if (++boom <= 2) throw new InvalidOperationException("the model is having a bad day");
                return new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "SAVE\nRecovered\nThe check ran once the model came back" }] };
            };

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
                var mark = await env.WaitForMark(env.Session.Id);
                Check.Equal("failed", mark["state"]!.Str(), $"attempt {attempt} is retryable");
                Check.Contains(mark["error"]!.Str(), "bad day");
            }
            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
            var cards = await env.WaitForCards(1);
            Check.Equal("Recovered", cards[0]!["title"].Str());
            Check.Equal("done", env.Mark(env.Session.Id)["state"]!.Str(), "a check that worked is done");
            Check.Equal(0, (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str() == "already" ? 0 : 1, "a done check is not repeated");
            env.Ctx.Unload();
        });

        r.Add("ideas check: a chat that was cut short and worked on since is checked again", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, ChatMessage.UserText("start the refactor"));
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "aborted", Parts = [new TextPart { Text = "…" }] });
            env.Talk("carried on and finished it", "Done, and here is what changed.");
            env.Says("SAVE\nWhat is left\nThe ending matters");

            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str(), "not skipped as unfinished");
            Check.Equal(1, (await env.WaitForCards(1)).Count);

            // A chat whose latest turn really was cut short is not checked at all.
            var cut = env.Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "cut", ProjectId = env.Project.Id });
            env.Ctx.SessionsFake.AppendMessage(cut.Id, ChatMessage.UserText("go"));
            env.Ctx.SessionsFake.AppendMessage(cut.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "aborted", Parts = [new TextPart { Text = "…" }] });
            env.Ctx.SessionsFake.AppendMessage(cut.Id, ChatMessage.UserText("again"));
            env.Ctx.SessionsFake.AppendMessage(cut.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "aborted", Parts = [new TextPart { Text = "…" }] });
            Check.Equal("unfinished", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = cut.Id }))["reason"].Str());
            env.Ctx.Unload();
        });

        r.Add("ideas check: a tab closed while the agent was still working is checked when the run ends", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Talk();
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, ChatMessage.UserText("and now the long one"));
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, new ChatMessage { Role = MessageRole.Assistant, Parts = [new ToolCallPart { Id = "c1", Name = "build" }] }); // no stop reason: the run is open
            env.Says("SAVE\nAfter the run\nThe check waited for the agent to finish");

            IdeaSaveCheck.DeferStep = TimeSpan.FromMilliseconds(100); // the waiting itself is not what is under test
            var res = await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
            Check.Equal("running", res["reason"].Str(), "the close is not held and the check is not lost");
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "Finished." }] });

            // The wait is a background loop; the fake session store keeps the messages, so the check runs after it.
            try
            {
                var cards = await env.WaitForCards(1);
                Check.Equal("After the run", cards[0]!["title"].Str());
            }
            catch (AssertException ex)
            {
                throw new AssertException(ex.Message + " | mark=" + env.Mark(env.Session.Id).ToJsonString());
            }
            IdeaSaveCheck.DeferStep = TimeSpan.FromSeconds(30);
            env.Ctx.Unload();
        });

        r.Add("ideas check: the digest keeps the end of a long conversation, not only its beginning", () =>
        {
            var messages = new List<ChatMessage>();
            for (var i = 0; i < 60; i++)
            {
                messages.Add(ChatMessage.UserText($"turn {i}: " + new string('x', 400)));
                messages.Add(new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = new string('y', 800) }] });
            }
            messages.Add(ChatMessage.UserText("in the end we cancelled the whole migration"));
            var digest = IdeaSaveCheck.Digest(messages);
            Check.True(digest.Length < 13000, "it fits the budget: " + digest.Length);
            Check.Contains(digest, "turn 0: x", "the beginning is there");
            Check.Contains(digest, "in the end we cancelled the whole migration", "and so is what came of it");
            Check.Contains(digest, "characters of the middle", "the gap is marked, not hidden");
        });

        r.Add("ideas check: a model that is not in the catalog is not quietly replaced by another one", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "openai", Id = "gpt-5", MaxOutputTokens = 16384 });
            env.Ctx.SettingsFake.Set("ideas.model", "aiproxy/not-in-the-catalog");
            env.Talk();
            env.Talk();
            env.Says("SAVE\nNever mind\nNot from the wrong model");

            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
            var mark = await env.WaitForMark(env.Session.Id);
            Check.Equal(0, env.Ctx.ModelsFake.Requests.Count, "no model was called at all");
            Check.Equal("failed", mark["state"]!.Str());
            Check.Contains(mark["error"]!.Str(), "not in the catalog");
            Check.Contains(env.Ctx.Log.Lines.FirstOrDefault(l => l.Contains("not in the catalog")) ?? "", "not replaced", "and the user is told");
            env.Ctx.Unload();
        });

        r.Add("ideas check: the cheapest effort it has, never a step up", () =>
        {
            var withLow = new ModelInfo { Provider = "p", Id = "m", Reasoning = new ReasoningInfo { Supported = true, Efforts = ["none", "low", "high"] } };
            var withHigh = new ModelInfo { Provider = "p", Id = "m", Reasoning = new ReasoningInfo { Supported = true, Efforts = ["medium", "high"] } };
            var none = new ModelInfo { Provider = "p", Id = "m", Reasoning = new ReasoningInfo { Supported = true, Efforts = ["none", "off"] } };
            Check.Equal("low", IdeaSaveCheck.EffortFor(withLow));
            Check.Equal(null, IdeaSaveCheck.EffortFor(withHigh), "no low: the model's default, never 'high'");
            Check.Equal("none", IdeaSaveCheck.EffortFor(none));
            Check.Equal(null, IdeaSaveCheck.EffortFor(new ModelInfo { Provider = "p", Id = "m" }), "no reasoning at all");
        });

        r.Add("ideas check: ideas past the 51st are still eligible for recall", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            for (var i = 0; i < 60; i++)
                await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = $"Filler idea number {i}" } });
            var last = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject
            {
                ["title"] = "Rework the transcript indexer",
                ["summary"] = "The transcript indexer is slow on long sessions.",
            } }))["id"].Str()!;
            env.Decide(body =>
            {
                // The model answers for the option whose title is the one we want (the options are in the system prompt).
                var options = body["messages"]![0]!["content"]!.Str();
                var letter = options.Split('\n')
                    .FirstOrDefault(l => l.Contains("Rework the transcript indexer", StringComparison.Ordinal))?[..1];
                Check.True(letter is { Length: 1 }, "the idea past the 51st was offered to the model");
                return new() { [letter!] = 0.91, ["none"] = 0.02 };
            });

            var res = await env.Rpc("ideas.recall", new JsonObject { ["sessionId"] = env.Session.Id, ["text"] = "the transcript indexer is slow, can we rework it" });
            Check.Equal("model", res["reason"].Str(), "it is found: " + res.ToJsonString());
            Check.Equal(last, res["match"]!["id"].Str());
            env.Ctx.Unload();
        });

        r.Add("ideas check: background model work waits for a slot, and work that does not get one is dropped, not run anyway", async () =>
        {
            var env = new Env();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(env.Scheduler);
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Talk();
            env.Talk();
            env.Says("SAVE\nA card\nWith the scheduler there");

            await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
            await env.WaitForCards(1);
            Check.Equal(0, env.Scheduler.Taken, "and handed back");
            Check.True(env.Scheduler.Priorities.Count > 0, "a slot was asked for");
            Check.True(env.Scheduler.Priorities.All(p => p < 0), "background work queues behind the chats: " + string.Join(",", env.Scheduler.Priorities));
            Check.True(env.Scheduler.Labels.Any(l => l.Contains("save check")), "and says what it is: " + string.Join(" | ", env.Scheduler.Labels));

            // Both slots busy: the check waits, and then it is dropped. Running it without a slot is what let a sweep, a
            // save check and a recall put three calls on a two-slot model, so the bound is the whole point.
            env.Scheduler.Busy = 0;
            env.Ctx.SettingsFake.Set("ideas.checkWaitSeconds", 1);
            env.Says("SAVE\nA second card\nThis one must not run without a slot");
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, ChatMessage.UserText("and one more"));
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "Done." }] });
            IdeaSaveCheck.DeferStep = TimeSpan.FromMilliseconds(50);
            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
            var mark = await env.WaitForMark(env.Session.Id, 8000);
            Check.Equal("failed", mark["state"]!.Str(), "a dropped check is retryable, not done");
            Check.Contains(mark["error"]!.Str(), "dropped", "and the mark says why: " + mark["error"]!.Str());
            Check.Equal(1, (await env.Cards()).Count, "no card from a check that never ran");
            Check.Equal(0, env.Scheduler.Taken, "and it never held a slot");
            Check.Contains(string.Join("|", env.Ctx.Log.Lines), "was dropped", "the log names the work and the reason");
            Check.Equal(2, env.Ctx.ModelsFake.Requests.Count, "only the first draft and its verifier called the model");

            // The queue opens again: the next close of the same conversation runs the check for real.
            env.Scheduler.Busy = 1;
            env.Says("SAVE\nRecovered\nThe retry ran");
            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
            var cards = await env.WaitForCards(2);
            Check.Equal("Recovered", cards[1]!["title"].Str());
            Check.Equal("done", env.Mark(env.Session.Id)["state"]!.Str());
            IdeaSaveCheck.DeferStep = TimeSpan.FromSeconds(30);
            env.Ctx.Unload();
        });

        r.Add("ideas check: a background check is never sent to a paid model on its own", async () =>
        {
            var env = new Env();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(env.Scheduler);
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "openai", Id = "gpt-5", IsLocal = false, MaxOutputTokens = 16384 });
            env.Ctx.SettingsFake.Set("ideas.model", "openai/gpt-5");
            env.Talk();
            env.Talk();
            env.Says("SAVE\nNever mind\nThis would cost money");

            Check.Equal("started", (await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id }))["reason"].Str());
            var mark = await env.WaitForMark(env.Session.Id);
            Check.Equal(0, env.Ctx.ModelsFake.Requests.Count, "no model was called");
            Check.Equal(0, (await env.Cards()).Count, "and no card");
            Check.Equal(0, env.Scheduler.Taken, "and it never took a slot");
            Check.Contains(string.Join("|", env.Ctx.Log.Lines), "paid model", "the log says why");
            env.Ctx.Unload();
        });

        r.Add("ideas check: the same chat checked twice leaves one card", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            env.Talk();
            env.Talk();
            env.Says("SAVE\nOne card only\nThe same plan, offered again");

            await env.Rpc("ideas.closed", new JsonObject { ["sessionId"] = env.Session.Id });
            await env.WaitForCards(1);
            // A retry on the same conversation revision (a reload, a restart) does not stack a second card.
            env.Ctx.SessionsFake.AppendMessage(env.Session.Id, new ChatMessage { Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "Done." }] });
            env.Ctx.Unload();
            await env.StartAsync();
            Check.Equal(1, (await env.Cards()).Count);
            env.Ctx.Unload();
        });
    }
}
