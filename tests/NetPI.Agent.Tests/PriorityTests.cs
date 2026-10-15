using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NetPI.Runtime;

namespace NetPI.Agent.Tests;

/// <summary>
/// Run priorities (<see cref="RunPriority"/>): a chat's <c>meta.priority</c> orders the queue for a slot, and a critical run
/// takes the slot of a pausable run below it (a chat started for the user), between that run's model calls or once its
/// tools have run for <see cref="AgentRuntime.LendSlotAfter"/>. The user's own chat is never paused. All on a model with
/// one slot (fake/solo), so every run but one queues.
/// </summary>
public static class PriorityTests
{
    public static void Register(TestRunner t)
    {
        t.Add("priority: chats queue by their meta.priority; a subagent gets its parent's, never above it nor above high", QueueOrderAndChildren);
        t.Add("priority: changing a queued chat reorders it immediately", ReprioritizeQueued);
        t.Add("priority: a critical chat takes the slot of a pausable run at its next model call, and that run goes on after it", PausedAtNextCall);
        t.Add("priority: the user's own chat is never paused, and a high chat only queues first", NotPaused);
        t.Add("priority: a tool that has run past LendSlotAfter lends the slot to a critical run; it keeps running and its result reaches the model", LentWhileToolsRun);
    }

    /// <summary>
    /// The scripted model: "victim" asks for the slow tool first and answers once it has the result; every other chat
    /// answers at once. Each call is logged by its chat's prompt (":after" once a tool result came back), and a gate per
    /// prompt holds that chat's answers until it is opened.
    /// </summary>
    private sealed class Script
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> Gates { get; } = new();
        public TaskCompletionSource ToolGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeTool Slow { get; }

        public Script() => Slow = new FakeTool("slow", async (_, _, ct) =>
        {
            await ToolGate.Task.WaitAsync(ct);
            return ToolResult.Ok("the slow result");
        });

        public TaskCompletionSource Hold(string prompt) => Gates.GetOrAdd(prompt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public IAsyncEnumerable<ModelStreamEvent> Handle(ModelRequest r, CancellationToken ct)
        {
            var who = Reply.LastUser(r);
            var after = Reply.HasToolResult(r);
            Calls.Enqueue(after ? who + ":after" : who);
            if (who == "victim" && !after) return Reply.Tool("slow");
            Func<CancellationToken, Task>? gate = Gates.TryGetValue(who, out var g) ? c => g.Task.WaitAsync(c) : null;
            return Reply.Text(who + " done", gate);
        }

        public string Order => string.Join(",", Calls);
    }

    private static async Task<(TestHost Host, Script Script)> StartAsync()
    {
        var h = await TestHost.StartAsync();
        var script = new Script();
        h.Catalog.Handler = script.Handle;
        h.AddTool(script.Slow);
        return (h, script);
    }

    /// <summary>A chat on the one-slot model; <paramref name="priority"/> as a webhook, schedule or spawn stamps it (null: the user's own chat).</summary>
    private static SessionInfo Chat(TestHost h, string? priority, bool manuallyPrioritized = false)
    {
        var s = h.NewSession(model: "fake/solo");
        if (priority is not null || manuallyPrioritized)
            h.Sessions.UpdateSession(s.Id, x =>
            {
                x.Meta ??= new JsonObject();
                if (priority is not null) x.Meta[RunPriority.MetaKey] = priority;
                if (manuallyPrioritized) x.Meta["prioritySource"] = "manual";
            });
        return s;
    }

