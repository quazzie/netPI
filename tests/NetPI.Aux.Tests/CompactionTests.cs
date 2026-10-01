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
        r.Add("compaction: resolve the effective named-agent model without silently switching models", async () =>
        {
            var e = new Env();
            var selected = T.Model("selected", 80_000, "test", "high");
            e.Ctx.ModelsFake.Models.Add(selected);
            e.Ctx.Settings.Set("agents.research.model", JsonValue.Create(selected.Ref));
            e.Session.Model = null;
            e.Session.Meta = new JsonObject { [SessionAgent.MetaKey] = "research" };
            Check.Equal(selected.Ref, (await e.Service.ResolveSessionModelAsync(e.Session, default))?.Ref);
            e.Ctx.ModelsFake.DefaultModelRef = null;
            Check.Equal(selected.Ref, (await e.Service.ResolveSessionModelAsync(e.Session, default))?.Ref);
            e.Session.Model = e.Model.Ref;
            Check.Equal(e.Model.Ref, (await e.Service.ResolveSessionModelAsync(e.Session, default))?.Ref, "stored model wins");
            e.Session.Model = "missing/model";
            Check.True(await e.Service.ResolveSessionModelAsync(e.Session, default) is null, "unavailable selection is not another model");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await e.Service.ResolveSessionModelAsync(e.Session, cancelled.Token);
                throw new AssertException("expected cancellation");
            }
            catch (OperationCanceledException) { }
        });

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

        r.Add("compaction: transcript serialization (roles, thinking, tool calls, truncation)", () =>
        {
            var a = T.Assistant("I will read it", T.Call("c1", "read", "{\"path\":\"a.cs\"}"));
            a.Parts.Insert(0, new ThinkingPart { Text = "the file first" });
            var s = TranscriptSerializer.Serialize(a);
            Check.Equal("[Assistant]\n[Thinking] the file first\nI will read it\n[Tool call] read({\"path\":\"a.cs\"})", s);
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
            // A normal conversation packs exactly as before: two entries make one chunk, three small ones make one.
            Check.Equal(1, TranscriptSerializer.Chunk(["aa", "bb", "cc"], 1000).Count);

            // An entry too long for a chunk is split, not truncated (idea-marq9s): everything reaches the summarizer.
            // Letters x/y/z, so counting them is not confused by the " (part i/n)" marker.
            var long1 = TranscriptSerializer.Chunk([new string('x', 600), new string('y', 3000), new string('z', 600)], 1000);
            Check.True(long1.All(c => c.Length <= 1000), string.Join("|", long1.Select(c => c.Length)));
            Check.Equal(3000, long1.Sum(c => c.Count(ch => ch == 'y')), "every y survives");
            Check.Equal(600, long1.Sum(c => c.Count(ch => ch == 'x')), "every x survives");
            Check.Equal(600, long1.Sum(c => c.Count(ch => ch == 'z')), "every z survives");
            Check.Equal(1, long1.Count(c => c.Contains('x')), "the x entry is intact in one chunk");
            Check.Equal(1, long1.Count(c => c.Contains('z')), "the z entry is intact in one chunk");

            // A message with a role tag keeps it on every piece, numbered, so the summarizer sees one message in parts.
            var message = TranscriptSerializer.Chunk([TranscriptSerializer.Serialize(T.User(string.Join('\n', Enumerable.Range(0, 400).Select(i => $"line {i}"))))], 1000);
            Check.True(message.Count >= 3, $"{message.Count} chunks for one huge message");
            Check.True(message.All(c => c.StartsWith("[User] (part ")), string.Join(" | ", message.Select(c => c[..Math.Min(24, c.Length)])));
            Check.Contains(string.Join("\n\n", message), "line 0\n");
            Check.Contains(string.Join("\n\n", message), $"line 399", "the last line is not dropped");
            Check.True(!string.Join("", message).Contains("chars omitted"), "nothing is truncated away");
            // Line boundaries are preferred, so no piece starts mid-word.
            Check.True(message.All(c => c.Split('\n').Skip(1).All(l => l.Length == 0 || l.StartsWith("line "))), "pieces break on line boundaries");
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
            Check.True(req.ReasoningEffort is null, "the chat has no effort set: the model's default");
            Check.Equal(10_000, req.MaxOutputTokens); // 80 % of the reserve, at most a quarter of the 40k window
            Check.Equal(0, req.Tools.Count);
            Check.Equal(session.Id, req.SessionId);
            var prompt = req.Messages.Single().Text;
            Check.Equal(MessageRole.User, req.Messages[0].Role);
            Check.Contains(prompt, "[User]\ngoal 0: uuu");
            Check.Contains(prompt, "[Tool call] read({\"path\":\"src/f0.cs\"})");
            Check.Contains(prompt, "[Tool result: read]\nRrrr");
            Check.Contains(prompt, "chars omitted");
            Check.Contains(prompt, "[Thinking] SECRET_THINKING 0");
            Check.True(prompt.StartsWith("<conversation>\n") && prompt.Contains("</conversation>"), "the conversation in tags");
            Check.Contains(prompt, "Create a structured context checkpoint summary");
            foreach (var h in new[] { "## Task", "## Constraints & Preferences", "## Progress", "### Done", "### In Progress", "### Blocked", "## Key Decisions", "## Next Steps", "## Critical Context" })
                Check.Contains(prompt, h);

            var store = env.Ctx.SessionsFake;
            var (sid, upTo) = store.MarkCompactedCalls.Single();
            Check.Equal(session.Id, sid);
            var summary = store.Appended.Last();
            Check.Equal(MessageRole.Summary, summary.Role);
            Check.True(summary.Text.StartsWith("## Task\nSUMMARY\n\n<read-files>\nsrc/f0.cs\n"), summary.Text);
            Check.Equal("src/f0.cs", summary.Meta!["readFiles"]![0]!.GetValue<string>());
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
            // tagged, so the UI keeps the banner while the summary is written and until the model answers again
            Check.Equal("compaction/start/auto,compaction/done/auto", string.Join(",", notices.Select(n => (JsonObject)n.Data!)
                .Select(d => $"{d["kind"].Str()}/{d["phase"].Str()}/{d["mode"].Str()}")));
            Check.Contains(((JsonObject)notices.First().Data!)["text"].Str(), "Compacting context (~");
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
            Check.True(CompactionService.ShouldCompact(114_689, 131_072, o), "fewer than the reserve left");
            Check.False(CompactionService.ShouldCompact(114_000, 131_072, o));
            Check.True(CompactionService.ShouldCompact(104_858, 131_072, new CompactionOptions { ThresholdPercent = 0.8 }), "a threshold when set");
            Check.Equal(13_107, CompactionOptions.From(new FakeSettings()).MaxSummaryTokens);
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

        r.Add("compaction: file lists come from the tool calls and are carried from summary to summary", () =>
        {
            var prev = new JsonObject { ["readFiles"] = new JsonArray("old.cs", "x.cs"), ["modifiedFiles"] = new JsonArray("done.cs") };
            var msgs = new List<ChatMessage>
            {
                T.Assistant("", T.Call("c1", "read", "{\"path\":\"a.cs\"}"), T.Call("c2", "grep", "{\"pattern\":\"x\"}")),
                T.Assistant("", T.Call("c3", "edit", "{\"file_path\":\"x.cs\",\"oldText\":\"a\",\"newText\":\"b\"}")),
                T.Assistant("", T.Call("c4", "ssh_read", "{\"host\":\"nuc\",\"path\":\"/etc/hosts\"}"), T.Call("c5", "write", "not json")),
                // ssh is one tool with actions now (ssh_read above is the older name, still in older chats)
                T.Assistant("", T.Call("c6", "ssh", "{\"action\":\"read\",\"host\":\"nuc\",\"path\":\"/etc/fstab\"}"),
                    T.Call("c7", "ssh", "{\"action\":\"edit\",\"host\":\"srv\",\"path\":\"/app/.env\"}")),
            };
            var (read, modified) = FileLists.Collect(msgs, prev);
            Check.Equal("a.cs,nuc:/etc/fstab,nuc:/etc/hosts,old.cs", string.Join(",", read), "read, minus what was modified");
            Check.Equal("done.cs,srv:/app/.env,x.cs", string.Join(",", modified));
            var text = "## Task\nG" + FileLists.Format(read, modified);
            Check.Contains(text, "<read-files>\na.cs\nnuc:/etc/fstab\nnuc:/etc/hosts\nold.cs\n</read-files>");
            Check.Contains(text, "<modified-files>\ndone.cs\nsrv:/app/.env\nx.cs\n</modified-files>");
            Check.Equal("## Task\nG", FileLists.Strip(text));
        });

        r.Add("compaction: a turn too long to keep gets its start summarized apart (split turn)", async () =>
        {
            var env = new Env();
            env.Add(T.User("earlier question"));
            env.Add(T.Assistant("earlier answer " + new string('e', 4000)));
            // one long turn: a single user message, then many tool rounds
            env.Add(T.User("LONG TASK: migrate the parser"));
            for (var i = 0; i < 12; i++)
            {
                env.Add(T.Assistant($"step {i}", T.Call($"s{i}", "read", $"{{\"path\":\"p{i}.cs\"}}")));
                env.Add(T.ToolResult(($"s{i}", "read", new string('r', 7000))));
            }
            env.Ctx.ModelsFake.Responder = req => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop",
                Parts = [new TextPart { Text = req.Messages[0].Text.Contains("PREFIX of a turn") ? "PREFIX-SUMMARY" : "HISTORY-SUMMARY" }],
            };
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);
            var reqs = env.Ctx.ModelsFake.Requests.ToList();
            Check.Equal(2, reqs.Count, "history and turn prefix");
            Check.Contains(reqs[0].Messages[0].Text, "earlier question");
            Check.NotContains(reqs[0].Messages[0].Text, "LONG TASK");
            Check.Contains(reqs[1].Messages[0].Text, "LONG TASK: migrate the parser");
            Check.Contains(reqs[1].Messages[0].Text, "## Original Request");
            Check.True(reqs[1].MaxOutputTokens < reqs[0].MaxOutputTokens, "a smaller budget for the prefix");
            Check.True(result.Summary!.Text.StartsWith("HISTORY-SUMMARY\n\n---\n\n**Turn Context (split turn):**\n\nPREFIX-SUMMARY"), result.Summary.Text);
            var firstKept = env.Ctx.Sessions.GetContextMessages(env.Session.Id)[1];
            Check.Equal(MessageRole.Assistant, firstKept.Role);
        });

        // Real-model regression: a chat that was one long turn had only the project and instruction notices before it; the
        // summary of those said "No explicit task yet … awaiting the user's actual task", above the turn's real summary.
        r.Add("compaction: notices before a split turn are not summarized (their plugins announce them again)", async () =>
        {
            var env = new Env();
            env.Add(ChatMessage.NoticeText("Working directory: C:/p (project \"p\").", "project"));
            env.Add(ChatMessage.NoticeText("Instruction files that apply here: " + new string('i', 3000), "instructions"));
            env.Add(T.User("LONG TASK: migrate the parser"));
            for (var i = 0; i < 12; i++)
            {
                env.Add(T.Assistant($"step {i}", T.Call($"s{i}", "read", $"{{\"path\":\"p{i}.cs\"}}")));
                env.Add(T.ToolResult(($"s{i}", "read", new string('r', 7000))));
            }
            env.Ctx.ModelsFake.Responder = req => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "PREFIX-SUMMARY" }],
            };
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);
            var reqs = env.Ctx.ModelsFake.Requests.ToList();
            Check.Equal(1, reqs.Count, "only the turn prefix is summarized");
            Check.Contains(reqs[0].Messages[0].Text, "PREFIX of a turn");
            Check.True(result.Summary!.Text.StartsWith("No earlier history.\n\n---\n\n**Turn Context (split turn):**\n\nPREFIX-SUMMARY"), result.Summary.Text);
        });

        r.Add("compaction: nothing but notices to summarize is nothing to compact", async () =>
        {
            var env = new Env();
            env.Add(ChatMessage.NoticeText("Instruction files that apply here: " + new string('i', 40_000), "instructions"));
            env.Add(T.User("hi"));
            env.Add(T.Assistant("hello"));
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.False(result.Compacted, result.Message);
            Check.Contains(result.Message, "Nothing to compact");
            Check.Equal(0, env.Ctx.ModelsFake.Requests.Count(), "no summarizer call");
        });

        r.Add("compaction: a summary cut off at the output limit is refused and nothing changes", async () =>
        {
            var env = new Env();
            env.Conversation(13);
            env.Ctx.ModelsFake.Responder = req => new ChatMessage { Role = MessageRole.Assistant, StopReason = "length", Parts = [new TextPart { Text = "## Task\nhalf a" }] };
            var before = env.Ctx.Sessions.GetContextMessages(env.Session.Id).Count;
            try
            {
                await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
                throw new AssertException("expected a failure");
            }
            catch (InvalidOperationException ex) { Check.Contains(ex.Message, "output limit"); }
            Check.Equal(before, env.Ctx.Sessions.GetContextMessages(env.Session.Id).Count, "context unchanged");
            Check.False(env.Ctx.SessionsFake.Appended.Any(m => m.Role == MessageRole.Summary), "no summary written");
        });

        r.Add("compaction: chunked (rolling) summaries when the transcript exceeds the summarizer window", async () =>
        {
            var env = new Env();
            var small = T.Model("small", 12_000, "test");
            env.Ctx.ModelsFake.Models.Add(small);
            env.Ctx.SettingsFake.Set("compaction.model", "test/small");
            var scheduler = new FakeAgentScheduler();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
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
            Check.Contains(reqs[0].Messages[0].Text, "Create a structured context checkpoint summary");
            for (var i = 1; i < reqs.Count; i++)
            {
                var p = reqs[i].Messages[0].Text;
                Check.Contains(p, $"<previous-summary>\nROLLING-{i}\n</previous-summary>");
                Check.Contains(p, "PRESERVE all existing information from the previous summary");
                Check.True(ModelMessages.EstimateTokens(p) < 12_000 - 3000, $"chunk {i} fits: {ModelMessages.EstimateTokens(p)}");
            }
            Check.True(env.Ctx.SessionsFake.Appended.Last().Text.StartsWith($"ROLLING-{reqs.Count}\n\n<read-files>"));
            // A different model than the agent's → its own slot, released afterwards.
            Check.Equal("test/small", scheduler.Acquired.Single().Key);
            Check.Equal("agt_1", scheduler.Acquired.Single().AgentId);
            Check.Equal(1, scheduler.Released);
        });

        r.Add("compaction: summarizer failure never breaks the run", async () =>
        {
            var env = new Env();
            env.Ctx.ModelsFake.Responder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new ThinkingPart { Text = "..." }], StopReason = "length" };
            env.Conversation(13);
            await env.Hook.OnBeforeModelCallAsync(env.Turn());
            Check.Equal(0, env.Ctx.SessionsFake.MarkCompactedCalls.Count);
            var failed = (JsonObject)env.Ctx.Bus.OfType(EventTypes.AgentNotice).Last().Data!;
            Check.Contains(failed["text"].Str(), "Auto-compaction failed");
            Check.Equal("compaction/failed/auto", $"{failed["kind"].Str()}/{failed["phase"].Str()}/{failed["mode"].Str()}", "the failure replaces the kept banner");
        });

        r.Add("compaction: compaction.run RPC, /compact command, busy agent, slots", async () =>
        {
            var env = new Env();
            var ctx = env.Ctx;
            await new CompactionPlugin().StartAsync(ctx, CancellationToken.None);
            Check.True(ctx.Services.GetAll<IAgentHook>().Single() is CompactionHook { Order: -100 });
            var cmd = ctx.UiFake.CommandList.Single();
            Check.Equal("compact", cmd.Name);
            Check.Equal("compaction.run", cmd.Rpc);
            Check.Equal("Summarize older messages to free context", cmd.Description);

            var scheduler = new FakeAgentScheduler();
            ctx.ServicesFake.Register<IAgentScheduler>(scheduler);
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
            Check.Contains(req.Messages[0].Text, "Additional focus: focus on the parser");
            Check.Equal("agt_9", req.AgentId);
            Check.Equal("manual", ctx.SessionsFake.Appended.Last().MetaString("mode"));
            Check.Equal("test/m1", scheduler.Acquired.Single().Key);
            Check.Equal("compaction", scheduler.Acquired.Single().Label);
            Check.Equal(1, scheduler.Released);

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
            Check.Contains(prompt, "<previous-summary>\n## Task\nSUMMARY\n</previous-summary>");
            Check.NotContains(prompt, "[Summary of the earlier conversation]");
            Check.NotContains(prompt, "<read-files>"); // the lists come from the meta, not through the model
            Check.Contains(prompt, "NEW conversation messages to incorporate");
            var files = result.Summary!.Meta!["readFiles"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Check.True(files.Contains("src/fA0.cs") || files.Contains("src/f0.cs"), string.Join(",", files));
            Check.True(s1.Compacted, "previous summary marked compacted");
            var ctxMsgs = store.GetContextMessages(env.Session.Id);
            Check.Equal(1, ctxMsgs.Count(m => m.Role == MessageRole.Summary));
            Check.Equal(result.Summary!.Id, ctxMsgs[0].Id);
        });

        // Audit case (idea-ckqma4): the previous summary (48k chars ≈ 12k tokens) was written by a larger summarizer;
        // rolling it in with an 8k summarizer built a request of ≈58k chars the summarizer cannot even read.
        r.Add("compaction: a previous summary too large for the summarizer is condensed before the roll", async () =>
        {
            var env = new Env();
            var small = T.Model("small", 8_192, "test");
            env.Ctx.ModelsFake.Models.Add(small);
            var scheduler = new FakeAgentScheduler();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(scheduler);

            // First compaction (by the big model) leaves a 48k-char summary in the context; the chat continues.
            var big = "## Task\nOLD " + new string('S', 47_994);
            env.Ctx.ModelsFake.Responder = req => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop",
                Parts = [new TextPart { Text = req.Model.Id == "small" ? "## Task\nCONDENSED" : big }],
            };
            env.Conversation(13, "A");
            await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            env.Conversation(2, "B");

            // Second compaction now runs on the 8k summarizer: every request it receives must fit its window.
            env.Ctx.SettingsFake.Set("compaction.model", "test/small");
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);

            var reqs = env.Ctx.ModelsFake.Requests.Skip(1).ToList();
            Check.True(reqs.All(r => r.Model.Id == "small"), "all by the small summarizer");
            foreach (var r in reqs)
                Check.True(ModelMessages.EstimateTokens(r.Messages[0].Text) < 8_192, $"fits the summarizer window: {ModelMessages.EstimateTokens(r.Messages[0].Text)} tokens");
            Check.True(reqs.Count >= 3, $"{reqs.Count} calls: the oversized summary is condensed in its own calls");
            Check.NotContains(reqs[0].Messages[0].Text, "<previous-summary>", "the first condense call has no summary yet");
            Check.Contains(reqs[^1].Messages[0].Text, "<previous-summary>\n## Task\nCONDENSED\n</previous-summary>", "the roll starts from the condensed summary");
            Check.True(reqs.Any(r => r.Messages[0].Text.Contains("Reading file 8")), "and the new messages are rolled in");
            Check.True(result.Summary!.Text.StartsWith("## Task\nCONDENSED"), result.Summary.Text);
            Check.Equal(reqs.Count, result.SummarizerCalls, "the condense calls count too");
            Check.Equal("test/small", scheduler.Acquired.Last().Key);
        });

        r.Add("compaction: the fake model can be strict about the output budget", async () =>
        {
            // idea-12wuj4: without the flag a test can script a 48k-char answer for a 100-token budget and never notice
            // that a real provider would have cut it off with stop reason "length".
            var env = new Env();
            var request = new ModelRequest
            {
                Model = env.Model, Messages = [ChatMessage.UserText("hi")], MaxOutputTokens = 100, Purpose = "test",
            };
            await env.Ctx.ModelsFake.CompleteAsync(request, CancellationToken.None);

            env.Ctx.ModelsFake.StrictOutput = true;
            env.Ctx.ModelsFake.Responder = _ => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = new string('x', 4000) }],
            };
            var ex = await Check.ThrowsAsync<AssertException>(() => env.Ctx.ModelsFake.CompleteAsync(request, CancellationToken.None));
            Check.Contains(ex.Message, "allows 100");
            Check.Contains(ex.Message, "length");

            // A response inside the budget is fine, and the flag is opt-in: off, the same answer passes as before.
            env.Ctx.ModelsFake.Responder = _ => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = "## Task\nOK" }],
            };
            await env.Ctx.ModelsFake.CompleteAsync(request, CancellationToken.None);
            env.Ctx.ModelsFake.StrictOutput = false;
            env.Ctx.ModelsFake.Responder = _ => new ChatMessage
            {
                Role = MessageRole.Assistant, StopReason = "stop", Parts = [new TextPart { Text = new string('x', 4000) }],
            };
            await env.Ctx.ModelsFake.CompleteAsync(request, CancellationToken.None);
        });

        // The condense boundary (idea-8t4amp): a prior summary that still leaves room for a minimal chunk plus the
        // fixed prompt and the answer goes in as-is; one token more and it is condensed first.
        r.Add("compaction: a prior summary is condensed exactly when it stops fitting the summarizer", async () =>
        {
            // The 8k summarizer: window 8192, output allowance 2048 (a quarter of the window).
            var threshold = 8_192 - 2_048 - CompactionService.FixedPromptTokens - CompactionService.MinChunkTokens;
            static Env EnvWithSmall(string id)
            {
                var env = new Env();
                var small = T.Model(id, 8_192, "test");
                env.Ctx.ModelsFake.Models.Add(small);
                env.Ctx.SettingsFake.Set("compaction.model", $"test/{id}");
                env.Ctx.ServicesFake.Register<IAgentScheduler>(new FakeAgentScheduler());
                return env;
            }

            // Exactly at the threshold: the whole prior summary goes into the request as-is (no condense),
            // and every request still fits the summarizer window.
            var env = EnvWithSmall("small");
            var prior = new string('P', threshold * 4);
            env.Add(new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = prior }] });
            env.Conversation(4);
            var result = await env.Service.CompactAsync(new CompactionRequest { SessionId = env.Session.Id, Model = env.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result.Compacted, result.Message);
            var reqs = env.Ctx.ModelsFake.Requests.ToList();
            Check.Contains(reqs[0].Messages[0].Text, prior, "the whole prior summary in the first request");
            foreach (var r in reqs)
                Check.True(ModelMessages.EstimateTokens(r.Messages[0].Text) < 8_192, $"{ModelMessages.EstimateTokens(r.Messages[0].Text)} tokens");

            // One token more: it is condensed first, in its own calls, and no request carries the whole prior.
            var more = EnvWithSmall("small2");
            var big = new string('P', (threshold + 1) * 4);
            more.Add(new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = big }] });
            more.Conversation(4);
            var result2 = await more.Service.CompactAsync(new CompactionRequest { SessionId = more.Session.Id, Model = more.Model, Mode = CompactionMode.Manual }, CancellationToken.None);
            Check.True(result2.Compacted, result2.Message);
            var reqs2 = more.Ctx.ModelsFake.Requests.ToList();
            Check.True(reqs2.Count >= 3, $"{reqs2.Count} calls: condense first, then the roll");
            Check.True(reqs2.All(r => !r.Messages[0].Text.Contains(big)), "the prior is condensed, never sent whole");
            Check.Contains(reqs2[^1].Messages[0].Text, "<previous-summary>", "the roll starts from the condensed summary");
            foreach (var r in reqs2)
                Check.True(ModelMessages.EstimateTokens(r.Messages[0].Text) < 8_192, $"{ModelMessages.EstimateTokens(r.Messages[0].Text)} tokens");
        });

        r.Add("compaction: the summary at the chat's reasoning effort; token formatting", () =>
        {
            Check.Equal("high", CompactionService.EffortFor(T.Model(efforts: ["low", "high"]), "high"));
            Check.Equal("low", CompactionService.EffortFor(T.Model(efforts: ["low", "high"]), "LOW"));
            Check.True(CompactionService.EffortFor(T.Model(efforts: ["low", "high"]), "xhigh") is null, "not offered: the model's default");
            Check.True(CompactionService.EffortFor(T.Model(efforts: ["low"]), null) is null);
            Check.True(CompactionService.EffortFor(T.Model(), "high") is null);
            Check.Equal("950", CompactionService.Fmt(950));
            Check.Equal("12.3k", CompactionService.Fmt(12_345));
            Check.Equal("131k", CompactionService.Fmt(131_072));
        });
    }
}
