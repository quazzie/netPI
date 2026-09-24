using System.Text.Json.Nodes;
using NetPI.Compaction;

namespace NetPI.Aux.Tests;

public static class CompactionTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new();
        public SessionInfo Session { get; }
        public ModelInfo Model { get; }
        public CompactionService Service { get; }
        public CompactionHook Hook { get; }
        public int Reloads;

        public Env(int window = 40_000)
        {
            Model = T.Model("m1", window, "test", "low", "medium", "high");
            Ctx.ModelsFake.Models.Add(Model);
            Ctx.ModelsFake.DefaultModelRef = Model.Ref;
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", Model = Model.Ref });
            Service = new CompactionService(Ctx);
            Hook = new CompactionHook(Service, Ctx.Logger);
        }

        public ChatMessage Add(ChatMessage m) => Ctx.SessionsFake.AppendMessage(Session.Id, m);

        /// <summary>rounds × (user 2000 chars, assistant+call, tool result 8000 chars, assistant 1000 chars) ≈ 2770 tokens each.</summary>
        public void Conversation(int rounds, string tag = "")
        {
            for (var i = 0; i < rounds; i++)
            {
                Add(T.User($"{tag}goal {i}: " + new string('u', 2000)));
                var a = T.Assistant($"Reading file {i}", T.Call($"c{tag}{i}", "read", $"{{\"path\":\"src/f{i}.cs\"}}"));
                a.Parts.Insert(0, new ThinkingPart { Text = "SECRET_THINKING " + i });
                Add(a);
                Add(T.ToolResult(($"c{tag}{i}", "read", "R" + new string('r', 7999))));
                Add(T.Assistant($"Done with {i}. " + new string('a', 1000)));
            }
        }

        public AgentTurnContext Turn(int index = 0, long lastContextTokens = 0)
        {
            var run = T.Run(Ctx, Model, Session);
            AgentTurnContext? turn = null;
            turn = T.Turn(run, Ctx.Sessions.GetContextMessages(Session.Id).ToList(), index: index, reload: () =>
            {
                Reloads++;
                turn!.Messages = Ctx.Sessions.GetContextMessages(Session.Id).ToList();
                return Task.CompletedTask;
            });
            turn.LastContextTokens = lastContextTokens;
            return turn;
        }
    }

    // ------------------------------------------------------------------ planner property test

    private static List<ChatMessage> RandomConversation(Random rnd)
    {
        var list = new List<ChatMessage>();
        var calls = 0;
        var rounds = rnd.Next(1, 8);
        for (var r = 0; r < rounds; r++)
        {
            list.Add(T.User(new string('u', rnd.Next(10, 3000))));
            var toolRounds = rnd.Next(0, 5);
            for (var k = 0; k < toolRounds; k++)
            {
                var n = rnd.Next(1, 4);
                var cs = Enumerable.Range(0, n).Select(_ => T.Call("c" + calls++, "read", "{\"path\":\"" + new string('p', rnd.Next(5, 200)) + "\"}")).ToArray();
                list.Add(T.Assistant(rnd.Next(2) == 0 ? "" : new string('t', rnd.Next(1, 500)), cs));
                if (rnd.Next(3) == 0)
                {
                    // Results in separate messages, sometimes with a steering notice in between.
                    for (var j = 0; j < cs.Length; j++)
                    {
                        list.Add(T.ToolResult((cs[j].Id, "read", new string('r', rnd.Next(10, 6000)))));
                        if (j == 0 && cs.Length > 1 && rnd.Next(2) == 0) list.Add(ChatMessage.NoticeText("steer", "steer"));
                    }
                }
                else list.Add(T.ToolResult(cs.Select(c => (c.Id, "read", new string('r', rnd.Next(10, 6000)))).ToArray()));
            }
            list.Add(T.Assistant(new string('a', rnd.Next(1, 800))));
        }
        for (var i = 0; i < list.Count; i++) list[i].Seq = i + 1;
        return list;
    }

    public static void Register(TestRunner r)
    {
        r.Add("compaction: planner never splits tool calls from their results (randomized)", () =>
        {
            var rnd = new Random(1234);
            var plans = 0;
            for (var iter = 0; iter < 600; iter++)
            {
                var msgs = RandomConversation(rnd);
                if (iter % 5 == 0)
                {
                    var s = new ChatMessage { Role = MessageRole.Summary, Seq = 10_000, Parts = [new TextPart { Text = "old summary" }] };
                    msgs.Insert(0, s);
                }
                var total = CompactionPlanner.Estimate(msgs);
                var keep = rnd.Next(50, (int)Math.Max(60, total));
                var plan = CompactionPlanner.Plan(msgs, keep, keep * 3 / 2);
                if (plan is null) continue;
                plans++;
                var kept = plan.Kept.ToList();
                var first = kept[0];
                Check.True(first.Role is MessageRole.User or MessageRole.Notice or MessageRole.Assistant, $"first kept role {first.Role}");
                var keptCalls = kept.Where(m => m.Role == MessageRole.Assistant).SelectMany(m => m.ToolCalls).Select(c => c.Id).ToHashSet();
                foreach (var tr in kept.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults))
                    Check.True(keptCalls.Contains(tr.CallId), $"orphan tool result {tr.CallId} (iteration {iter})");
                var lastSummarized = plan.ToSummarize.Last(m => m.Role != MessageRole.Summary);
                Check.Equal(lastSummarized.Seq, plan.UpToSeq);
                Check.Equal(total, plan.SummarizeTokens + plan.KeptTokens);
                Check.True(plan.ToSummarize.Any(m => m.Role != MessageRole.Summary), "summarizes at least one real message");
                if (msgs[0].Role == MessageRole.Summary) Check.True(plan.ToSummarize.First().Role == MessageRole.Summary, "old summary is folded in");
            }
            Check.True(plans > 300, $"only {plans} plans");
        });

        r.Add("compaction: planner prefers a User boundary and handles edge cases", () =>
        {
            var msgs = new List<ChatMessage>
            {
                T.User(new string('u', 4000)),                                      // 1000
                T.Assistant("x", T.Call("a", "read")), T.ToolResult(("a", "read", new string('r', 4000))),
                T.User(new string('u', 400)),                                       // user boundary
                T.Assistant("y", T.Call("b", "read")), T.ToolResult(("b", "read", new string('r', 4000))),
                T.Assistant(new string('z', 400)),
            };
            for (var i = 0; i < msgs.Count; i++) msgs[i].Seq = i + 1;
            var plan = CompactionPlanner.Plan(msgs, 1000, 1600)!;
            Check.Equal(3, plan.CutIndex);
            Check.Equal(MessageRole.User, plan.Kept.First().Role);
            Check.Equal(3L, plan.UpToSeq);

            // No User boundary within reach → an Assistant boundary, never the tool result.
            var plan2 = CompactionPlanner.Plan(msgs, 1000, 1000)!;
            Check.Equal(MessageRole.Assistant, plan2.Kept.First().Role);

            // Everything fits / a single message → nothing to compact.
            Check.True(CompactionPlanner.Plan([msgs[0]], 10, 10) is null);
            Check.True(CompactionPlanner.Plan([new ChatMessage { Role = MessageRole.Summary, Seq = 9, Parts = [new TextPart { Text = "s" }] }, msgs[0]], 10, 10) is null);

            // The last message alone exceeds the budget but is a tool result → cut at its assistant.
            var big = new List<ChatMessage>
            {
                T.User("a"), T.Assistant("b"), T.User("c"), T.Assistant("d", T.Call("q", "read")), T.ToolResult(("q", "read", new string('r', 40_000))),
            };
            for (var i = 0; i < big.Count; i++) big[i].Seq = i + 1;
            var plan3 = CompactionPlanner.Plan(big, 1000, 1500)!;
            Check.Equal(MessageRole.Assistant, plan3.Kept.First().Role);
            Check.Equal("d", plan3.Kept.First().Text);
            Check.Equal(2, plan3.Kept.Count());

            // Summary returned last by a store is moved to the front.
            var reordered = new List<ChatMessage>(msgs) { new() { Role = MessageRole.Summary, Seq = 100, Parts = [new TextPart { Text = "S" }] } };
            var plan4 = CompactionPlanner.Plan(reordered, 1000, 1600)!;
            Check.Equal(MessageRole.Summary, plan4.ToSummarize.First().Role);
        });

        r.Add("compaction: transcript serialization (roles, tool calls, truncation, no thinking)", () =>
        {
            var a = T.Assistant("I will read it", T.Call("c1", "read", "{\"path\":\"a.cs\"}"));
            a.Parts.Insert(0, new ThinkingPart { Text = "PRIVATE" });
            var s = TranscriptSerializer.Serialize(a);
            Check.Equal("[Assistant]\nI will read it\n[Tool call] read({\"path\":\"a.cs\"})", s);
            var tool = new ChatMessage
            {
                Role = MessageRole.Tool,
                Parts = [new ToolResultPart { CallId = "c1", Name = "bash", Content = new string('x', 5000), IsError = true }],
            };
            var ts = TranscriptSerializer.Serialize(tool);
            Check.Contains(ts, "[Tool result: bash (error)]");
            Check.Contains(ts, "chars omitted");
            Check.True(ts.Length < 2200, $"length {ts.Length}");
            Check.Equal("[Notice: project]\nmoved", TranscriptSerializer.Serialize(ChatMessage.NoticeText("moved", "project")));
            var u = T.User("look");
            u.Parts.Add(new ImagePart { Data = "AAAA" });
            Check.Contains(TranscriptSerializer.Serialize(u), "[1 image(s) attached]");
            var chunks = TranscriptSerializer.Chunk([new string('a', 600), new string('b', 3000), new string('c', 600)], 1000);
            Check.True(chunks.Count == 3 && chunks.All(c => c.Length <= 1000), string.Join("|", chunks.Select(c => c.Length)));
            Check.Equal(1, TranscriptSerializer.Chunk(["aa", "bb", "cc"], 1000).Count);
        });

        r.Add("compaction: auto-compaction before a model call", async () =>
        {
            var env = new Env();
            env.Conversation(13);
            var session = env.Session;
            var turn = env.Turn();
            var countBefore = turn.Messages.Count;
            await env.Hook.OnBeforeModelCallAsync(turn);

            var req = env.Ctx.ModelsFake.Requests.Single();
            Check.Equal("compaction", req.Purpose);
            Check.Equal(CompactionService.SystemPrompt, req.SystemPrompt);
            Check.Equal("low", req.ReasoningEffort);
            Check.Equal(8192, req.MaxOutputTokens);
            Check.Equal(0, req.Tools.Count);
            Check.Equal(session.Id, req.SessionId);
            var prompt = req.Messages.Single().Text;
            Check.Equal(MessageRole.User, req.Messages[0].Role);
            Check.Contains(prompt, "[User]\ngoal 0: uuu");
            Check.Contains(prompt, "[Tool call] read({\"path\":\"src/f0.cs\"})");
            Check.Contains(prompt, "[Tool result: read]\nRrrr");
            Check.Contains(prompt, "chars omitted");
            Check.NotContains(prompt, "SECRET_THINKING");
            foreach (var h in new[] { "## Goal", "## Constraints & preferences", "## Progress", "### Done", "### In progress", "## Key decisions", "## Files & code touched", "## Current state & next steps", "## Open questions" })
                Check.Contains(prompt, h);

            var store = env.Ctx.SessionsFake;
            var (sid, upTo) = store.MarkCompactedCalls.Single();
            Check.Equal(session.Id, sid);
            var summary = store.Appended.Last();
            Check.Equal(MessageRole.Summary, summary.Role);
            Check.Equal("## Goal\nSUMMARY", summary.Text);
            Check.Equal("compaction", summary.MetaString("kind"));
            Check.Equal(upTo, (long)summary.Meta!["coversUpToSeq"]!);
            var before = (long)summary.Meta["tokensBefore"]!;
            var after = (long)summary.Meta["tokensAfter"]!;
            Check.True(before > 32_000 && after < 16_000 && after > 10_000, $"{before} → {after}");
            // The first kept message is a user message right after the compacted range.
            var firstKept = store.Messages.First(m => m.Seq == upTo + 1);
            Check.Equal(MessageRole.User, firstKept.Role);
            Check.True(store.Messages.Where(m => m.Seq <= upTo).All(m => m.Compacted));

            Check.Equal(1, env.Reloads);
            Check.Equal(MessageRole.Summary, turn.Messages[0].Role);
            Check.True(turn.Messages.Count < countBefore);
            Check.Equal(after, turn.LastContextTokens);
            Check.Equal(after, session.ContextTokens);
            var notices = env.Ctx.Bus.OfType(EventTypes.AgentNotice);
            Check.Contains(((JsonObject)notices.Last().Data!)["text"].Str(), "Context compacted: ~");
            Check.Equal(session.Id, notices.Last().SessionId);
            Check.Equal(1, env.Ctx.Bus.OfType(EventTypes.SessionContext).Count);

            // Right after compaction the hook does nothing (below threshold).
            await env.Hook.OnBeforeModelCallAsync(env.Turn(1));
            Check.Equal(1, env.Ctx.ModelsFake.Requests.Count);
        });

        r.Add("compaction: no compaction below the threshold; usage-based estimate triggers it", async () =>
        {
            var env = new Env();
            env.Conversation(4); // ≈ 11k tokens of 40k
            await env.Hook.OnBeforeModelCallAsync(env.Turn());
            Check.Equal(0, env.Ctx.ModelsFake.Requests.Count);

            // The provider reported a much bigger context (e.g. tokenizer ≠ chars/4) → compact.
            var turn = env.Turn(lastContextTokens: 0);
            var lastAssistant = turn.Messages.Last(m => m.Role == MessageRole.Assistant);
            lastAssistant.Usage = new Usage { InputTokens = 36_000, OutputTokens = 200 };
            turn.LastContextTokens = 36_200;
            Check.True(CompactionHook.EstimateContext(turn, 0) >= 36_200);
            await env.Hook.OnBeforeModelCallAsync(turn);
            Check.Equal(1, env.Ctx.ModelsFake.Requests.Count);

            // Usage recorded before the latest summary is stale and ignored.
            var stale = env.Turn(lastContextTokens: 36_200);
            var a = stale.Messages.Last(m => m.Role == MessageRole.Assistant);
            a.Usage = new Usage { InputTokens = 36_000 };
            Check.True(stale.Messages.Any(m => m.Role == MessageRole.Summary));
            Check.True(CompactionHook.EstimateContext(stale, 0) < 20_000, "stale usage ignored");
        });

        r.Add("compaction: settings (disabled, threshold, reserve)", async () =>
        {
            var o = new CompactionOptions();
            Check.True(CompactionService.ShouldCompact(104_858, 131_072, o));
            Check.False(CompactionService.ShouldCompact(104_000, 131_072, o));
            Check.True(CompactionService.ShouldCompact(115_000, 131_072, new CompactionOptions { ThresholdPercent = 0.95 }));
            Check.False(CompactionService.ShouldCompact(3_000, 8_192, o)); // reserve capped at half the window
            var env = new Env();
            env.Ctx.SettingsFake.Set("compaction.thresholdPercent", 95);
            Check.Equal(0.95, env.Service.Options().ThresholdPercent);
            env.Ctx.SettingsFake.Set("compaction.enabled", false);
            env.Conversation(13);
            await env.Hook.OnBeforeModelCallAsync(env.Turn());
            Check.Equal(0, env.Ctx.ModelsFake.Requests.Count);
            Check.Equal(131_072, CompactionService.WindowFor(T.Model(window: null), new CompactionOptions()));
        });

        r.Add("compaction: context overflow → aggressive compaction and retry (once per turn)", async () =>
        {
            var env = new Env();
            env.Conversation(8); // below the threshold: the provider still said "too long"
            var turn = env.Turn(index: 3);
            var d = await env.Hook.OnModelErrorAsync(turn, new ModelException("prompt is too long", false, 400) { ContextOverflow = true });
            Check.True(d is { Retry: true });
            var summary = env.Ctx.SessionsFake.Appended.Last();
            Check.Equal("overflow", summary.MetaString("mode"));
            // keepRecent halved: ≈ 6000 tokens kept.
            Check.True((long)summary.Meta!["tokensAfter"]! < 9_000, summary.Meta.ToJsonString());
            Check.Equal(1, env.Reloads);
            // Same turn again → give up (no loop).
            Check.True(await env.Hook.OnModelErrorAsync(turn, new ModelException("too long", false) { ContextOverflow = true }) is null);
            // Other errors are not ours.
            Check.True(await env.Hook.OnModelErrorAsync(env.Turn(index: 4), new ModelException("rate limit", true, 429)) is null);
            Check.True(CompactionHook.IsContextOverflow(new InvalidOperationException("x", new ModelException("y", false) { ContextOverflow = true })));
        });

        // E2E regression: when the backend's real window is smaller than the advertised one, overflow compaction kept
        // "the last keepRecent/2 tokens" of the advertised window, i.e. nearly everything, and the retry overflowed again.
        r.Add("compaction: overflow when the real window is smaller than advertised still frees most of the context", async () =>
        {
            var env = new Env(window: 200_000);
            env.Conversation(4); // ≈ 11k tokens: far below the advertised window, but the backend said "too long"
            var before = CompactionPlanner.Estimate(env.Ctx.Sessions.GetContextMessages(env.Session.Id));
            var d = await env.Hook.OnModelErrorAsync(env.Turn(index: 2), new ModelException("exceeds the available context size", false, 400) { ContextOverflow = true });
            Check.True(d is { Retry: true }, "retry after compaction");
            var after = CompactionPlanner.Estimate(env.Ctx.Sessions.GetContextMessages(env.Session.Id));
            Check.True(after <= before * 6 / 10, $"messages shrank to at most 60%: {before} → {after}");
        });

        // E2E regression: on a 12k window where the system prompt and 36 tool schemas took 7k, compaction kept 30 % of the
        // window of messages, the context stayed ~88 % full and the next tool result overflowed it.
        r.Add("compaction: what every call sends (prompt, tools) leaves less room for kept messages", async () =>
        {
            var env = new Env(window: 12_000);
            env.Conversation(3); // ≈ 8.3k tokens of messages
            var result = await env.Service.CompactAsync(new CompactionRequest
            {
                SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Auto, OverheadTokens = 7_000, TokensBefore = 15_300,
            }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);
            var kept = CompactionPlanner.Estimate(env.Ctx.Sessions.GetContextMessages(env.Session.Id).Where(m => m.Role != MessageRole.Summary));
            Check.True(kept <= 1_500, $"kept the latest exchange only: {kept} tokens");
            Check.True(result.TokensAfter <= 12_000 * 7 / 10, $"room to work after compacting: {result.TokensAfter}");

            // a large window keeps its full share
            var big = new Env(window: 200_000);
            big.Conversation(12); // ≈ 33k tokens
            var r2 = await big.Service.CompactAsync(new CompactionRequest
            {
                SessionId = big.Session.Id, Model = big.Model, Mode = CompactionMode.Auto, OverheadTokens = 7_000, TokensBefore = 170_000,
            }, CancellationToken.None);
            var keptBig = CompactionPlanner.Estimate(big.Ctx.Sessions.GetContextMessages(big.Session.Id).Where(m => m.Role != MessageRole.Summary));
            Check.True(r2.Compacted && keptBig >= 19_000, $"keeps compaction.keepRecentTokens (20k): {keptBig}");
        });

        r.Add("compaction: chunked (rolling) summaries when the transcript exceeds the summarizer window", async () =>
        {
            var env = new Env();
            var small = T.Model("small", 12_000, "test");
            env.Ctx.ModelsFake.Models.Add(small);
            env.Ctx.SettingsFake.Set("compaction.model", "test/small");
            var lanes = new FakeLaneScheduler();
            env.Ctx.ServicesFake.Register<ILaneScheduler>(lanes);
            var n = 0;
            env.Ctx.ModelsFake.Responder = req => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = $"ROLLING-{Interlocked.Increment(ref n)}" }],
            };
            env.Conversation(13);
            await env.Hook.OnBeforeModelCallAsync(env.Turn());

            var reqs = env.Ctx.ModelsFake.Requests.ToList();
            Check.True(reqs.Count >= 3, $"{reqs.Count} summarizer calls");
            Check.True(reqs.All(q => q.Model.Id == "small" && q.ReasoningEffort is null && q.MaxOutputTokens == 3000));
            Check.NotContains(reqs[0].Messages[0].Text, "<previous-summary>");
            Check.Contains(reqs[0].Messages[0].Text, $"part 1 of {reqs.Count}");
            for (var i = 1; i < reqs.Count; i++)
            {
                var p = reqs[i].Messages[0].Text;
                Check.Contains(p, $"<previous-summary>\nROLLING-{i}\n</previous-summary>");
                Check.True(ModelMessages.EstimateTokens(p) < 12_000 - 3000, $"chunk {i} fits: {ModelMessages.EstimateTokens(p)}");
            }
            Check.Equal($"ROLLING-{reqs.Count}", env.Ctx.SessionsFake.Appended.Last().Text);
            // A different model than the agent's → its own lane, released afterwards.
            Check.Equal("test/small", lanes.Acquired.Single().PoolKey);
            Check.Equal("agt_1", lanes.Acquired.Single().AgentId);
            Check.Equal(1, lanes.Released);
        });

        r.Add("compaction: summarizer failure never breaks the run", async () =>
        {
            var env = new Env();
            env.Ctx.ModelsFake.Responder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new ThinkingPart { Text = "..." }], StopReason = "length" };
            env.Conversation(13);
            await env.Hook.OnBeforeModelCallAsync(env.Turn());
            Check.Equal(0, env.Ctx.SessionsFake.MarkCompactedCalls.Count);
            Check.Contains(((JsonObject)env.Ctx.Bus.OfType(EventTypes.AgentNotice).Last().Data!)["text"].Str(), "Auto-compaction failed");
        });

        r.Add("compaction: compaction.run RPC, /compact command, busy agent, lanes", async () =>
        {
            var env = new Env();
            var ctx = env.Ctx;
            await new CompactionPlugin().StartAsync(ctx, CancellationToken.None);
            Check.True(ctx.Services.GetAll<IAgentHook>().Single() is CompactionHook { Order: -100 });
            var cmd = ctx.UiFake.CommandList.Single();
            Check.Equal("compact", cmd.Name);
            Check.Equal("compaction.run", cmd.Rpc);
            Check.Equal("Summarize older messages to free context", cmd.Description);

            var lanes = new FakeLaneScheduler();
            ctx.ServicesFake.Register<ILaneScheduler>(lanes);
            var runtime = new FakeAgentRuntime();
            ctx.ServicesFake.Register<IAgentRuntime>(runtime);

            env.Add(T.User("hi"));
            Check.Contains((string?)await ctx.RpcFake.Call("compaction.run", new JsonObject { ["sessionId"] = env.Session.Id }), "Nothing to compact");

            env.Conversation(3);
            runtime.Agents.Add(new AgentInfo { Id = "agt_9", SessionId = env.Session.Id, Status = AgentStatus.Running });
            var busy = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("compaction.run", new JsonObject { ["sessionId"] = env.Session.Id }));
            Check.Equal("busy", busy.Code);

            runtime.Agents[0].Status = AgentStatus.Idle;
            var result = (string?)await ctx.RpcFake.Call("compaction.run", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "focus on the parser" });
            Check.Contains(result, "Context compacted: ~");
            var req = ctx.ModelsFake.Requests.Last();
            Check.Contains(req.Messages[0].Text, "Additional focus requested by the user: focus on the parser");
            Check.Equal("agt_9", req.AgentId);
            Check.Equal("manual", ctx.SessionsFake.Appended.Last().MetaString("mode"));
            Check.Equal("test/m1", lanes.Acquired.Single().PoolKey);
            Check.Equal("compaction", lanes.Acquired.Single().Label);
            Check.Equal(1, lanes.Released);

            var nf = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("compaction.run", new JsonObject { ["sessionId"] = "ses_nope" }));
            Check.Equal("not_found", nf.Code);
        });

        r.Add("compaction: a second compaction folds the previous summary in", async () =>
        {
            var env = new Env();
            env.Conversation(13, "A");
            await env.Hook.OnBeforeModelCallAsync(env.Turn());
            var store = env.Ctx.SessionsFake;
            var s1 = store.Appended.Last();
            Check.Equal(MessageRole.Summary, s1.Role);
            env.Conversation(2, "B");

            // Manual compaction keeps less than the kept range of the first one: its cut lies before s1's seq.
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);
            var prompt = env.Ctx.ModelsFake.Requests.Last().Messages[0].Text;
            Check.Contains(prompt, "[Summary of the earlier conversation]\n## Goal\nSUMMARY");
            Check.True(s1.Compacted, "previous summary marked compacted");
            var ctxMsgs = store.GetContextMessages(env.Session.Id);
            Check.Equal(1, ctxMsgs.Count(m => m.Role == MessageRole.Summary));
            Check.Equal(result.Summary!.Id, ctxMsgs[0].Id);
        });

        r.Add("compaction: lowest reasoning effort and token formatting", () =>
        {
            Check.Equal("minimal", CompactionService.LowestEffort(T.Model(efforts: ["high", "minimal", "low"])));
            Check.Equal("none", CompactionService.LowestEffort(T.Model(efforts: ["none", "low"])));
            Check.Equal("xhigh", CompactionService.LowestEffort(T.Model(efforts: ["xhigh"])));
            Check.True(CompactionService.LowestEffort(T.Model()) is null);
            Check.Equal("950", CompactionService.Fmt(950));
            Check.Equal("12.3k", CompactionService.Fmt(12_345));
            Check.Equal("131k", CompactionService.Fmt(131_072));
        });
    }
}