    private static async Task QueueOrderAndChildren()
    {
        var (h, script) = await StartAsync();
        await using var host = h;
        var first = Chat(h, null);
        script.Hold("first");
        await h.SendAsync(first.Id, "first");
        await Wait.Until(() => script.Order == "first", "the first chat holds the slot");

        var low = Chat(h, "low");
        var normal = Chat(h, "normal");
        var high = Chat(h, "high");
        await h.SendAsync(low.Id, "low");
        await h.SendAsync(normal.Id, "normal");
        await h.SendAsync(high.Id, "high");
        await Wait.Until(() => h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Queued == 3, "three queued");
        Check.Equal("100,0,-100", string.Join(",", h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Waiters.Select(w => w.Priority)),
            "the queue is in priority order, not arrival");
        script.Hold("first").SetResult();
        foreach (var s in new[] { first, low, normal, high }) await h.IdleAsync(s.Id);
        Check.Equal("first,high,normal,low", script.Order);

        // a subagent's level: its parent's by default, lower when it asks, never above its parent's nor above high
        Check.Equal("high", AgentRuntime.ChildPriority(null, Session("critical")), "a critical parent's subagents run at high");
        Check.Equal("normal", AgentRuntime.ChildPriority(null, Session(null)), "the user's chat: normal");
        Check.Equal("low", AgentRuntime.ChildPriority(RunPriority.Low, Session(null)), "lower when it asks");
        Check.Equal("normal", AgentRuntime.ChildPriority(RunPriority.High, Session(null)), "never above its parent");
        Check.Equal("low", AgentRuntime.ChildPriority(null, Session("low")));

        // spawned: the child's chat carries its level (which also makes its runs pausable)
        var parent = h.Runtime.GetBySession(high.Id)!;
        var child = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "child", ParentAgentId = parent.Id, Priority = RunPriority.Low });
        Check.Equal("low", h.Sessions.GetSession(child.SessionId)!.Meta?[RunPriority.MetaKey]?.GetValue<string>());
        await h.StatusAsync(child.Id, AgentStatus.Completed);
        await h.IdleAsync(high.Id);
    }

    private static SessionInfo Session(string? priority) => new()
    {
        Id = "ses_x", Meta = priority is null ? null : new JsonObject { [RunPriority.MetaKey] = priority },
    };

    private static async Task PausedAtNextCall()
    {
        var (h, script) = await StartAsync();
        await using var host = h;
        var victim = Chat(h, "normal");   // a webhook's chat
        await h.SendAsync(victim.Id, "victim");
        await Wait.Until(() => script.Slow.Calls == 1, "the victim's tool runs");

        var critical = Chat(h, "normal", manuallyPrioritized: true);
        script.Hold("critical");
        await h.SendAsync(critical.Id, "critical");
        await Wait.Until(() => h.Runtime.GetBySession(critical.Id)!.Status == AgentStatus.Queued, "the critical chat queues behind the tool");
        h.Sessions.UpdateSession(critical.Id, x => x.Meta![RunPriority.MetaKey] = "critical");
        await Wait.Until(() => h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Waiters.Single().Priority == RunPriority.Critical,
            "changing the queued chat updates its scheduler priority");
        Check.Equal("victim", script.Order, "the tool is not interrupted: it has not run for a minute");

        script.ToolGate.SetResult();
        await Wait.Until(() => script.Order == "victim,critical", "at its next model call the victim gives its slot up");
        await Wait.Until(() => h.Runtime.GetBySession(victim.Id) is { Status: AgentStatus.Queued, Activity: "paused for a critical run" },
            "the victim is paused, and says why");
        Check.Equal(RunPriority.Normal + RunPriority.Resume, h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Waiters.Single().Priority,
            "it queues first among its own level");

        script.Hold("critical").SetResult();
        await h.IdleAsync(critical.Id);
        await h.IdleAsync(victim.Id);
        Check.Equal("victim,critical,victim:after", script.Order, "the victim goes on after the critical chat");
        Check.True(h.Messages(victim.Id).SelectMany(m => m.ToolResults).Any(r => r.Content.Contains("the slow result")), "with its tool's result");
        Check.Contains(h.Messages(victim.Id).Last().Text, "victim done");
    }

    private static async Task ReprioritizeQueued()
    {
        var (h, script) = await StartAsync();
        await using var host = h;
        var holder = Chat(h, "low");
        await h.SendAsync(holder.Id, "victim");
        await Wait.Until(() => script.Slow.Calls == 1, "the low priority chat holds the slot");
        var normal = Chat(h, "normal");
        var low = Chat(h, "low");
        await h.SendAsync(normal.Id, "normal");
        await h.SendAsync(low.Id, "low");
        await Wait.Until(() => h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Queued == 2, "both chats queue");

        h.Sessions.UpdateSession(low.Id, x => x.Meta![RunPriority.MetaKey] = "high");
        await Wait.Until(() => h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Waiters is { Count: 2 } waiters
            && waiters[0].SessionId == low.Id && waiters[0].Priority == RunPriority.High,
            "the selected high priority moves ahead of the earlier normal waiter");

        script.ToolGate.SetResult();
        foreach (var s in new[] { holder, low, normal }) await h.IdleAsync(s.Id);
        Check.Equal("victim,victim:after,low,normal", script.Order, "the promoted queued chat runs first");
    }

    private static async Task NotPaused()
    {
        var (h, script) = await StartAsync();
        await using var host = h;
        var mine = Chat(h, "high", manuallyPrioritized: true);   // a manually prioritized chat keeps its slot
        await h.SendAsync(mine.Id, "victim");
        await Wait.Until(() => script.Slow.Calls == 1, "the user's tool runs");
        var critical = Chat(h, "critical");
        await h.SendAsync(critical.Id, "critical");
        await Wait.Until(() => h.Runtime.GetBySession(critical.Id)!.Status == AgentStatus.Queued, "the critical chat queues");
        script.ToolGate.SetResult();
        await h.IdleAsync(mine.Id);
        await h.IdleAsync(critical.Id);
        Check.Equal("victim,victim:after,critical", script.Order, "the user's chat finished its run first");

        // a high chat goes to the front of the queue, and does not pause a pausable run
        var (h2, script2) = await StartAsync();
        await using var host2 = h2;
        var webhook = Chat(h2, "low");
        await h2.SendAsync(webhook.Id, "victim");
        await Wait.Until(() => script2.Slow.Calls == 1, "the low chat's tool runs");
        var normal = Chat(h2, "normal");
        var high = Chat(h2, "high");
        await h2.SendAsync(normal.Id, "normal");
        await h2.SendAsync(high.Id, "high");
        await Wait.Until(() => h2.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Queued == 2, "both queue");
        script2.ToolGate.SetResult();
        foreach (var s in new[] { webhook, normal, high }) await h2.IdleAsync(s.Id);
        Check.Equal("victim,victim:after,high,normal", script2.Order);
    }

    private static async Task LentWhileToolsRun()
    {
        var (h, script) = await StartAsync();
        await using var host = h;
        ((AgentRuntime)h.Runtime).LendSlotAfter = TimeSpan.FromMilliseconds(200);
        var victim = Chat(h, "normal");
        await h.SendAsync(victim.Id, "victim");
        await Wait.Until(() => script.Slow.Calls == 1, "the victim's tool runs");

        var critical = Chat(h, "critical");
        script.Hold("critical");
        await h.SendAsync(critical.Id, "critical");
        await Wait.Until(() => script.Order == "victim,critical", "the critical chat runs while the victim's tool still does");
        var lent = h.Runtime.GetBySession(victim.Id)!;
        Check.Equal(AgentStatus.Running, lent.Status, "the victim still runs its tool");
        Check.Equal("slot lent to a critical run, tools still running", lent.Activity);
        Check.Equal(0, h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Queued, "and holds no place in the queue while its tool runs");

        // the tool ends while the critical chat holds the slot: the victim queues for one, first in its level
        script.ToolGate.SetResult();
        await Wait.Until(() => h.Runtime.GetBySession(victim.Id) is { Status: AgentStatus.Queued, Activity: "paused for a critical run" },
            "the victim waits for the slot with its tool's result");
        script.Hold("critical").SetResult();
        await h.IdleAsync(critical.Id);
        await h.IdleAsync(victim.Id);
        Check.Equal("victim,critical,victim:after", script.Order);
        Check.True(h.Messages(victim.Id).SelectMany(m => m.ToolResults).Any(r => r.Content.Contains("the slow result")), "the result reached the model as usual");

        // no critical run: a long tool keeps its slot
        var (h2, script2) = await StartAsync();
        await using var host2 = h2;
        ((AgentRuntime)h2.Runtime).LendSlotAfter = TimeSpan.FromMilliseconds(50);
        var alone = Chat(h2, "normal");
        await h2.SendAsync(alone.Id, "victim");
        await Wait.Until(() => script2.Slow.Calls == 1, "the tool runs");
        await Task.Delay(300);
        Check.Equal(1, h2.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Busy, "no one wants the slot: the run keeps it");
        script2.ToolGate.SetResult();
        await h2.IdleAsync(alone.Id);
        Check.Equal("victim,victim:after", script2.Order);
    }
}
