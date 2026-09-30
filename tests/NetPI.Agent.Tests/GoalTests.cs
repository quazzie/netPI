using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI.Goal;
using NetPI.Nudge;

namespace NetPI.Agent.Tests;

/// <summary>Goals with the real runner and a scripted model: the loop, the runtime's stops, notices and the tools.</summary>
public static class GoalTests
{
    public static void Register(TestRunner t)
    {
        t.Add("goal: set by the user, the agent is started again until goal_update complete", UntilComplete);
        t.Add("goal: runs without a successful tool call pause it (goal.noProgressLimit)", NoProgress);
        t.Add("goal: stopping the run pauses it; resume starts again with a resumed notice", StopAndResume);
        t.Add("goal: a failed run pauses it with the error", FailedRun);
        t.Add("goal: goal.maxContinuations and the token budget pause it", Limits);
        t.Add("goal: a call a hook decided (nudge) is metered too, with the nudge plugin running", NudgedCallsCounted);
        t.Add("goal: blocked by the model stops the loop; goal_set is refused while a goal is open", BlockedAndSetRefused);
        t.Add("goal: paused by the user mid-run, the model hears it at its next call and no run follows", PausedMidRun);
        t.Add("goal: goal_set on the user's request starts the loop", SetByModel);
        t.Add("goal: announced again after compaction, once; changes and resumes are announced", Notices);
        t.Add("goal: RPC validation (empty, too long, subagent sessions, nothing to pause)", RpcValidation);
    }

    private static async Task<(TestHost H, TestPluginContext Ctx)> Start()
    {
        var h = await TestHost.StartAsync();
        var ctx = await h.StartPluginAsync(new GoalPlugin());
        return (h, ctx);
    }

    private static JsonObject? GoalOf(TestHost h, string sid) => h.Sessions.GetSession(sid)?.Meta?["goal"] as JsonObject;
    private static string? StatusOf(TestHost h, string sid) => (string?)GoalOf(h, sid)?["status"];
    private static long IntOf(TestHost h, string sid, string key) => GoalOf(h, sid)?[key] is { } n ? long.Parse(n.ToJsonString()) : -1;

    private static bool Idle(TestHost h, string sid) =>
        h.Runtime.GetBySession(sid) is not { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded };

    /// <summary>Wait for the goal to leave "active" and the agent to be idle (with no continuation pending).</summary>
    private static async Task SettledAsync(TestHost h, string sid, string status)
    {
        await Wait.Until(() => StatusOf(h, sid) == status && Idle(h, sid), $"goal {status} and idle (now {StatusOf(h, sid)})");
        await Task.Delay(150); // a continuation would have started by now
        Check.True(Idle(h, sid), "no run after the goal stopped");
    }

