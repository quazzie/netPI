using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Plugins;
using NetPI.Runtime;

namespace NetPI.Agent.Tests;

/// <summary>
/// Waiting for the user (<see cref="UserWait"/>): a call that waits ends its run, holding nothing, and the chat goes on from
/// it when the user answers. A fake tool and a fake hook stand for ask_user and a guardrail; the plugins' own tests cover
/// what each of them does with it.
/// </summary>
public static class WaitTests
{
    public static void Register(TestRunner t)
    {
        t.Add("waits: a call that waits ends the run (no slot, no reload hold, yielded with its activity); the answer is its result and the chat goes on", AnswerGoesOn);
        t.Add("waits: the user writing instead ends the wait as steered and the message follows; a notice meanwhile waits for the answer", SteerAndNotice);
        t.Add("waits: stopping the chat ends the wait as cancelled; the agent is idle", StopCancels);
        t.Add("waits: a wait survives a reload of the runtime (its record), and the answer goes on in the new one", SurvivesRuntimeReload);
        t.Add("waits: a hook that asks before a call runs - the call does not run; allowed, it runs in the run that goes on, the hook seeing AllowedCalls", HookWaitAllowed);
        t.Add("waits: a batch with two waits goes on when both are answered; an answer while the batch still runs goes on at its end", TwoWaitsAndEarlyAnswer);
        t.Add("waits: a user's message while the waiting batch still runs ends the wait as steered when the batch stops", SteerWhileBatchRuns);
        t.Add("waits: a subagent cannot wait: its call gets an error result, and nothing is announced", SubagentRefused);
    }

    private static async Task<(TestHost Host, ReloadGate Gate)> StartAsync()
    {
        var gate = new ReloadGate();
        var h = await TestHost.StartAsync(h => h.Services.Register<IReloadGate>(gate));
        return (h, gate);
    }

    private static UserWait NewWait(string id, string activity = "waiting for your test") => new()
    {
        Id = id, Kind = "test", Activity = activity, Data = new JsonObject { ["question"] = "go on?" },
        IfSteered = ToolResult.Ok("No answer: the user wrote instead.", new JsonObject { ["status"] = "steered" }),
        IfCancelled = ToolResult.Ok("No answer: the chat was stopped.", new JsonObject { ["status"] = "cancelled" }),
    };

    /// <summary>A tool that waits for the user, with the wait id it is given in its arguments.</summary>
    private static FakeTool Confirm(bool readOnly = false) => new("confirm", (c, a, ct) =>
        Task.FromResult(ToolResult.WaitForUser(NewWait(a.GetProperty("id").GetString()!))), readOnly);

    private static ToolResultPart? ResultOf(TestHost h, string sessionId, string callId) =>
        h.Messages(sessionId).SelectMany(m => m.ToolResults).LastOrDefault(r => r.CallId == callId);

    private static async Task<AgentInfo> WaitingAsync(TestHost h, string sessionId, int count = 1)
    {
        await Wait.Until(() => h.Runtime.GetBySession(sessionId) is { Status: AgentStatus.Yielded } && h.Runtime.Waits(sessionId).Count >= count
                               && h.Runtime.Waits(sessionId).Count(w => !w.Answered) >= 1,
            "the chat waits for the user");
        return h.Runtime.GetBySession(sessionId)!;
    }

    private static async Task AnswerGoesOn()
    {
        var (h, gate) = await StartAsync();
        await using var host = h;
        h.AddTool(Confirm());
        var s = h.NewSession(model: "fake/solo");   // one slot: the wait must give it back
        var call = Reply.Call("confirm", new { id = "w_answer" });
        var requests = new List<ModelRequest>();
        h.Catalog.Handler = (r, ct) =>
        {
            lock (requests) requests.Add(r);
            return Reply.HasToolResult(r) ? Reply.Text("went on") : Reply.Tools(call);
        };
        await h.SendAsync(s.Id, "do it");
        var a = await WaitingAsync(h, s.Id);
        Check.Equal("waiting for your test", a.Activity);
        await Wait.Until(() => gate.Count == 0, "no reload hold while it waits");
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "and no slot");
        var w = h.Runtime.Waits(s.Id).Single();
        Check.Equal("w_answer", w.Id);
        Check.Equal(call.Id, w.CallId);
        Check.Equal("confirm", w.Tool);
        Check.Equal("go on?", (string?)w.Data!["question"]);
        Check.True(ResultOf(h, s.Id, call.Id) is null, "the call has no result while it waits");
        var announced = FakeBus.Data(h.Bus.OfType(EventTypes.AgentWaiting).Single());
        Check.Equal("w_answer", (string?)announced["wait"]!["id"]);

