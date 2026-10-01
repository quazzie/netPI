using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

public static class AgentReworkTests
{
    public static void Register(TestRunner t)
    {
        t.Add("agent rework: historical fallback forks survive Runtime and Context replacement", HistoricalFallbackFork);
        t.Add("agent rework: reload keeps a physical lease until its real owner releases it", ReloadLease);
        t.Add("agent rework: cancellation and grants leave no waiters or physical leases", Cancellation);
        t.Add("agent rework: profile identity works without Context and survives its return", ProfilesWithoutContext);
        t.Add("agent rework: executor tools disappear and recover while stored goals require explicit resume", ExecutorAvailability);
        t.Add("agent rework: runtime shutdown timeout retains inference admission", RuntimeShutdown);
        t.Add("agent rework: identity changed during rendering takes effect before any request", () => PromptRevisionRace(false));
        t.Add("agent rework: Context renders the current numeric revision after an identity race", () => PromptRevisionRace(true));
        t.Add("agent rework: a reset fork cannot resurrect its old prefix after Context reload", ForkReset);
    }

    private static async Task HistoricalFallbackFork()
    {
        using var db = TestSqlite.TryCreate();
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Runtime, db: db);
        var origin = h.NewSession();
        h.Sessions.UpdateSession(origin.Id, s => { s.Meta = new JsonObject { [SessionIdentity.MetaKey] = "Identity A" }; });
        await h.SendAsync(origin.Id, "first"); await h.IdleAsync(origin.Id);
        var prefixA = h.Catalog.Requests.Last().SystemPrompt;
        var seqA = h.Messages(origin.Id).Last().Seq;
        // Context sees only the later portion of history; that must not override Runtime's historical prefix.
        await h.StartPluginAsync(new NetPI.Context.ContextPlugin());
        h.Sessions.UpdateSession(origin.Id, s => { s.Meta![SessionIdentity.MetaKey] = "Identity B"; SessionPrompt.Invalidate(s); });
        await h.SendAsync(origin.Id, "second"); await h.IdleAsync(origin.Id);
        var prefixB = h.Catalog.Requests.Last().SystemPrompt;
        var latest = h.Sessions.GetSession(origin.Id)!;
        SessionInfo Fork(long seq) => ((ISessionStore)h.Sessions).ForkSession(origin.Id, seq,
            NetPI.Host.Sessions.SessionFork.Template(latest, seq, 0));
        var early = Fork(seqA);
        var late = Fork(h.Messages(origin.Id).Last().Seq);
        var before = Fork(0);
        await h.StopPluginAsync("netpi.runtime");
        await h.StartPluginAsync(new RuntimePlugin());
        await h.StopPluginAsync("netpi.context");
        await h.SendAsync(early.Id, "continue early"); await h.IdleAsync(early.Id);
        Check.Equal(prefixA, h.Catalog.Requests.Last().SystemPrompt);
        Check.Equal(1, ((JsonArray)h.Sessions.GetSession(early.Id)!.Meta![SessionPrompt.HistoryKey]!).Count);
        await h.SendAsync(late.Id, "continue late"); await h.IdleAsync(late.Id);
        Check.Equal(prefixB, h.Catalog.Requests.Last().SystemPrompt);
        await h.SendAsync(before.Id, "new beginning"); await h.IdleAsync(before.Id);
        Check.Contains(h.Catalog.Requests.Last().SystemPrompt, "Identity B", "before a sent prefix, render current setup");
        await h.StartPluginAsync(new NetPI.Context.ContextPlugin());
        await h.SendAsync(early.Id, "Context returns"); await h.IdleAsync(early.Id);
        Check.Equal(prefixA, h.Catalog.Requests.Last().SystemPrompt);
        h.Sessions.UpdateSession(early.Id, s => { s.Meta![SessionIdentity.MetaKey] = "Identity C"; SessionPrompt.Invalidate(s); });
        await h.SendAsync(early.Id, "explicit reset"); await h.IdleAsync(early.Id);
        Check.Contains(h.Catalog.Requests.Last().SystemPrompt, "Identity C");
        Check.NotContains(h.Catalog.Requests.Last().SystemPrompt, "Identity A");
    }

    private static AgentSlotRequest Request(string id) => new() { Key = "fake/solo", AgentId = id, Provider = "fake" };
    private static async Task ReloadLease()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        var old = h.Scheduler!;
        using var held = await old.AcquireAsync(Request("old-holder"), CancellationToken.None);
        await h.StopPluginAsync("netpi.agents");
        Check.False(held.IsReleased, "stop cannot free an active inference slot");
        await h.StartPluginAsync(new AgentsPlugin());
        var next = h.Scheduler!;
        Check.Equal(1, next.Resources().Single(r => r.Model == "fake/solo").Busy);
        var waiting = next.AcquireAsync(Request("new-holder"), CancellationToken.None).AsTask();
        Check.False(waiting.IsCompleted);
        Check.Equal("model capacity", next.Snapshot().Single(p => p.Key == "fake/solo").Waiters.Single().WaitingFor);
        held.Dispose();
        using var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Equal(1, next.Resources().Single(r => r.Model == "fake/solo").Busy);
        held.Dispose();
        Check.Equal(1, next.Resources().Single(r => r.Model == "fake/solo").Busy, "double release is harmless across versions");
    }

    private static async Task Cancellation()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        var scheduler = h.Scheduler!;
        for (var round = 0; round < 100; round++)
        {
            using var held = await scheduler.AcquireAsync(Request("holder"), CancellationToken.None);
            using var cancel = new CancellationTokenSource();
            var waiting = scheduler.AcquireAsync(Request("waiter"), cancel.Token).AsTask();
            await Task.WhenAll(Task.Run(cancel.Cancel), Task.Run(held.Dispose));
            try { using var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            Check.Equal(0, scheduler.Snapshot().Sum(p => p.Busy + p.Queued));
            Check.Equal(0, h.Services.Get<IResourceLeases>()!.Snapshot().Count);
        }
    }

    private static async Task ProfilesWithoutContext()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("profiles.admin", new JsonObject
            { ["name"] = "Admin", ["prompt"] = "You are an administrator.", ["toolsOff"] = new JsonArray("write") }),
            plugins: TestHost.Plugins.Runtime);
        await h.StartPluginAsync(new NetPI.Profiles.ProfilesPlugin());
        h.AddTool(new FakeTool("write", (_, _, _) => Task.FromResult(ToolResult.Ok("ok"))));
        using var section = h.Services.Register<IPromptSection>(new Section());
        var session = h.NewSession();
        await h.Rpc.CallAsync("profiles.apply", new { sessionId = session.Id, profile = "admin" });
        await h.SendAsync(session.Id, "hello"); await h.IdleAsync(session.Id);
        var first = h.Catalog.Requests.Last();
        Check.Contains(first.SystemPrompt, "You are an administrator.");
        Check.Contains(first.SystemPrompt, "Independent guidance");
        Check.False(first.Tools.Any(t => t.Name == "write"));
        await h.StartPluginAsync(new NetPI.Context.ContextPlugin());
        await h.SendAsync(session.Id, "again"); await h.IdleAsync(session.Id);
        Check.Equal(first.SystemPrompt, h.Catalog.Requests.Last().SystemPrompt, "Context adopts the already sent fallback prefix");
        await h.StopPluginAsync("netpi.context");
        await h.Rpc.CallAsync("profiles.apply", new { sessionId = session.Id, profile = (string?)null });
        await h.SendAsync(session.Id, "default"); await h.IdleAsync(session.Id);
        Check.NotContains(h.Catalog.Requests.Last().SystemPrompt, "You are an administrator.");
        Check.True(h.Catalog.Requests.Last().Tools.Any(t => t.Name == "write"));
        Check.True(SessionPrompt.Revision(h.Sessions.GetSession(session.Id)!) >= 2);
    }

    private sealed class Section : IPromptSection
    {
        public string Id => "independent"; public int Order => 400;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct) => ValueTask.FromResult<string?>("Independent guidance");
    }

    private sealed class ChangingSection(Action change) : IPromptSection
    {
        private int _changed;
        public string Id => "identity"; public int Order => 0;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _changed, 1) != 0) return ValueTask.FromResult<string?>(null);
            change();
            return ValueTask.FromResult<string?>("Old identity");
        }
    }

    private static async Task PromptRevisionRace(bool context)
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Runtime | (context ? TestHost.Plugins.Context : TestHost.Plugins.None));
        var session = h.NewSession();
        using var section = h.Services.Register<IPromptSection>(new ChangingSection(() => h.Sessions.UpdateSession(session.Id, s =>
        {
            s.Meta ??= new JsonObject(); s.Meta[SessionIdentity.MetaKey] = "New identity"; SessionPrompt.Invalidate(s);
        })));
        await h.SendAsync(session.Id, "hello"); await h.IdleAsync(session.Id);
        Check.Equal(1, h.Catalog.Calls);
        Check.Contains(h.Catalog.Requests.Single().SystemPrompt, "New identity");
        Check.NotContains(h.Catalog.Requests.Single().SystemPrompt, "Old identity");
    }

    private static async Task ForkReset()
    {
        using var db = TestSqlite.TryCreate();
        if (db is null) throw new InvalidOperationException("SQLite is required for durable fork revision coverage");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Context | TestHost.Plugins.Runtime, db: db);
        h.Settings.SetQuiet("context.customPrompt", "Original identity");
        var origin = h.NewSession();
        await h.SendAsync(origin.Id, "hello"); await h.IdleAsync(origin.Id);
        var fork = ((ISessionStore)h.Sessions).ForkSession(origin.Id, h.Messages(origin.Id).Last().Seq, new SessionInfo
        {
            Title = "fork", Meta = new JsonObject { ["forkedFrom"] = new JsonObject { ["sessionId"] = origin.Id, ["seq"] = h.Messages(origin.Id).Last().Seq } },
        });
        h.Sessions.UpdateSession(fork.Id, s => { s.Meta![SessionIdentity.MetaKey] = "New fork identity"; SessionPrompt.Invalidate(s); });
        await h.StopPluginAsync("netpi.context");
        await h.StartPluginAsync(new NetPI.Context.ContextPlugin());
        await h.SendAsync(fork.Id, "continue"); await h.IdleAsync(fork.Id);
        Check.Contains(h.Catalog.Requests.Last().SystemPrompt, "New fork identity");
        Check.NotContains(h.Catalog.Requests.Last().SystemPrompt, "Original identity");
    }

    private static async Task RuntimeShutdown()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Runtime | TestHost.Plugins.Agents);
        await h.StartPluginAsync(new NetPI.Goal.GoalPlugin());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Handler = (_, _) => Reply.Text("done", async _ => { started.TrySetResult(); await finish.Task; });
        var session = h.NewSession("fake/solo");
        await h.Rpc.CallAsync("goal.set", new { sessionId = session.Id, objective = "Keep this goal" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await h.StopPluginAsync("netpi.runtime");
            var owner = h.Services.Get<IResourceLeases>()!.Snapshot().Single().Holder;
            Check.True(owner.Retiring);
            Check.True(owner.CancellationRequestedAt is not null);
            Check.True(owner.ProviderReturnedAt is null, "cancellation requested is not provider acknowledgement");
            Check.True(owner.LeaseId is not null && owner.ExecutorGeneration is not null);
            Check.Equal(h.Catalog.Requests.Last().CorrelationId, owner.CorrelationId);
            await h.StartPluginAsync(new RuntimePlugin());
            Check.Equal(owner.LeaseId, h.Services.Get<IResourceLeases>()!.Snapshot().Single().Holder.LeaseId,
                "retiring ownership survives replacement");
            var waiting = h.Scheduler!.AcquireAsync(Request("next"), CancellationToken.None).AsTask();
            Check.False(waiting.IsCompleted);
            finish.TrySetResult();
            using var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait.Until(() => h.Sessions.GetSession(session.Id)?.Meta?["goal"]?["status"]?.GetValue<string>() == "execution-unavailable", "removed executor saves the goal");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task ExecutorAvailability()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.AgentTools);
        await h.StartPluginAsync(new NetPI.Goal.GoalPlugin());
        Check.False(h.Tools.All.Any(t => t.Definition.Name == "agent_spawn"));
        var session = h.NewSession();
        var goal = await h.Rpc.CallAsync("goal.set", new { sessionId = session.Id, objective = "Saved work" });
        Check.Equal("execution-unavailable", goal!["status"]!.GetValue<string>());
        await h.StartPluginAsync(new NetPI.Runtime.RuntimePlugin());
        await h.Bus.DrainAsync();
        Check.True(h.Tools.All.Any(t => t.Definition.Name == "agent_spawn"));
        Check.Equal(0, h.Catalog.Calls, "capability return does not resume a goal");
        await h.StopPluginAsync("netpi.runtime"); await h.Bus.DrainAsync();
        Check.False(h.Tools.All.Any(t => t.Definition.Name == "agent_spawn"));
        Check.Equal("execution-unavailable", (await h.Rpc.CallAsync("goal.get", new { sessionId = session.Id }))!["status"]!.GetValue<string>());
    }
}