    private static List<ChatMessage> GoalNotices(TestHost h, string sid) =>
        h.Messages(sid).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == "goal").ToList();

    private static FakeTool Work(Action? onCall = null) => new("work", (c, a, ct) =>
    {
        onCall?.Invoke();
        return Task.FromResult(ToolResult.Ok("worked"));
    });

    private static string Last(ModelRequest r) => r.Messages.LastOrDefault()?.Text ?? "";

    private static async IAsyncEnumerable<ModelStreamEvent> Hang([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private static async Task UntilComplete()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var works = 0;
        h.AddTool(Work(() => Interlocked.Increment(ref works)));
        var s = h.NewSession(title: "");
        var calls = 0;
        var firstRequest = "";
        h.Catalog.Handler = (r, ct) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) firstRequest = string.Join("\n", r.Messages.Select(m => m.Text)); // the working-directory notice comes last
            return n switch
            {
                1 => Reply.Tool("work"),
                2 => Reply.Text("Part one is done."), // stops without goal_update: started again
                3 => Reply.Tool("work"),
                4 => Reply.Tool("goal_update", new { status = "complete", summary = "Both parts done, checked with work." }),
                _ => Reply.Text("All done."),
            };
        };
        var set = await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Do both parts" });
        Check.Equal("active", (string?)set!["status"]);
        Check.Equal("Do both parts", h.Sessions.GetSession(s.Id)!.Title, "an untitled session is named after the goal");
        await SettledAsync(h, s.Id, "complete");

        Check.Contains(firstRequest, "The user set a goal for this session");
        Check.Contains(firstRequest, "<goal>\nDo both parts\n</goal>");
        var notices = GoalNotices(h, s.Id);
        Check.Equal(2, notices.Count);
        Check.Contains(notices[1].Text, "automatic continuation 1");
        Check.Equal((string?)set["id"], notices[1].MetaString("goalId"));
        Check.Equal(2, works);
        Check.Equal(2, h.Runtime.GetBySession(s.Id)!.Runs);
        Check.Equal(1, IntOf(h, s.Id, "continuations"));
        Check.Equal("Both parts done, checked with work.", (string?)GoalOf(h, s.Id)!["reason"]);
        Check.Equal(5 * 110L, IntOf(h, s.Id, "tokensUsed"));
        Check.Equal("All done.", h.Messages(s.Id)[^1].Text);
    }

    private static async Task NoProgress()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Text("Thinking about it.");
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Fix it" });
        await SettledAsync(h, s.Id, "paused");
        // the first run was started by setting the goal; the next three were automatic and made no progress
        Check.Equal(4, h.Runtime.GetBySession(s.Id)!.Runs);
        Check.Equal(3, IntOf(h, s.Id, "continuations"));
        Check.Contains((string?)GoalOf(h, s.Id)!["reason"], "No progress: 3 automatic runs in a row");
    }

    private static async Task StopAndResume()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var resumed = "";
        var calling = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) =>
        {
            if (Last(r).Contains("The user resumed the goal")) resumed = Last(r);
            if (resumed.Length == 0)
            {
                calling.TrySetResult();
                return Hang(ct);
            }
            return Reply.HasToolResult(r) ? Reply.Text("Done now.") : Reply.Tool("goal_update", new { status = "complete", summary = "Done." });
        };
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Long job" });
        await calling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check.True(await h.Runtime.AbortAsync(s.Id), "abort");
        await SettledAsync(h, s.Id, "paused");
        Check.Equal("Stopped.", (string?)GoalOf(h, s.Id)!["reason"]);

        var r = await h.Rpc.CallAsync("goal.resume", new { sessionId = s.Id });
        Check.Equal("active", (string?)r!["status"]);
        await SettledAsync(h, s.Id, "complete");
        Check.Contains(resumed, "<goal>\nLong job\n</goal>");
        Check.Equal(0, IntOf(h, s.Id, "continuations"));
    }

    private static async Task FailedRun()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Fail(new HttpRequestException("connection refused"));
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Anything" });
        await SettledAsync(h, s.Id, "paused");
        Check.Contains((string?)GoalOf(h, s.Id)!["reason"], "The run failed: Model call failed: connection refused");
        Check.Equal(1, h.Runtime.GetBySession(s.Id)!.Runs);

        // a run that fails before its first turn never reaches the run hooks: the goal still pauses, with the error
        var gone = h.NewSession(model: "fake/missing");
        await h.Rpc.CallAsync("goal.set", new { sessionId = gone.Id, objective = "Anything" });
        await SettledAsync(h, gone.Id, "paused");
        Check.Contains((string?)GoalOf(h, gone.Id)!["reason"], "The run failed: Model 'fake/missing' is not available");

        // resume on an active goal whose agent is idle starts it again instead of failing
        h.SetModel(gone.Id, "fake/local");
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tool("goal_update", new { status = "complete", summary = "Done." });
        await h.Rpc.CallAsync("goal.resume", new { sessionId = gone.Id });
        await SettledAsync(h, gone.Id, "complete");
    }

    private static async Task Limits()
    {
        var (h, _) = await Start();
        await using var _h = h;
        h.AddTool(Work());
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("Progress made.") : Reply.Tool("work");

        h.Settings.Set("goal.maxContinuations", JsonValue.Create(2));
        var s = h.NewSession();
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Endless" });
        await SettledAsync(h, s.Id, "paused");
        Check.Contains((string?)GoalOf(h, s.Id)!["reason"], "Reached 2 automatic runs (goal.maxContinuations)");
        Check.Equal(3, h.Runtime.GetBySession(s.Id)!.Runs);

        // 110 tokens per model call, 2 calls per run: 220 after the first run, 440 after the second
        h.Settings.Set("goal.maxContinuations", JsonValue.Create(100));
        var b = h.NewSession();
        await h.Rpc.CallAsync("goal.set", new { sessionId = b.Id, objective = "Budgeted", tokenBudget = 300 });
        await SettledAsync(h, b.Id, "paused");
        Check.Contains((string?)GoalOf(h, b.Id)!["reason"], "Token budget reached: 440 of 300");
        Check.Equal(2, h.Runtime.GetBySession(b.Id)!.Runs);

        // goal.tokenBudget is the default for goals set without one
        h.Settings.Set("goal.tokenBudget", JsonValue.Create(5000));
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tool("goal_update", new { status = "complete", summary = "Done." });
        var d = h.NewSession();
        var set = await h.Rpc.CallAsync("goal.set", new { sessionId = d.Id, objective = "Default budget" });
        Check.Equal(5000L, long.Parse(set!["tokenBudget"]!.ToJsonString()));
        await SettledAsync(h, d.Id, "complete");
    }

    /// <summary>
    /// The token budget is what the run cost, not what the calls the goal's own hook happened to see cost: a call that
    /// another hook decided (here: nudge, on a textual tool call) ends the hook chain before a metering hook, so the
    /// goal must be metered where nothing can skip it.
    /// </summary>
    private static async Task NudgedCallsCounted()
    {
        var h = await TestHost.StartAsync();
        await using var _h = h;
        await h.StartPluginAsync(new GoalPlugin());
        await h.StartPluginAsync(new NudgePlugin());
        var step = 0;
        h.Catalog.Handler = (r, ct) => ++step switch
        {
            // the first answer is a tool call written as text: the nudge hook decides that call, and the run goes on
            1 => Reply.Text("<tool_call>\n<function=read>\n<parameter=path>a</parameter>\n</function>\n</tool_call>"),
            2 => Reply.Tool("goal_update", new { status = "complete", summary = "Done." }),
            _ => Reply.Text("All done."),
        };
        var s = h.NewSession();
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Meter every call", tokenBudget = 1000 });
        await SettledAsync(h, s.Id, "complete");

        Check.True(h.Messages(s.Id).Any(m => m.Role == MessageRole.Notice && m.MetaString("kind") == "nudge"), "the nudge hook decided the first call");
        var assistants = h.Messages(s.Id).Where(m => m.Role == MessageRole.Assistant && m.Usage is not null).ToList();
        Check.Equal(3, assistants.Count, "three model calls: the nudged one, the one that completed the goal, the answer");
        var spent = assistants.Sum(m => m.Usage!.InputTokens + m.Usage.CacheWriteTokens + m.Usage.OutputTokens);
        Check.Equal(3 * 110L, spent, "110 tokens per call");
        Check.Equal(spent, IntOf(h, s.Id, "tokensUsed"), "the goal is metered every call, not only the ones its hook saw");
    }

    private static async Task BlockedAndSetRefused()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Tool("goal_update", new { status = "blocked", summary = "I need the API key." }),
            2 => Reply.Tool("goal_set", new { objective = "Something else" }),
            _ => Reply.Text("I need the API key to go on."),
        };
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Call the API" });
        await SettledAsync(h, s.Id, "blocked");
        Check.Equal("I need the API key.", (string?)GoalOf(h, s.Id)!["reason"]);
        var refused = h.Messages(s.Id).SelectMany(m => m.ToolResults).Single(t => t.Name == "goal_set");
        Check.True(refused.IsError);
        Check.Contains(refused.Content, "A goal is already blocked");
        Check.Equal(1, h.Runtime.GetBySession(s.Id)!.Runs);
        Check.Equal("Call the API", (string?)GoalOf(h, s.Id)!["objective"]);
    }

    private static async Task PausedMidRun()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var inTool = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.AddTool(new FakeTool("slow", async (c, a, ct) =>
        {
            inTool.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolResult.Ok("slow done");
        }));
        var s = h.NewSession();
        var afterTool = "";
        h.Catalog.Handler = (r, ct) =>
        {
            if (!Reply.HasToolResult(r) && !r.Messages.Any(m => m.Role == MessageRole.Tool)) return Reply.Tool("slow");
            afterTool = string.Join("\n", Reply.Tail(r).Select(m => m.Text));
            return Reply.Text("Stopping here.");
        };
        await h.Rpc.CallAsync("goal.set", new { sessionId = s.Id, objective = "Slow work" });
        await inTool.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Rpc.CallAsync("goal.pause", new { sessionId = s.Id });
        release.TrySetResult();
        await SettledAsync(h, s.Id, "paused");
        Check.Contains(afterTool, "The goal is paused (Paused by the user). It no longer restarts you");
        Check.Equal(1, h.Runtime.GetBySession(s.Id)!.Runs);
    }

    private static async Task SetByModel()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Tool("goal_set", new { objective = "All tests pass" }),
            2 => Reply.Text("Starting on it."),
            3 => Reply.Tool("goal_update", new { status = "complete", summary = "All tests pass (ran them)." }),
            _ => Reply.Text("Done."),
        };
        await h.SendAsync(s.Id, "Keep going until all tests pass, make that your goal.");
        await SettledAsync(h, s.Id, "complete");
        Check.Equal(2, h.Runtime.GetBySession(s.Id)!.Runs);
        var notices = GoalNotices(h, s.Id);
        Check.Equal(1, notices.Count); // the tool result told the model; only the continuation is a notice
        Check.Contains(notices[0].Text, "automatic continuation 1");
    }

    private static async Task Notices()
    {
        var (h, ctx) = await Start();
        await using var _h = h;
        var goals = new Goals(ctx);
        var s = h.NewSession();
        // as if the goal had been announced earlier and the notice was then compacted away
        goals.Update(s.Id, _ => new NetPI.Goal.Goal { Objective = "Keep the docs current", ToldVersion = 1, ToldStatus = "active" });

        var turn = Turn(h, s, []);
        await goals.SyncNoticeAsync(turn);
        var n = GoalNotices(h, s.Id);
        Check.Equal(1, n.Count);
        Check.Contains(n[0].Text, "repeated because the earlier notices were compacted");
        await goals.SyncNoticeAsync(turn); // the notice is in the context now
        Check.Equal(1, GoalNotices(h, s.Id).Count);

        goals.Edit(s.Id, "Keep the docs and the tests current", null);
        await goals.SyncNoticeAsync(turn);
        Check.Contains(GoalNotices(h, s.Id)[^1].Text, "The user changed the goal");
        Check.Contains(GoalNotices(h, s.Id)[^1].Text, "Keep the docs and the tests current");

        goals.Pause(s.Id);
        await goals.SyncNoticeAsync(turn);
        Check.Contains(GoalNotices(h, s.Id)[^1].Text, "The goal is paused (Paused by the user)");
        var count = GoalNotices(h, s.Id).Count;
        await goals.SyncNoticeAsync(turn);
        Check.Equal(count, GoalNotices(h, s.Id).Count);

        goals.Clear(s.Id);
        await goals.SyncNoticeAsync(turn);
        Check.Equal(count, GoalNotices(h, s.Id).Count); // the model already knows it stopped
        Check.Equal("cleared", StatusOf(h, s.Id));
        Check.True(await h.Rpc.CallAsync("goal.get", new { sessionId = s.Id }) is null, "a cleared goal reads as none");
    }

    private static AgentTurnContext Turn(TestHost h, SessionInfo s, List<ChatMessage> start)
    {
        AgentTurnContext turn = null!;
        turn = new AgentTurnContext
        {
            Run = new AgentRunContext
            {
                Agent = new AgentInfo { Id = "agt_t", SessionId = s.Id },
                Session = s,
                Cwd = h.Workspace,
                Model = TestHost.LocalModel(),
                Services = h.Services,
                Sessions = h.Sessions,
                Models = h.Catalog,
                Events = h.Bus,
            },
            SystemPrompt = "",
            Messages = start,
            Tools = [],
            ReloadMessagesAsync = () =>
            {
                turn.Messages = h.Messages(s.Id);
                return Task.CompletedTask;
            },
        };
        return turn;
    }

    private static async Task RpcValidation()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var s = h.NewSession();
        async Task<string> Fails(string method, object args)
        {
            try { await h.Rpc.InvokeAsync(method, args); }
            catch (RpcException ex) { return ex.Message; }
            throw new AssertException($"{method} should fail");
        }
        Check.Contains(await Fails("goal.set", new { sessionId = s.Id, objective = "  " }), "The goal is empty");
        Check.Contains(await Fails("goal.set", new { sessionId = s.Id, objective = new string('x', 4001) }), "keep it under 4000");
        Check.Contains(await Fails("goal.pause", new { sessionId = s.Id }), "There is no goal");
        Check.Contains(await Fails("goal.resume", new { sessionId = s.Id }), "There is no paused goal");
        var sub = h.Sessions.CreateSession(new SessionInfo { Title = "sub", Kind = "subagent" });
        Check.Contains(await Fails("goal.set", new { sessionId = sub.Id, objective = "x" }), "not subagents");
        Check.True(await h.Rpc.CallAsync("goal.get", new { sessionId = s.Id }) is null, "no goal yet");
    }
}
