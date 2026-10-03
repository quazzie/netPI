using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class LoopTests
{
    public static void Register(TestRunner t)
    {
        t.Add("loop: simple text reply", SimpleReply);
        t.Add("loop: the time to the first token is kept in the message meta", FirstToken);
        t.Add("loop: stream deltas are coalesced", Coalescing);
        t.Add("loop: tool loop with a fake tool", ToolLoop);
        t.Add("loop: parallel read-only batch", ParallelReadOnly);
        t.Add("loop: a tool with actions runs its read-only calls in parallel (IReadOnlyCalls), the others one by one", ParallelReadOnlyCalls);
        t.Add("loop: mixed batch runs sequentially", SequentialBatch);
        t.Add("loop: steering mid-batch skips remaining tools", SteeringMidBatch);
        t.Add("loop: queued follow-ups one at a time", FollowUps);
        t.Add("loop: abort persists partial message", AbortPartial);
        t.Add("loop: abort during a tool call", AbortDuringTool);
        t.Add("loop: abort during a tool that swallows cancellation stops the batch", AbortDuringSwallowingTool);
        t.Add("loop: a run cancelled in an after-tool hook keeps one result per call id", OneResultWhenCancelledInAfterHook);
        t.Add("loop: an abort keeps the queue: dequeue it, then send it as the next turn", AbortKeepsQueue);
        t.Add("loop: a run that fails before it takes the queue does not start itself again for it", FailedRunKeepsQueue);
        t.Add("loop: taking a steer back from the queue lifts its cancel, so a wait is not interrupted by nothing", StaleSteerSignal);
        t.Add("loop: unknown tool and invalid JSON arguments", ToolErrors);
        t.Add("loop: {\"help\": true} on any tool returns its manual and does not run it", ToolHelpCall);
        t.Add("loop: tool exceptions and result truncation", ToolExceptionAndTruncation);
        t.Add("loop: the result limiter itself: head, tail, the note, and never half a character", ResultLimiterKeepsEnds);
        t.Add("loop: without a prompt builder the run freezes the prompt it built for itself", FallbackPromptIsFrozen);
        t.Add("loop: model error writes an error notice", ModelError);
        t.Add("loop: missing model writes an error notice", MissingModel);
        t.Add("loop: default model resolved while the catalog is still loading (startup)", DefaultModelWhileCatalogLoads);
        t.Add("loop: max turns", MaxTurns);
        t.Add("loop: stream reset and notices", StreamResetAndNotice);
        t.Add("loop: model switched mid-run is used next turn", ModelSwitchMidRun);
        t.Add("hooks: inject", HookInject);
        t.Add("hooks: replace", HookReplace);
        t.Add("hooks: stop", HookStop);
        t.Add("hooks: before model call, retry on error, block tool, run start/end", HookMisc);
        t.Add("middleware: model middleware wraps agent calls", Middleware);
        t.Add("rpc: agent.send / queue / dequeue / get / list / abort", Rpcs);
        t.Add("runtime: stopping the agent plugin cancels runs", PluginStop);
    }

    private static FakeTool Echo() => new("echo", (ctx, args, ct) =>
        Task.FromResult(ToolResult.Ok("echo:" + (args.TryGetProperty("text", out var v) ? v.GetString() : ""), new { len = 3 })));

    private static async Task FirstToken()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Late(ct);
        await h.SendAsync(s.Id, "hi");
        await h.IdleAsync(s.Id);

        var m = h.Messages(s.Id).Last(x => x.Role == MessageRole.Assistant);
        var ttft = m.Meta?["ttftMs"]?.GetValue<long>();
        Check.True(ttft is >= 60, $"from the call's start to the first delta: {ttft} ms");
        Check.True(m.DurationMs - ttft >= 30, $"the rest of the call is not in it: {m.DurationMs} ms in all");

        // prefill 80ms, then the answer takes another 40ms
        static async IAsyncEnumerable<ModelStreamEvent> Late([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(80, ct);
            await foreach (var e in Reply.Stream(Reply.Message(new TextPart { Text = "late" }), async c => await Task.Delay(40, c), ct)) yield return e;
        }
    }

    private static async Task SimpleReply()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Stream(Reply.Message(new ThinkingPart { Text = "hmm, let me think" }, new TextPart { Text = "Hello there!" }), async c => await Task.Delay(20, c));
        await h.SendAsync(s.Id, "hi");
        var a = await h.IdleAsync(s.Id);

        var msgs = h.Messages(s.Id);
        Check.Equal(3, msgs.Count, "messages");
        Check.Equal(MessageRole.User, msgs[0].Role);
        Check.Equal("hi", msgs[0].Text);
        Check.Equal("project", msgs[1].MetaString("kind"), "the working directory arrives as a notice before the first model call");
        var m = msgs[2];
        Check.Equal(MessageRole.Assistant, m.Role);
        Check.Equal("Hello there!", m.Text);
        Check.Equal("fake", m.Provider);
        Check.Equal("local", m.Model);
        Check.Equal("stop", m.StopReason);
        Check.True(m.DurationMs is not null, "duration set");
        Check.True(m.Parts.OfType<ThinkingPart>().Single().DurationMs is not null, "thinking duration set");
        Check.Equal(a.Id, m.MetaString("agentId"));

        Check.Equal(AgentStatus.Idle, a.Status);
        Check.False(a.IsSubagent);
        Check.Equal(1, a.Runs);
        Check.Equal(1, a.Turns);
        Check.Equal("Hello there!", a.Result);
        Check.Equal(100L, a.InputTokens);
        Check.Equal(10L, a.OutputTokens);
        Check.Equal("fake/local", a.Model);
        Check.Equal("fake/local", a.Agent);
        Check.Equal(110L, h.Sessions.GetSession(s.Id)!.ContextTokens);

        var req = h.Catalog.Requests.Single();
        Check.Contains(req.SystemPrompt, "NetPI");
        Check.True(req.Messages.Any(m => m.Text.Contains("Working directory: " + h.Workspace)), "working directory notice sent");
        Check.NotContains(req.SystemPrompt, h.Workspace, "not in the system prompt");
        Check.Equal(4096, req.MaxOutputTokens);
        Check.Equal(s.Id, req.SessionId);
        Check.Equal(a.Id, req.AgentId);
        Check.True(req.Tools.Any(x => x.Name == "agent_spawn"), "agent tools offered");

        var start = h.Bus.OfType(EventTypes.StreamStart).Single();
        Check.Equal(s.Id, start.SessionId);
        Check.Equal("fake/local", FakeBus.Data(start)["model"]!.GetValue<string>());
        var text = string.Concat(h.Bus.OfType(EventTypes.StreamDelta).Select(FakeBus.Data).Where(d => (string?)d["kind"] == "text").Select(d => (string?)d["text"]));
        Check.Equal("Hello there!", text);
        var thinking = string.Concat(h.Bus.OfType(EventTypes.StreamDelta).Select(FakeBus.Data).Where(d => (string?)d["kind"] == "thinking").Select(d => (string?)d["text"]));
        Check.Equal("hmm, let me think", thinking);
        Check.Equal(1, h.Bus.OfType(EventTypes.StreamEnd).Count);
        // stream.end precedes the assistant message.added
        var all = h.Bus.All;
        var endIdx = all.FindIndex(e => e.Type == EventTypes.StreamEnd);
        var addIdx = all.FindLastIndex(e => e.Type == EventTypes.MessageAdded);
        Check.True(endIdx < addIdx, "stream.end before message.added");

        var usage = FakeBus.Data(h.Bus.OfType(EventTypes.UsageRecorded).Single());
        Check.Equal("fake", (string?)usage["provider"]);
        Check.Equal("local", (string?)usage["model"]);
        Check.Equal(s.Id, (string?)usage["sessionId"]);
        Check.Equal(a.Id, (string?)usage["agentId"]);
        Check.Equal(100L, usage["usage"]!["inputTokens"]!.GetValue<long>());
        var ctxEvt = h.Bus.OfType(EventTypes.SessionContext).Single();
        Check.Equal(null, ctxEvt.SessionId); // broadcast
        Check.Equal(110L, FakeBus.Data(ctxEvt)["used"]!.GetValue<long>());
        Check.Equal("100000", FakeBus.Data(ctxEvt)["window"]!.ToJsonString());

        var statuses = h.Bus.OfType(EventTypes.AgentStatus).Select(e => FakeBus.Data(e)["agent"]!["status"]!.GetValue<string>()).ToList();
        Check.True(statuses.Contains("running"), "running published");
        Check.Equal("idle", statuses[^1]);
    }

    private static async Task Coalescing()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => $"w{i} "));
        h.Catalog.Handler = (r, ct) => Reply.Text(text); // 4-char chunks, no delays
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var deltas = h.Bus.OfType(EventTypes.StreamDelta);
        var chunks = (text.Length + 3) / 4;
        Check.True(deltas.Count < chunks / 4, $"coalesced: {deltas.Count} events for {chunks} chunks");
        Check.Equal(text, string.Concat(deltas.Select(d => (string?)FakeBus.Data(d)["text"])));
        Check.True(deltas.All(d => d.SessionId == s.Id), "deltas are session scoped");
    }

    private static async Task ToolLoop()
    {
        await using var h = await TestHost.StartAsync();
        var echo = Echo();
        h.AddTool(echo);
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("done: " + r.Messages[^1].ToolResults.Single().Content)
            : Reply.Tool("echo", new { text = "abc" });
        await h.SendAsync(s.Id, "use echo");
        var a = await h.IdleAsync(s.Id);

        var msgs = h.Messages(s.Id);
        Check.Equal("User,Notice,Assistant,Tool,Assistant", string.Join(",", msgs.Select(m => m.Role)));
        Check.Equal("tool_use", msgs[2].StopReason);
        var call = msgs[2].ToolCalls.Single();
        var result = msgs[3].ToolResults.Single();
        Check.Equal(call.Id, result.CallId);
        Check.Equal("echo", result.Name);
        Check.Equal("echo:abc", result.Content);
        Check.False(result.IsError);
        Check.Equal(3, result.Details!["len"]!.GetValue<int>());
        Check.True(result.DurationMs is not null);
        Check.Equal("done: echo:abc", msgs[4].Text);
        Check.Equal(1, echo.Calls);
        Check.Equal(2, a.Turns);
        Check.Equal(1, a.ToolCalls);

        var ts = FakeBus.Data(h.Bus.OfType(EventTypes.ToolStart).Single());
        Check.Equal(call.Id, (string?)ts["callId"]);
        Check.Equal("ECHO", (string?)ts["label"]);
        Check.Equal(a.Id, (string?)ts["agentId"]);
        Check.Contains((string?)ts["arguments"], "abc");
        var te = FakeBus.Data(h.Bus.OfType(EventTypes.ToolEnd).Single());
        Check.Equal(false, te["isError"]!.GetValue<bool>());
        Check.Equal(1, h.Bus.OfType(EventTypes.StreamTool).Count);
    }

    private static async Task ParallelReadOnly()
    {
        await using var h = await TestHost.StartAsync();
        int current = 0, max = 0;
        var peek = new FakeTool("peek", async (ctx, args, ct) =>
        {
            var c = Interlocked.Increment(ref current);
            lock (h) max = Math.Max(max, c);
            await Task.Delay(250, ct);
            Interlocked.Decrement(ref current);
            return ToolResult.Ok("peeked " + args.GetProperty("n").GetInt32());
        }, readOnly: true);
        h.AddTool(peek);
        var s = h.NewSession();
        var sw = new Stopwatch();
        h.Catalog.Handler = (r, ct) =>
        {
            if (Reply.HasToolResult(r)) return Reply.Text("all done");
            sw.Start();
            return Reply.Tools(Reply.Call("peek", new { n = 1 }), Reply.Call("peek", new { n = 2 }), Reply.Call("peek", new { n = 3 }));
        };
        await h.SendAsync(s.Id, "peek thrice");
        await h.IdleAsync(s.Id);
        Check.Equal(3, peek.Calls);
        Check.True(max >= 2, $"ran concurrently (max {max})");
        Check.True(sw.ElapsedMilliseconds < 700, $"parallel batch took {sw.ElapsedMilliseconds}ms");
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Content).OrderBy(x => x).ToList();
        Check.Equal("peeked 1|peeked 2|peeked 3", string.Join("|", results));
        // the model got all three results (normalized into one tool message)
        var last = h.Catalog.Requests.Last();
        Check.Equal(3, last.Messages[^1].ToolResults.Count());
    }

    /// <summary>One tool, two actions: peek only reads, poke changes things.</summary>
    private sealed class PeekPoke(Func<Task> body) : IAgentTool, IReadOnlyCalls
    {
        public ToolDefinition Definition { get; } = new() { Name = "pp", Description = "Peek or poke." };
        public bool IsReadOnly(JsonElement args) => args.TryGetProperty("action", out var a) && a.GetString() == "peek";
        public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) { await body(); return ToolResult.Ok("ok"); }
    }

    private static async Task ParallelReadOnlyCalls()
    {
        foreach (var (second, parallel) in new[] { ("peek", true), ("poke", false) })
        {
            await using var h = await TestHost.StartAsync();
            int current = 0, max = 0;
            h.AddTool(new PeekPoke(async () =>
            {
                var c = Interlocked.Increment(ref current);
                lock (h) max = Math.Max(max, c);
                await Task.Delay(150);
                Interlocked.Decrement(ref current);
            }));
            var s = h.NewSession();
            h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
                ? Reply.Text("done")
                : Reply.Tools(Reply.Call("pp", new { action = "peek" }), Reply.Call("pp", new { action = second }));
            await h.SendAsync(s.Id, "go");
            await h.IdleAsync(s.Id);
            Check.Equal(parallel ? 2 : 1, max, $"peek + {second}");
        }
    }

    private static async Task SequentialBatch()
    {
        await using var h = await TestHost.StartAsync();
        int current = 0, max = 0;
        Func<ToolContext, JsonElement, CancellationToken, Task<ToolResult>> body = async (ctx, args, ct) =>
        {
            var c = Interlocked.Increment(ref current);
            lock (h) max = Math.Max(max, c);
            await Task.Delay(60, ct);
            Interlocked.Decrement(ref current);
            return ToolResult.Ok("ok");
        };
        h.AddTool(new FakeTool("peek", body, readOnly: true));
        h.AddTool(new FakeTool("poke", body, readOnly: false));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("done")
            : Reply.Tools(Reply.Call("peek"), Reply.Call("poke"), Reply.Call("peek"));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(1, max, "sequential");
        var order = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single().CallId).ToList();
        var calls = h.Messages(s.Id)[2].ToolCalls.Select(c => c.Id).ToList();
        Check.Equal(string.Join(",", calls), string.Join(",", order), "results in call order");
    }

    private static async Task SteeringMidBatch()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var work = new FakeTool("work", async (ctx, args, ct) =>
        {
            if (args.GetProperty("n").GetInt32() == 1)
            {
                started.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
            return ToolResult.Ok("worked");
        });
        h.AddTool(work);
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) || Reply.LastUser(r) == "change of plans"
            ? Reply.Text("ack")
            : Reply.Tools(Reply.Call("work", new { n = 1 }), Reply.Call("work", new { n = 2 }), Reply.Call("work", new { n = 3 }));

        await h.SendAsync(s.Id, "start");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var info = await h.SendAsync(s.Id, "change of plans");
        Check.Equal(1, info.QueuedMessages);
        var q = h.Runtime.GetQueue(s.Id);
        Check.Equal(1, q.Count);
        Check.Equal("steer", q[0].Mode);
        Check.Equal("change of plans", q[0].Text);
        await h.Bus.DrainAsync();
        var qe = FakeBus.Data(h.Bus.OfType(EventTypes.AgentQueue).Last());
        Check.Equal("steer", (string?)qe["items"]![0]!["mode"]);

        gate.SetResult();
        var a = await h.IdleAsync(s.Id);
        Check.Equal(1, work.Calls, "only the running call executed");
        var msgs = h.Messages(s.Id);
        Check.Equal("User,Notice,Assistant,Tool,Tool,Tool,User,Assistant", string.Join(",", msgs.Select(m => m.Role)));
        Check.Equal("worked", msgs[3].ToolResults.Single().Content);
        Check.Contains(msgs[4].ToolResults.Single().Content, "Skipped");
        Check.True(msgs[4].ToolResults.Single().IsError);
        Check.Contains(msgs[5].ToolResults.Single().Content, "Skipped");
        Check.Equal("change of plans", msgs[6].Text);
        Check.Equal("steer", msgs[6].MetaString("delivery"));
        Check.Equal("ack", msgs[7].Text);
        Check.Equal("change of plans", Reply.LastUser(h.Catalog.Requests.Last()));
        Check.Equal(0, h.Runtime.GetQueue(s.Id).Count);
        Check.Equal(1, a.Runs);
    }

    private static async Task FollowUps()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Text("first", c => gate.Task.WaitAsync(c)),
            2 => Reply.Text("second"),
            _ => Reply.Text("third"),
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first call");
        await h.SendAsync(s.Id, "A", DeliveryMode.Queue);
        var info = await h.SendAsync(s.Id, "B", DeliveryMode.Queue);
        Check.Equal(2, info.QueuedMessages);
        Check.Equal("queue,queue", string.Join(",", h.Runtime.GetQueue(s.Id).Select(x => x.Mode)));
        gate.SetResult();
        var a = await h.IdleAsync(s.Id);

        var reqs = h.Catalog.Requests.ToList();
        Check.Equal(3, reqs.Count);
        Check.Equal("A", Reply.LastUser(reqs[1]));
        Check.False(reqs[1].Messages.Any(m => m.Text == "B"), "B not yet delivered");
        Check.Equal("B", Reply.LastUser(reqs[2]));
        Check.Equal("go,first,A,second,B,third", string.Join(",", h.Messages(s.Id).Where(m => m.MetaString("kind") != "project").Select(m => m.Text)));
        Check.Equal("queue", h.Messages(s.Id).Single(m => m.Text == "A").MetaString("delivery"));
        Check.Equal(1, a.Runs);
        Check.Equal(0, a.QueuedMessages);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Hang([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new ThinkingDelta("thinking...");
        yield return new TextDelta("partial ");
        yield return new TextDelta("answer");
        await Task.Delay(Timeout.Infinite, ct);
        yield return new StreamCompleted(Reply.Message(new TextPart { Text = "never" }));
    }

    private static async Task AbortPartial()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Hang(ct);
        var s = h.NewSession();
        Check.False(await h.Runtime.AbortAsync(s.Id), "nothing to abort yet");
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Bus.OfType(EventTypes.StreamDelta).Any(e => ((string?)FakeBus.Data(e)["text"])?.Contains("answer") == true), "partial streamed");
        Check.True(await h.Runtime.AbortAsync(s.Id), "abort");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(AgentStatus.Idle, a.Status);
        var last = h.Messages(s.Id)[^1];
        Check.Equal(MessageRole.Assistant, last.Role);
        Check.Equal("aborted", last.StopReason);
        Check.Equal("partial answer", last.Text);
        Check.Equal("thinking...", last.Parts.OfType<ThinkingPart>().Single().Text);
        Check.Equal(1, h.Bus.OfType(EventTypes.StreamEnd).Count);
        Check.False(await h.Runtime.AbortAsync(s.Id), "already idle");
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(p => p.Busy), "slot released");
    }

    private static async Task AbortDuringTool()
    {
        await using var h = await TestHost.StartAsync();
        var started = new TaskCompletionSource();
        var wait = new FakeTool("wait", async (ctx, args, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Ok("never");
        });
        h.AddTool(wait);
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Tools(Reply.Call("wait"), Reply.Call("wait"));
        await h.SendAsync(s.Id, "go");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Runtime.AbortAsync(s.Id);
        await h.IdleAsync(s.Id);
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).ToList();
        Check.Equal(2, results.Count);
        Check.True(results.All(r => r.IsError && r.Content.StartsWith("Aborted")), "aborted results");
        var ends = h.Bus.OfType(EventTypes.ToolEnd);
        Check.Equal(1, ends.Count, "tool.end for the started call");
        Check.Equal(1, h.Catalog.Calls);
    }

    // E2E regression: the shell tools do not throw on cancellation (they kill the process and return an "[aborted]"
    // result), so after a user abort the runner went on to execute the remaining calls of the batch.
    private static async Task AbortDuringSwallowingTool()
    {
        await using var h = await TestHost.StartAsync();
        var started = new TaskCompletionSource();
        var shell = new FakeTool("shellish", async (ctx, args, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return ToolResult.Error("[aborted; the process tree was killed]"); }
            return ToolResult.Ok("never");
        });
        h.AddTool(shell);
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Tools(Reply.Call("shellish"), Reply.Call("shellish"), Reply.Call("shellish"));
        await h.SendAsync(s.Id, "go");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Runtime.AbortAsync(s.Id);
        await h.IdleAsync(s.Id);
        Check.Equal(1, shell.Calls, "only the first call ran");
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).ToList();
        Check.Equal(3, results.Count);
        Check.Contains(results[0].Content, "[aborted");
        Check.True(results.Skip(1).All(r => r.IsError && r.Content == AgentRunner.NotExecutedAbort), "the rest was not executed");
        Check.Equal(1, h.Bus.OfType(EventTypes.ToolStart).Count, "no tool.start for the skipped calls");
        Check.Equal(1, h.Catalog.Calls);
    }
 
    /// <summary>
    /// A call's result is persisted before its after-hooks run: a run cancelled in an after-hook must not write a second,
    /// contradictory result ("aborted") for the same call id - the transcript keeps the one real result (idea-3coif8).
    /// </summary>
    private static async Task OneResultWhenCancelledInAfterHook()
    {
        await using var h = await TestHost.StartAsync();
        var hook = new AfterToolGate();
        h.Services.Register<IAgentHook>(hook);
        h.AddTool(Echo());
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("echo", new { text = "x" });
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => hook.Entered.IsCompleted, "the after-hook is running: the result is persisted");
        await h.Runtime.AbortAsync(s.Id);
        await h.IdleAsync(s.Id);

        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).ToList();
        Check.Equal(1, results.Count, "one result per call id");
        Check.Equal("echo:x", results[0].Content, "the real result, not 'aborted'");
        Check.False(results[0].IsError);
        Check.Equal(1, h.Bus.OfType(EventTypes.ToolEnd).Count, "one tool.end, no skipped one for the same call");
        Check.Equal(1, h.Catalog.Calls);
    }

    /// <summary>An after-tool hook that signals entry, then blocks until the run's token is cancelled.</summary>
    private sealed class AfterToolGate : IAgentHook
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public int Order => 0;
        public ValueTask OnRunStartAsync(AgentRunContext run) => ValueTask.CompletedTask;
        public ValueTask OnBeforeModelCallAsync(AgentTurnContext turn) => ValueTask.CompletedTask;
        public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant) => ValueTask.FromResult<TurnDecision?>(null);
        public ValueTask<ModelErrorDecision?> OnModelErrorAsync(AgentTurnContext turn, Exception error) => ValueTask.FromResult<ModelErrorDecision?>(null);
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call) => ValueTask.FromResult<ToolCallDecision?>(null);
        public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
        {
            async Task Body()
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, turn.Run.CancellationToken).ConfigureAwait(false); // throws when the run is cancelled
            }
            return new ValueTask(Body());
        }
        public ValueTask OnRunEndAsync(AgentRunContext run) => ValueTask.CompletedTask;
    }

    // A stop leaves the queued input with no run left to run in: the UI's chip send action dequeues it and
    // sends the same text as the next turn. The queue must survive the abort, the removal is what authorizes
    // the send (a second removal reports "gone", so the text is never sent twice), and the resent text starts
    // its own run.
    /// <summary>
    /// The steps of a turn that can fail (the model is gone, the budget is spent, no slot) come before the queue is taken, so
    /// a run that failed there still holds the input. It used to start the next run for it at once, which failed the same
    /// way, appended the same error and started again, for as long as the cause lasted.
    /// </summary>
    private static async Task FailedRunKeepsQueue()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        string? model = null;
        var breakOnce = 1;
        h.AddTool(new FakeTool("work", async (ctx, args, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            // The model goes away mid-run: the next turn fails before the queue is read. Once only: the second run must work.
            if (Interlocked.Exchange(ref breakOnce, 0) == 1) h.SetModel(ctx.SessionId, "fake/gone");
            return ToolResult.Ok("done");
        }));
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tool("work", new { });
        var s = h.NewSession();
        model = h.Sessions.GetSession(s.Id)!.Model;
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first call");
        await h.SendAsync(s.Id, "steer now", DeliveryMode.Steer);
        await h.SendAsync(s.Id, "later", DeliveryMode.Queue);
        gate.SetResult();
        var a = await h.IdleAsync(s.Id);

        int ErrorNotices() => h.Messages(s.Id).Count(m => m.MetaString("kind") == "error");
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Contains(a.Error ?? "", "not available");
        Check.Equal(1, a.Runs, "no run was started for the queue");
        Check.Equal(1, ErrorNotices());
        await Task.Delay(300);   // a negative: the old behaviour restarted at once, so a short wait is enough to see it
        a = h.Runtime.GetBySession(s.Id)!;
        Check.Equal(1, a.Runs, "still one run");
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Equal(1, ErrorNotices());
        Check.Equal(2, h.Runtime.GetQueue(s.Id).Count, "the queue is as the user left it: the steer and the follow-up");
        Check.Equal(2, a.QueuedMessages);

        // the cause is fixed and the user writes again: the same run takes what was waiting, in order
        h.Sessions.UpdateSession(s.Id, x => x.Model = model);
        await h.SendAsync(s.Id, "try again");
        a = await h.IdleAsync(s.Id);
        Check.Equal(0, h.Runtime.GetQueue(s.Id).Count, "the queue was taken");
        var said = h.Messages(s.Id).Where(m => m.Role == MessageRole.User).Select(m => m.Text).ToList();
        Check.Equal("go,try again,steer now,later", string.Join(",", said));
    }

    /// <summary>A steer cancels the agent's signal so a guard approval, ask_user or agent wait stops waiting for it. Taking the
    /// steer back leaves nothing to steer to: the old cancel must not make every later wait return at once.</summary>
    private static async Task StaleSteerSignal()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("first", c => gate.Task.WaitAsync(c));
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first call");
        var state = ((NetPI.Runtime.AgentRuntime)h.Runtime).FindState(s.Id)!;
        Check.False(state.SteerSignal.IsCancellationRequested, "nothing steered yet");

        await h.SendAsync(s.Id, "one", DeliveryMode.Steer);
        await h.SendAsync(s.Id, "two", DeliveryMode.Steer);
        Check.True(state.SteerSignal.IsCancellationRequested, "a steer cancels the signal");
        var queued = h.Runtime.GetQueue(s.Id);
        Check.True(h.Runtime.RemoveQueued(s.Id, queued[0].Id), "take one back");
        Check.True(state.SteerSignal.IsCancellationRequested, "one steer is still queued: the cancel stands");
        Check.True(h.Runtime.RemoveQueued(s.Id, queued[1].Id), "take the other back");
        Check.False(state.SteerSignal.IsCancellationRequested, "nothing left to steer to: a wait is not interrupted by nothing");

        // a steer that is not taken back is delivered as before
        await h.SendAsync(s.Id, "three", DeliveryMode.Steer);
        Check.True(state.SteerSignal.IsCancellationRequested);
        gate.SetResult();
        await h.IdleAsync(s.Id);
        Check.Equal(0, h.Runtime.GetQueue(s.Id).Count);
        Check.True(h.Messages(s.Id).Any(m => m.Text == "three"), "the steer was delivered");
    }

    private static async Task AbortKeepsQueue()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Text("first", c => gate.Task.WaitAsync(c)),
            _ => Reply.Text("second"),
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first call");
        await h.SendAsync(s.Id, "change of plans", DeliveryMode.Steer);
        Check.Equal(1, h.Runtime.GetQueue(s.Id).Count, "the steer waits in the queue");
        Check.True(await h.Runtime.AbortAsync(s.Id), "abort");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Equal(1, a.Runs, "the abort started no follow-up run for the queue");
        var queued = h.Runtime.GetQueue(s.Id);
        Check.Equal(1, queued.Count, "the queue survives the abort");
        var q = queued[0];
        Check.Equal("steer", q.Mode);
        Check.Equal("user", q.Source);
        Check.True(h.Runtime.RemoveQueued(s.Id, q.Id), "dequeue");
        Check.False(h.Runtime.RemoveQueued(s.Id, q.Id), "not removed twice: already gone");
        Check.Equal(0, h.Runtime.GetQueue(s.Id).Count);
        // what the chip's send action does: the removal succeeded, so send the same text as the next turn
        await h.SendAsync(s.Id, q.Text, DeliveryMode.Auto);
        a = await h.IdleAsync(s.Id);
        Check.Equal(2, a.Runs, "the resent text started its own run");
        Check.Equal(0, a.QueuedMessages);
        Check.Equal("go,change of plans", string.Join(",", h.Messages(s.Id).Where(m => m.Role == MessageRole.User).Select(m => m.Text)),
            "the resent text is the next turn");
        Check.Equal("second", h.Messages(s.Id)[^1].Text, "the new run answered the resent text");
    }

    private static async Task ToolErrors()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Echo());
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("ok")
            : Reply.Tools(Reply.Call("nope", new { }), Reply.Call("echo", "{\"text\": bad json"), Reply.Call("ECHO", new { text = "x" }));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).ToList();
        Check.Equal(3, results.Count);
        Check.True(results[0].IsError);
        Check.Contains(results[0].Content, "Unknown tool 'nope'");
        Check.Contains(results[0].Content, "echo");
        Check.True(results[1].IsError);
        Check.Contains(results[1].Content, "Invalid JSON arguments for echo");
        Check.False(results[2].IsError, "case-insensitive tool name");
        Check.Equal("echo:x", results[2].Content);
    }

    private static async Task ToolHelpCall()
    {
        await using var h = await TestHost.StartAsync();
        var manual = new FakeTool("manual", (ctx, args, ct) => Task.FromResult(ToolResult.Ok("ran")), help: "Actions: a, b. Example: {\"action\":\"a\"}.");
        var plain = new FakeTool("plain", (ctx, args, ct) => Task.FromResult(ToolResult.Ok("ran")));
        h.AddTool(manual);
        h.AddTool(plain);
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("ok")
            : Reply.Tools(Reply.Call("manual", new { help = true }), Reply.Call("plain", new { help = "true" }), Reply.Call("plain", new { help = false }));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).ToList();
        Check.Equal(3, results.Count);
        Check.False(results[0].IsError);
        Check.Contains(results[0].Content, "manual: Fake tool manual. Does test things.\n\nActions: a, b. Example: {\"action\":\"a\"}.\n\nArguments (JSON schema): {");
        Check.Contains(results[1].Content, "plain: Fake tool plain. Does test things.\n\nArguments (JSON schema): ");
        Check.Equal(0, manual.Calls, "help does not run the tool");
        Check.Equal("ran", results[2].Content);
        Check.Equal(1, plain.Calls, "help: false runs it");
    }

    private static async Task ToolExceptionAndTruncation()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agent.maxToolResultChars", 1000));
        h.AddTool(new FakeTool("boom", (ctx, args, ct) => throw new IOException("disk on fire")));
        h.AddTool(new FakeTool("big", (ctx, args, ct) => Task.FromResult(ToolResult.Ok(new string('a', 3000) + new string('z', 2000)))));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tools(Reply.Call("boom"), Reply.Call("big"));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).ToList();
        Check.True(results[0].IsError);
        Check.Contains(results[0].Content, "disk on fire");
        Check.False(results[1].IsError);
        Check.Contains(results[1].Content, "characters not shown");
        Check.True(results[1].Content.StartsWith("aaaa") && results[1].Content.EndsWith("zzzz"), "head and tail kept");
        Check.True(results[1].Content.Length < 1500, $"about 1000 chars plus the note (got {results[1].Content.Length})");
        // the whole result is in a file the note names, to read or grep
        var m = System.Text.RegularExpressions.Regex.Match(results[1].Content, @"The whole result is in (.+?): read");
        Check.True(m.Success, "the note names the file");
        Check.Equal(new string('a', 3000) + new string('z', 2000), File.ReadAllText(m.Groups[1].Value));
    }

    /// <summary>The limiter the tool batch calls: what the model sees is the head, the note and the tail, and the
    /// whole result goes to the file the note names (or it says it could not be saved).</summary>
    private static void ResultLimiterKeepsEnds()
    {
        var content = new string('a', 4000) + new string('b', 1000);
        string? saved = null;
        var limited = ResultLimiter.Limit(content, 1000, c => saved = c);
        Check.Equal(content, saved, "the whole result is written out, not the trimmed one");
        Check.True(limited.StartsWith(new string('a', 666), StringComparison.Ordinal), $"it starts with the head: {limited[..20]}");
        Check.True(limited.EndsWith(new string('b', 334), StringComparison.Ordinal), $"and ends with the tail: {limited[^20..]}");
        Check.Contains(limited, $"[... {4000:N0} of {5000:N0} characters not shown (limit {1000:N0}). The whole result is in ");

        Check.Contains(ResultLimiter.Limit(content, 1000, _ => null), "It could not be saved;");

        // a short result, and no limit at all, are the model's as they are (and cost no file)
        Check.Equal("short", ResultLimiter.Limit("short", 1000, _ => throw new InvalidOperationException("not saved")));
        Check.Equal(content, ResultLimiter.Limit(content, 0, _ => throw new InvalidOperationException("not saved")));

        // a pair that would be cut in half is left out of both ends (the head at 666, the tail at the last 334)
        var emoji = new string('a', 665) + "\U0001F600" + "\U0001F600" + new string('c', 333);
        var paired = ResultLimiter.Limit(emoji, 1000, _ => null);
        Check.True(paired.StartsWith(new string('a', 665), StringComparison.Ordinal), "the head stops before the pair");
        Check.True(paired.EndsWith("\U0001F600" + new string('c', 333), StringComparison.Ordinal), "the tail starts after the pair");
        Check.Equal(2, paired.Count(char.IsSurrogate), "so no half of a pair is left: the one pair is whole");
    }

    /// <summary>No ISystemPromptBuilder in the app (the context plugin is not loaded): the run builds the prompt from
    /// the sections itself, announces the working directory once, and the next turn sends that same prefix.</summary>
    private static async Task FallbackPromptIsFrozen()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var ctx = new TestPluginContext(h, "prompt-test");
        var renders = 0;
        ctx.Services.Register<IPromptSection>(new CountingSection(() => renders++));
        var session = h.NewSession();
        var pc = new PromptContext
        {
            Session = session,
            Cwd = h.Workspace,
            Model = h.Catalog.Cached.Single(m => m.Ref == "fake/local"),
            Tools = [new ToolDefinition { Name = "echo", Description = "echoes", PromptGuidelines = ["use it sparingly"] }],
        };
        var prompt = await FallbackPromptBuilder.BuildAsync(ctx, pc, SessionPrompt.Revision(session), default);
        Check.Contains(prompt, "You are a coding agent running in NetPI", "the built-in prompt is the identity");
        Check.Contains(prompt, $"Working directory: {h.Workspace}");
        Check.Contains(prompt, "Model: fake/local");
        Check.Contains(prompt, "SECTION", "a registered section is part of it");
        Check.Contains(prompt, "- use it sparingly", "and the tools' guidelines with it");
        var notices = h.Messages(session.Id).Where(m => m.Role == MessageRole.Notice).ToList();
        Check.Equal(1, notices.Count, "the working directory is announced once");
        Check.Contains(notices[0].Text, $"Working directory: {h.Workspace}");

        // the next turn: the stored prefix, and nothing rendered or announced again
        var again = h.Sessions.GetSession(session.Id)!;
        var second = await FallbackPromptBuilder.BuildAsync(ctx, new PromptContext
        {
            Session = again, Cwd = pc.Cwd, Model = pc.Model, Tools = pc.Tools,
        }, SessionPrompt.Revision(again), default);
        Check.Equal(prompt, second, "the frozen prompt is sent again");
        Check.Equal(1, renders, "the sections are rendered once");
        Check.Equal(1, h.Messages(session.Id).Count(m => m.Role == MessageRole.Notice), "and the notice is not repeated");
    }

    private sealed class CountingSection(Action onRender) : IPromptSection
    {
        public string Id => "counted";
        public int Order => 50;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
        {
            onRender();
            return ValueTask.FromResult<string?>("SECTION");
        }
    }

    private static async Task ModelError()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Reply.Fail(new ModelException("backend exploded", transient: false, statusCode: 500));
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        var a = await h.IdleAsync(s.Id);
        var last = h.Messages(s.Id)[^1];
        Check.Equal(MessageRole.Notice, last.Role);
        Check.Equal("error", last.MetaString("kind"));
        Check.Contains(last.Text, "backend exploded");
        Check.Contains(last.Text, "500");
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Contains(a.Error, "backend exploded");
        Check.Equal(1, h.Bus.OfType(EventTypes.StreamEnd).Count);
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(p => p.Busy), "slot released");

        // the next message clears the error
        h.Catalog.Handler = (r, ct) => Reply.Text("fine now");
        await h.SendAsync(s.Id, "again");
        a = await h.IdleAsync(s.Id);
        Check.Equal(null, a.Error);
        Check.Equal(2, a.Runs);
    }

    private static async Task MissingModel()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession(model: "ghost/model");
        await h.SendAsync(s.Id, "go");
        var a = await h.IdleAsync(s.Id);
        var last = h.Messages(s.Id)[^1];
        Check.Equal("error", last.MetaString("kind"));
        Check.Contains(last.Text, "ghost/model");
        Check.Equal(0, h.Catalog.Calls);
        Check.Contains(a.Error, "ghost/model");
    }

    // E2E regression: right after startup the host catalog cache is empty (the first listing is still running), so
    // DefaultModelRef was null and the first message of a session without a model failed with "No model is configured".
    private static async Task DefaultModelWhileCatalogLoads()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        var def = h.Catalog.DefaultModelRef;
        h.Catalog.DefaultModelRef = null; // cache not populated yet
        var listed = 0;
        h.Catalog.OnList = () => { listed++; h.Catalog.DefaultModelRef = def; };
        await h.SendAsync(s.Id, "hi");
        var a = await h.IdleAsync(s.Id);
        Check.True(listed >= 1, "the runner listed the models");
        Check.Equal(MessageRole.Assistant, h.Messages(s.Id)[^1].Role);
        Check.Equal("ok", h.Messages(s.Id)[^1].Text);
        Check.Equal(def, a.Model);
        Check.Equal(1, h.Catalog.Calls);
    }

    private static async Task MaxTurns()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agent.maxTurns", 3));
        h.AddTool(Echo());
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Tool("echo", new { text = "again" });
        await h.SendAsync(s.Id, "loop forever");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(3, h.Catalog.Calls);
        var last = h.Messages(s.Id)[^1];
        Check.Equal("error", last.MetaString("kind"));
        Check.Contains(last.Text, "agent.maxTurns");
        Check.Contains(a.Error, "limit");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> ResetThenAnswer([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new TextDelta("garbage that will be discarded");
        yield return new StreamNotice("retrying in 1s", "warn");
        yield return new StreamReset("retry");
        yield return new TextDelta("clean answer");
        yield return new StreamCompleted(Reply.Message(new TextPart { Text = "clean answer" }));
    }

    private static async Task StreamResetAndNotice()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => ResetThenAnswer(ct);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(1, h.Bus.OfType(EventTypes.StreamReset).Count);
        var notice = FakeBus.Data(h.Bus.OfType(EventTypes.AgentNotice).Single());
        Check.Equal("warn", (string?)notice["level"]);
        Check.Equal("retrying in 1s", (string?)notice["text"]);
        // after the reset only the clean text streams
        var all = h.Bus.All;
        var resetIdx = all.FindIndex(e => e.Type == EventTypes.StreamReset);
        var after = string.Concat(all.Skip(resetIdx).Where(e => e.Type == EventTypes.StreamDelta).Select(e => (string?)FakeBus.Data(e)["text"]));
        Check.Equal("clean answer", after);
        Check.Equal("clean answer", h.Messages(s.Id)[^1].Text);
    }

    private static async Task ModelSwitchMidRun()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Echo());
        var s = h.NewSession(model: "fake/local");
        h.Catalog.Handler = (r, ct) =>
        {
            if (Reply.HasToolResult(r)) return Reply.Text("answered by " + r.Model.Ref);
            h.SetModel(s.Id, "cloud/big"); // user switches the model while the first call runs
            return Reply.Tool("echo", new { text = "x" });
        };
        await h.SendAsync(s.Id, "go");
        var a = await h.IdleAsync(s.Id);
        var reqs = h.Catalog.Requests.ToList();
        Check.Equal("fake/local", reqs[0].Model.Ref);
        Check.Equal("cloud/big", reqs[1].Model.Ref);
        Check.Equal("answered by cloud/big", h.Messages(s.Id)[^1].Text);
        Check.Equal("cloud", a.Agent, "moved to the cloud provider's slots");
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(p => p.Busy));
    }

    // ---------------------------------------------------------------- hooks

    private sealed class Hook : IAgentHook
    {
        public int Order { get; init; }
        public Func<AgentTurnContext, ValueTask>? BeforeModel;
        public Func<AgentTurnContext, ChatMessage, TurnDecision?>? After;
        public Func<AgentTurnContext, Exception, ModelErrorDecision?>? OnError;
        public Func<ToolCallPart, ToolCallDecision?>? BeforeTool;
        public List<string> Log { get; } = [];

        public ValueTask OnRunStartAsync(AgentRunContext run) { lock (Log) Log.Add("start"); return ValueTask.CompletedTask; }
        public ValueTask OnBeforeModelCallAsync(AgentTurnContext turn) => BeforeModel?.Invoke(turn) ?? ValueTask.CompletedTask;
        public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant) => ValueTask.FromResult(After?.Invoke(turn, assistant));
        public ValueTask<ModelErrorDecision?> OnModelErrorAsync(AgentTurnContext turn, Exception error) => ValueTask.FromResult(OnError?.Invoke(turn, error));
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call) => ValueTask.FromResult(BeforeTool?.Invoke(call));
        public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result) { lock (Log) Log.Add("tool:" + call.Name); return ValueTask.CompletedTask; }
        public ValueTask OnRunEndAsync(AgentRunContext run) { lock (Log) Log.Add("end:" + run.Outcome); return ValueTask.CompletedTask; }
    }

    private static async Task HookInject()
    {
        await using var h = await TestHost.StartAsync();
        var n = 0;
        h.Services.Register<IAgentHook>(new Hook { After = (t, m) => Interlocked.Increment(ref n) == 1 ? TurnDecision.Inject("Please double-check your answer.", "nudge") : null });
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Reply.Text(Interlocked.Increment(ref calls) == 1 ? "answer1" : "answer2");
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var msgs = h.Messages(s.Id);
        Check.Equal("User,Notice,Assistant,Notice,Assistant", string.Join(",", msgs.Select(m => m.Role)));
        Check.Equal("nudge", msgs[3].MetaString("kind"));
        Check.Equal("Please double-check your answer.", msgs[3].Text);
        Check.Equal("answer2", msgs[4].Text);
        Check.Contains(Reply.LastUser(h.Catalog.Requests.Last()), "<system-notice kind=\"nudge\">");
    }

    private static async Task HookReplace()
    {
        await using var h = await TestHost.StartAsync();
        var echo = Echo();
        h.AddTool(echo);
        h.Services.Register<IAgentHook>(new Hook
        {
            After = (t, m) => m.Text.Contains("<tool>")
                ? TurnDecision.Replace(new ChatMessage { Parts = [new TextPart { Text = "calling echo" }, Reply.Call("echo", new { text = "repaired" }, "call_rep")] })
                : null,
        });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Text("<tool>echo repaired</tool>");
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var msgs = h.Messages(s.Id);
        Check.Equal("User,Notice,Assistant,Tool,Assistant", string.Join(",", msgs.Select(m => m.Role)));
        Check.Equal("calling echo", msgs[2].Text);
        Check.Equal("tool_use", msgs[2].StopReason);
        Check.Equal("call_rep", msgs[2].ToolCalls.Single().Id);
        Check.Equal("echo:repaired", msgs[3].ToolResults.Single().Content);
        Check.Equal(1, echo.Calls);
        Check.True(h.Bus.OfType(EventTypes.MessageUpdated).Count >= 1, "the repaired message is written back (message.updated)");
    }

    private static async Task HookStop()
    {
        await using var h = await TestHost.StartAsync();
        var echo = Echo();
        h.AddTool(echo);
        h.Services.Register<IAgentHook>(new Hook { After = (t, m) => TurnDecision.Stop() });
        h.Catalog.Handler = (r, ct) => Reply.Tool("echo", new { text = "x" });
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(1, h.Catalog.Calls);
        Check.Equal(0, echo.Calls);
        var last = h.Messages(s.Id)[^1];
        Check.Equal(MessageRole.Tool, last.Role);
        Check.Contains(last.ToolResults.Single().Content, "Not executed");
    }

    private static async Task HookMisc()
    {
        await using var h = await TestHost.StartAsync();
        var echo = Echo();
        h.AddTool(echo);
        var seen = new List<string>();
        var hook = new Hook
        {
            Order = 5,
            BeforeModel = t =>
            {
                t.SystemPrompt += "\nHOOKED";
                return ValueTask.CompletedTask;
            },
            OnError = (t, e) => new ModelErrorDecision { Retry = true },
            BeforeTool = c =>
            {
                lock (seen) seen.Add(c.Arguments);
                return c.Name == "echo" && c.Arguments.Contains("secret") ? new ToolCallDecision { Block = true, Reason = "no secrets" } : null;
            },
        };
        // a second hook with a lower order runs first; the arguments it changes are what the next hook sees
        var first = new Hook
        {
            Order = -1,
            BeforeTool = c => c.Arguments.Contains("fixme") ? new ToolCallDecision { Arguments = "{\"text\":\"fixed\"}" }
                : c.Arguments.Contains("leak") ? new ToolCallDecision { Arguments = "{\"text\":\"secret leak\"}" } : null,
        };
        h.Services.Register<IAgentHook>(hook);
        h.Services.Register<IAgentHook>(first);
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Fail(new ModelException("context overflow", false) { ContextOverflow = true }),
            2 => Reply.Tools(Reply.Call("echo", new { text = "secret" }), Reply.Call("echo", new { text = "fixme" }), Reply.Call("echo", new { text = "leak" })),
            _ => Reply.Text("done"),
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(3, h.Catalog.Calls, "one retry after the error");
        Check.True(h.Catalog.Requests.All(r => r.SystemPrompt!.EndsWith("HOOKED")), "prompt modified");
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).ToList();
        Check.True(results[0].IsError);
        Check.Contains(results[0].Content, "no secrets");
        Check.Equal("echo:fixed", results[1].Content);
        Check.True(seen.Contains("{\"text\":\"fixed\"}") && !seen.Any(a => a.Contains("fixme")), "the later hook saw the changed arguments: " + string.Join(" ", seen));
        Check.Contains(results[2].Content, "no secrets", "blocked on the arguments the first hook made");
        var assistant = h.Messages(s.Id).First(m => m.Role == MessageRole.Assistant);
        Check.Contains(assistant.ToolCalls.ElementAt(1).Arguments, "fixed");
        Check.NotContains(assistant.ToolCalls.ElementAt(2).Arguments, "secret", "a blocked call keeps the arguments the model sent");
        Check.False(h.Messages(s.Id).Any(m => m.MetaString("kind") == "error"), "no error notice after retry");
        Check.Equal("start|tool:echo|tool:echo|tool:echo|end:completed", string.Join("|", hook.Log));
    }

    private sealed class CountingMiddleware : IModelMiddleware
    {
        public int Calls;
        public int Order => 0;

        public async IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            yield return new StreamNotice("middleware says hi");
            await foreach (var e in next(request, ct).WithCancellation(ct)) yield return e;
        }
    }

    private static async Task Middleware()
    {
        await using var h = await TestHost.StartAsync();
        var mw = new CountingMiddleware();
        h.Services.Register<IModelMiddleware>(mw);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(1, mw.Calls);
        Check.Equal("middleware says hi", (string?)FakeBus.Data(h.Bus.OfType(EventTypes.AgentNotice).Single())["text"]);
    }

    private static async Task Rpcs()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("reply to " + Reply.LastUser(r), c => gate.Task.WaitAsync(c));
        var s = h.NewSession();
        var sent = await h.Rpc.CallAsync("agent.send", new { sessionId = s.Id, text = "look", images = new[] { new { mediaType = "image/png", data = "iVBORw0KGgo=" } } });
        Check.Equal(s.Id, (string?)sent!["sessionId"]);
        var agentId = (string)sent["id"]!;
        await Wait.Until(() => h.Catalog.Calls == 1, "model called");
        var user = h.Messages(s.Id)[0];
        Check.Equal("iVBORw0KGgo=", user.Parts.OfType<ImagePart>().Single().Data);

        await h.Rpc.CallAsync("agent.send", new { sessionId = s.Id, text = "later", mode = "queue" });
        var queue = (JsonArray)(await h.Rpc.CallAsync("agent.queue", new { sessionId = s.Id }))!;
        Check.Equal(1, queue.Count);
        Check.Equal("queue", (string?)queue[0]!["mode"]);
        var removed = await h.Rpc.CallAsync("agent.dequeue", new { sessionId = s.Id, id = (string)queue[0]!["id"]! });
        Check.Equal(true, removed!.GetValue<bool>());
        Check.Equal(0, ((JsonArray)(await h.Rpc.CallAsync("agent.queue", new { sessionId = s.Id }))!).Count);

        // a subagent's report waiting for the agent is internal: it can't be removed (the person's own inputs can)
        await h.Runtime.SendAsync(s.Id, new UserInput { Text = "<agent-result>report</agent-result>", AsNotice = true, NoticeKind = "agent-result", Source = "agent:agt_child" });
        var report = ((JsonArray)(await h.Rpc.CallAsync("agent.queue", new { sessionId = s.Id }))!).Single()!;
        Check.Equal("agent:agt_child", (string?)report["source"]);
        try
        {
            await h.Rpc.CallAsync("agent.dequeue", new { sessionId = s.Id, id = (string)report["id"]! });
            throw new AssertException("expected forbidden");
        }
        catch (RpcException ex)
        {
            Check.Equal("forbidden", ex.Code);
            Check.Contains(ex.Message, "comes from agent:agt_child, not from you");
        }
        Check.Equal(1, ((JsonArray)(await h.Rpc.CallAsync("agent.queue", new { sessionId = s.Id }))!).Count, "the report stays for the agent");

        var byId = await h.Rpc.CallAsync("agent.get", new { id = agentId });
        Check.Equal("running", (string?)byId!["status"]);
        var bySession = await h.Rpc.CallAsync("agent.get", new { sessionId = s.Id });
        Check.Equal(agentId, (string?)bySession!["id"]);
        Check.Equal(null, await h.Rpc.CallAsync("agent.get", new { sessionId = "ses_none" }));
        var active = (JsonArray)(await h.Rpc.CallAsync("runs.list", new { includeFinished = false }))!;
        Check.Equal(1, active.Count);

        var aborted = await h.Rpc.CallAsync("agent.abort", new { sessionId = s.Id });
        Check.Equal(true, aborted!.GetValue<bool>());
        await h.IdleAsync(s.Id);
        Check.Equal(0, ((JsonArray)(await h.Rpc.CallAsync("runs.list", new { includeFinished = false }))!).Count);
        Check.Equal(1, ((JsonArray)(await h.Rpc.CallAsync("runs.list", new { }))!).Count);

        try
        {
            await h.Rpc.CallAsync("agent.send", new { sessionId = "ses_missing", text = "x" });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex)
        {
            Check.Equal("not_found", ex.Code);
            // one wording for "that chat is not there", whatever asked for it (ISessionStore.Require)
            Check.Equal("Session ses_missing not found", ex.Message);
        }
    }

    private static async Task PluginStop()
    {
        var h = await TestHost.StartAsync();
        try
        {
            h.Catalog.Handler = (r, ct) => Hang(ct);
            var s = h.NewSession();
            var sub = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "hang around", Name = "hanger" });
            await h.SendAsync(s.Id, "go");
            await Wait.Until(() => h.Catalog.Calls == 2, "both running");
            await Wait.Until(() => h.Bus.OfType(EventTypes.StreamDelta).Count(e => ((string?)FakeBus.Data(e)["text"])?.Contains("answer") == true) >= 2, "both streamed");
            var runtime = h.Plugin<RuntimePlugin>().Runtime!;
            await h.StopPluginAsync("netpi.runtime");
            Check.Equal(null, h.Services.Get<IAgentRuntime>());
            var main = runtime.GetBySession(s.Id)!;
            Check.Equal(AgentStatus.Idle, main.Status);
            var subInfo = runtime.Get(sub.Id)!;
            Check.Equal(AgentStatus.Cancelled, subInfo.Status);
            Check.Equal("plugin reloaded", subInfo.Error);
            Check.Equal("aborted", h.Messages(s.Id)[^1].StopReason);
            Check.Equal(0, h.Scheduler!.Snapshot().Sum(p => p.Busy), "slots released");
            try
            {
                await runtime.SendAsync(s.Id, new UserInput { Text = "x" });
                throw new AssertException("expected InvalidOperationException");
            }
            catch (InvalidOperationException) { }
        }
        finally
        {
            await h.DisposeAsync();
        }
    }
}