        Check.True(await h.Runtime.ResolveAsync("w_answer", WaitResolution.Answer(ToolResult.Ok("yes, go on", new JsonObject { ["status"] = "answered" }))));
        var done = await h.IdleAsync(s.Id);
        Check.Equal(AgentStatus.Idle, done.Status);
        Check.Equal("yes, go on", ResultOf(h, s.Id, call.Id)!.Content, "the answer is the call's result");
        Check.Equal("answered", (string?)ResultOf(h, s.Id, call.Id)!.Details!["status"]);
        Check.Equal("went on", h.Messages(s.Id)[^1].Text);
        lock (requests) Check.Equal("yes, go on", requests[^1].Messages.Last(m => m.Role == MessageRole.Tool).ToolResults.Single().Content, "the model read the answer");
        var ended = FakeBus.Data(h.Bus.OfType(EventTypes.AgentWaitEnded).Single());
        Check.Equal("answered", (string?)ended["reason"]);
        Check.Equal(0, h.Runtime.Waits(s.Id).Count);
        Check.False(await h.Runtime.ResolveAsync("w_answer", WaitResolution.Answer(ToolResult.Ok("again"))), "answered already");
        await Wait.Until(() => gate.Count == 0, "the run that went on released its hold");
    }

    private static async Task SteerAndNotice()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        h.AddTool(Confirm());
        var s = h.NewSession();
        var call = Reply.Call("confirm", new { id = "w_steer" });
        h.Catalog.Handler = (r, ct) => Reply.LastUser(r) == "never mind" ? Reply.Text("ok then") : Reply.HasToolResult(r) ? Reply.Text("??") : Reply.Tools(call);
        await h.SendAsync(s.Id, "do it");
        await WaitingAsync(h, s.Id);
        // something for the chat that is not the user: it waits for the answer too
        await h.Runtime.SendAsync(s.Id, new UserInput { Text = "a report arrived", AsNotice = true, NoticeKind = "agent-result", Source = "system" });
        Check.Equal(AgentStatus.Yielded, h.Runtime.GetBySession(s.Id)!.Status, "a notice does not end the wait");
        Check.Equal(1, h.Runtime.GetQueue(s.Id).Count, "it waits in the queue");
        Check.True(ResultOf(h, s.Id, call.Id) is null);

        await h.SendAsync(s.Id, "never mind");
        await h.IdleAsync(s.Id);
        var msgs = h.Messages(s.Id);
        var result = ResultOf(h, s.Id, call.Id)!;
        Check.Equal("No answer: the user wrote instead.", result.Content, "the owner's result for a steer");
        Check.Equal("steered", (string?)result.Details!["status"]);
        var at = msgs.FindIndex(m => m.ToolResults.Any(r => r.CallId == call.Id));
        Check.True(at >= 0 && msgs.FindIndex(m => m.Text.Contains("a report arrived")) > at, "the notice comes after the call's result");
        Check.True(msgs.FindIndex(m => m.Role == MessageRole.User && m.Text == "never mind") > at, "and the user's message after it");
        Check.Equal("ok then", msgs[^1].Text);
        Check.Equal("steered", (string?)FakeBus.Data(h.Bus.OfType(EventTypes.AgentWaitEnded).Single())["reason"]);
        Check.False(await h.Runtime.ResolveAsync("w_steer", WaitResolution.Answer(ToolResult.Ok("late"))), "nothing waits any more");
    }

    private static async Task StopCancels()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        h.AddTool(Confirm());
        var s = h.NewSession();
        var call = Reply.Call("confirm", new { id = "w_stop" });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("??") : Reply.Tools(call);
        await h.SendAsync(s.Id, "do it");
        await WaitingAsync(h, s.Id);
        Check.True(await h.Runtime.AbortAsync(s.Id), "stopping a chat that waits stops something");
        var a = h.Runtime.GetBySession(s.Id)!;
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Equal("No answer: the chat was stopped.", ResultOf(h, s.Id, call.Id)!.Content);
        Check.Equal(0, h.Runtime.Waits(s.Id).Count);
        Check.Equal("cancelled", (string?)FakeBus.Data(h.Bus.OfType(EventTypes.AgentWaitEnded).Single())["reason"]);
        Check.Equal(1, h.Messages(s.Id).Count(m => m.Role == MessageRole.Assistant), "no model call after the stop");
    }

    private static async Task SurvivesRuntimeReload()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        h.AddTool(Confirm());
        var s = h.NewSession();
        var call = Reply.Call("confirm", new { id = "w_reload" });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("went on after the reload") : Reply.Tools(call);
        await h.SendAsync(s.Id, "do it");
        await WaitingAsync(h, s.Id);

        // the runtime is swapped (what a publish does once no run is active): nothing runs, the wait is in the record
        await h.StopPluginAsync("netpi.runtime");
        await h.StartPluginAsync(new RuntimePlugin());
        var a = h.Runtime.GetBySession(s.Id)!;
        Check.Equal(AgentStatus.Yielded, a.Status, "still waiting, not failed as interrupted");
        Check.Equal("waiting for your test", a.Activity);
        Check.True(a.Error is null, a.Error ?? "");
        Check.Equal("w_reload", h.Runtime.Waits(s.Id).Single().Id);
        Check.True(await h.Runtime.ResolveAsync("w_reload", WaitResolution.Answer(ToolResult.Ok("answered after the reload"))));
        await h.IdleAsync(s.Id);
        Check.Equal("answered after the reload", ResultOf(h, s.Id, call.Id)!.Content);
        Check.Equal("went on after the reload", h.Messages(s.Id)[^1].Text);
    }

    /// <summary>Asks before every call of <c>act</c>, unless the user allowed that call (what a guardrail's ask rule does).</summary>
    private sealed class AskFirst : IAgentHook
    {
        public int Asked;
        public int Order => 10_000;
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call)
        {
            if (call.Name != "act") return ValueTask.FromResult<ToolCallDecision?>(null);
            if (turn.AllowedCalls.TryGetValue(call.Id, out var allowed) && allowed.Kind == "test") return ValueTask.FromResult<ToolCallDecision?>(null);
            Interlocked.Increment(ref Asked);
            return ValueTask.FromResult<ToolCallDecision?>(new ToolCallDecision { Wait = NewWait("w_hook_" + call.Id, "waiting for your OK") });
        }
    }

    private static async Task HookWaitAllowed()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        var hook = new AskFirst();
        h.Services.Register<IAgentHook>(hook);
        var act = new FakeTool("act", (c, a, ct) => Task.FromResult(ToolResult.Ok("acted")));
        h.AddTool(act);
        var s = h.NewSession();
        var first = Reply.Call("act", new { n = 1 });
        var after = Reply.Call("act", new { n = 2 });   // a call after the one that waits: it runs once the batch goes on
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("both acted") : Reply.Tools(first, after);
        await h.SendAsync(s.Id, "act twice");
        var a = await WaitingAsync(h, s.Id);
        Check.Equal("waiting for your OK", a.Activity);
        Check.Equal(0, act.Calls, "the call that waits did not run, nor the one after it");
        Check.Equal(1, Volatile.Read(ref hook.Asked), "the batch stopped at the first wait");
        Check.Equal(first.Id, h.Runtime.Waits(s.Id).Single().CallId);

        Check.True(await h.Runtime.ResolveAsync("w_hook_" + first.Id, WaitResolution.Allow()));
        // the call after it asks too: the batch stops again at that one
        await Wait.Until(() => h.Runtime.Waits(s.Id).Any(w => w.CallId == after.Id), "the second call asks in its turn");
        Check.Equal(1, act.Calls, "the allowed call ran in the run that went on");
        Check.Equal("acted", ResultOf(h, s.Id, first.Id)!.Content);
        Check.True(await h.Runtime.ResolveAsync("w_hook_" + after.Id, WaitResolution.Allow()));
        await h.IdleAsync(s.Id);
        Check.Equal(2, act.Calls);
        Check.Equal("both acted", h.Messages(s.Id)[^1].Text);
    }

    private static async Task TwoWaitsAndEarlyAnswer()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        var release = new TaskCompletionSource();
        h.AddTool(Confirm(readOnly: true));
        h.AddTool(new FakeTool("slow", async (c, a, ct) => { await release.Task.WaitAsync(ct); return ToolResult.Ok("slow done"); }, readOnly: true));
        var s = h.NewSession();
        var one = Reply.Call("confirm", new { id = "w_one" });
        var two = Reply.Call("confirm", new { id = "w_two" });
        var turns = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (Reply.HasToolResult(r)) return Reply.Text("all answered");
            return Interlocked.Increment(ref turns) == 1 ? Reply.Tools(one, two) : Reply.Tools(Reply.Call("confirm", new { id = "w_early" }), Reply.Call("slow"));
        };
        // two waits in one (read-only, parallel) batch: the chat goes on when both are answered
        await h.SendAsync(s.Id, "two questions");
        await WaitingAsync(h, s.Id, 2);
        Check.True(await h.Runtime.ResolveAsync("w_one", WaitResolution.Answer(ToolResult.Ok("one"))));
        var a = h.Runtime.GetBySession(s.Id)!;
        Check.Equal(AgentStatus.Yielded, a.Status, "one answer of two: it still waits");
        Check.True(h.Runtime.Waits(s.Id).Single(w => w.Id == "w_one").Answered);
        Check.True(await h.Runtime.ResolveAsync("w_two", WaitResolution.Answer(ToolResult.Ok("two"))));
        await h.IdleAsync(s.Id);
        Check.Equal("one", ResultOf(h, s.Id, one.Id)!.Content);
        Check.Equal("two", ResultOf(h, s.Id, two.Id)!.Content);

        // answered while the batch still runs (another call of it is slow): the batch goes on the moment it stops
        await h.SendAsync(s.Id, "again");
        await Wait.Until(() => h.Runtime.Waits(s.Id).Any(w => w.Id == "w_early"), "the wait is answerable while the batch runs");
        Check.Equal(AgentStatus.Running, h.Runtime.GetBySession(s.Id)!.Status, "the batch is still running");
        Check.True(await h.Runtime.ResolveAsync("w_early", WaitResolution.Answer(ToolResult.Ok("early"))));
        release.SetResult();
        await h.IdleAsync(s.Id);
        var early = h.Messages(s.Id).SelectMany(m => m.ToolResults).Last(r => r.Name == "confirm");
        Check.Equal("early", early.Content);
        Check.Equal("all answered", h.Messages(s.Id)[^1].Text);
    }

    private static async Task SteerWhileBatchRuns()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        var release = new TaskCompletionSource();
        h.AddTool(Confirm(readOnly: true));
        h.AddTool(new FakeTool("slow", async (c, a, ct) => { await release.Task.WaitAsync(ct); return ToolResult.Ok("slow done"); }, readOnly: true));
        var s = h.NewSession();
        var call = Reply.Call("confirm", new { id = "w_running" });
        h.Catalog.Handler = (r, ct) => Reply.LastUser(r) == "skip it" ? Reply.Text("skipped") : Reply.Tools(call, Reply.Call("slow"));
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Runtime.Waits(s.Id).Count == 1, "registered while the batch runs");
        await h.SendAsync(s.Id, "skip it");   // a steer into the running batch
        release.SetResult();
        await h.IdleAsync(s.Id);
        Check.Equal("No answer: the user wrote instead.", ResultOf(h, s.Id, call.Id)!.Content);
        Check.Equal("skipped", h.Messages(s.Id)[^1].Text);
        Check.Equal(0, h.Runtime.Waits(s.Id).Count);
    }

    private static async Task SubagentRefused()
    {
        var (h, _) = await StartAsync();
        await using var host = h;
        h.AddTool(Confirm());
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("REPORT: could not wait") : Reply.Tool("confirm", new { id = "w_sub" });
        var sub = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "ask the user" });
        await h.StatusAsync(sub.Id, AgentStatus.Completed);
        var result = h.Messages(sub.SessionId).SelectMany(m => m.ToolResults).Single(r => r.Name == "confirm");
        Check.True(result.IsError);
        Check.Contains(result.Content, "subagent");
        Check.Equal(0, h.Runtime.Waits().Count, "nothing waits");
        Check.Equal(0, h.Bus.OfType(EventTypes.AgentWaiting).Count, "and nothing was announced");
    }
}
