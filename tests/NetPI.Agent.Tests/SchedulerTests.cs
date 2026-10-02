using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

public static class SchedulerTests
{
    public static void Register(TestRunner t)
    {
        t.Add("scheduler: a model with 2 slots → the third run queues", ThirdQueues);
        t.Add("agents: listed always with their state (instances, switched off, model not loaded); other model calls while busy", AgentsListed);
        t.Add("agents: agents on one local model share its slots; the choice for a chat; switched off refuses at once", SharedModelSlots);
        t.Add("agents: a removed agent takes no new work, and its running calls still count on the shared model", AgentRemoved);
        t.Add("agents: chats run on their agent (taken, agents.use, agents.setEnabled); an inactive agent stops the chat at once", ChatsOnAgents);
        t.Add("agents: the lanes of earlier versions become agents, once", Upgrade);
        t.Add("scheduler: priority, FIFO, cancellation, idempotent release", PriorityAndCancel);
        t.Add("agents: more instances wake waiters; instances follow the catalog", CapacityIncrease);
        t.Add("scheduler: agents.changed is debounced, and only sent on changes", Debounce);
        t.Add("scheduler: the wait queue has a cap and a longest wait (agents.queueMax/queueTimeoutSeconds)", QueueCapAndTimeout);
        t.Add("scheduler: by default a waiting run stays queued (no queue timeout)", QueueHasNoTimeoutByDefault);
        t.Add("scheduler: stop fails waiters; runs survive a reload of the agents plugin", ReloadDuringWait);
        t.Add("scheduler: budget exceeded + usage.summary", Budget);
        t.Add("scheduler: runs without the agents plugin", NoAgentsPlugin);
    }

    private static AgentSlotRequest Req(string pool, string agent, int priority = 0) => new() { Key = pool, AgentId = agent, Priority = priority, Label = agent };

    private static async Task ThirdQueues()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("x", c => gate.Task.WaitAsync(c));
        var sessions = Enumerable.Range(0, 3).Select(i => h.NewSession(title: "s" + i)).ToList();
        foreach (var s in sessions) await h.SendAsync(s.Id, "go");
        await Wait.Until(() =>
        {
            var st = sessions.Select(s => h.Runtime.GetBySession(s.Id)!.Status).ToList();
            return h.Catalog.Calls == 2 && st.Count(x => x == AgentStatus.Running) == 2 && st.Count(x => x == AgentStatus.Queued) == 1;
        }, "two calls entered the held provider, one queued");
        var queued = sessions.Select(s => h.Runtime.GetBySession(s.Id)!).Single(a => a.Status == AgentStatus.Queued);
        Check.Equal("waiting for a slot on fake/local", queued.Activity);
        Check.Equal(2, h.Catalog.Calls, "queued agent has not called the model");
        var pool = h.Scheduler!.Snapshot().Single(p => p.Key == "fake/local");
        Check.Equal(2, pool.Capacity);
        Check.Equal(2, pool.Busy);
        Check.Equal(1, pool.Queued);
        Check.Equal(queued.Id, pool.Waiters.Single().AgentId);
        Check.Equal("catalog", pool.Source);
        Check.Equal("queued", pool.Status);
        var rpc = (JsonArray)(await h.Rpc.CallAsync("agents.list"))!;
        Check.Equal(2, rpc.Single(p => (string?)p!["key"] == "fake/local")!["busy"]!.GetValue<int>());

        gate.SetResult();
        foreach (var s in sessions) await h.IdleAsync(s.Id);
        Check.Equal(3, h.Catalog.Calls);
        await Task.Delay(250);
        await h.Bus.DrainAsync();
        var last = FakeBus.Data(h.Bus.OfType(AgentSchedulerEvents.Changed).Last());
        Check.Equal(0, ((JsonArray)last["agents"]!).Count, "no agents, nothing running");
    }

    private static JsonNode J(string json) => JsonNode.Parse(json)!;

    private static async Task AgentsListed()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.coder", J("""{ "model": "fake/local" }"""));
            x.Settings.SetQuiet("agents.solo", J("""{ "model": "fake/solo", "instances": 3, "use": "One at a time." }"""));
            x.Settings.SetQuiet("agents.big", J("""{ "model": "cloud/big" }"""));
            x.Settings.SetQuiet("agents.off", J("""{ "model": "cloud/big", "disabled": true }"""));
            x.Settings.SetQuiet("agents.gone", J("""{ "model": "fake/nope" }"""));
            x.Settings.SetQuiet("agents.maxDepth", 3);
        });
        var pools = h.Scheduler!.Snapshot();
        Check.Equal("big|coder|gone|off|solo", string.Join("|", pools.Select(p => p.Key)), "the agents, always; nothing else while idle");
        var by = pools.ToDictionary(p => p.Key);
        Check.True(pools.All(p => p.Configured && p.Source == "settings"));
        Check.Equal(2, by["coder"].Capacity, "instances: the local model's slots");
        Check.Equal("fake/local", by["coder"].Model);
        Check.Equal(1, by["big"].Capacity, "instances: 1 on a cloud model");
        Check.Equal(3, by["solo"].Capacity);
        Check.Equal("One at a time.", by["solo"].Use);
        Check.True(by["coder"].Available && by["big"].Available);
        Check.Equal("idle", by["coder"].Status);
        Check.False(by["off"].Available);
        Check.True(by["off"].Disabled);
        Check.Equal("disabled", by["off"].Status);
        Check.False(by["gone"].Available);
        Check.Contains(by["gone"].Unavailable, "not in the model list");

        // a local agent is active only while its model is loaded
        var local = h.Catalog.Cached.Single(m => m.Ref == "fake/local");
        local.Status = "unloaded";
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        var coder = h.Scheduler.Snapshot().Single(p => p.Key == "coder");
        Check.False(coder.Available);
        Check.Equal("unavailable", coder.Status);
        Check.Equal("local isn't loaded", coder.Unavailable);
        local.Status = "loaded";
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        Check.True(h.Scheduler.Snapshot().Single(p => p.Key == "coder").Available);

        // other model calls show while they run
        var lease = await h.Scheduler.AcquireAsync(Req(h.Scheduler.Resolve(TestHost.SoloModel()), "summarizer"), CancellationToken.None);
        var busy = h.Scheduler.Snapshot().Single(p => p.Key == "fake/solo");
        Check.False(busy.Configured);
        Check.Equal(1, busy.Busy);
        lease.Dispose();
        Check.False(h.Scheduler.Snapshot().Any(p => p.Key == "fake/solo"));
    }

    private static async Task SharedModelSlots()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.a", J("""{ "model": "fake/local", "instances": 2 }"""));
            x.Settings.SetQuiet("agents.b", J("""{ "model": "fake/local", "instances": 2 }"""));
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        Check.Equal("a", s.Resolve(TestHost.LocalModel(), "a"));
        Check.Equal("b", s.Resolve(TestHost.LocalModel(), "b"));
        Check.Equal("fake/local", s.Resolve(TestHost.LocalModel(), "nope"), "an unknown agent: a slot per model");
        var a1 = await s.AcquireAsync(Req("a", "A1"), CancellationToken.None);
        var b1 = await s.AcquireAsync(Req("b", "B1"), CancellationToken.None);
        // the model serves two at once: a third run waits although "a" has an instance free
        Check.False(s.TryAcquire(Req("a", "A2"), out _), "the model's slots are taken");
        var a2 = s.AcquireAsync(Req("a", "A2"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.False(a2.IsCompleted);
        Check.Equal("queued", s.Snapshot().Single(p => p.Key == "a").Status);
        b1.Dispose();
        var la2 = await a2.WaitAsync(TimeSpan.FromSeconds(2));

        // the scheduler's choice for a chat: its own agent on the model, else the least busy agent on the model
        Check.Equal("a", s.ChooseAgent(TestHost.LocalModel(), "a"));
        Check.Equal("b", s.ChooseAgent(TestHost.LocalModel(), null), "a is full");
        Check.Equal("b", s.ChooseAgent(TestHost.LocalModel(), "elsewhere"));
        try { s.ChooseAgent(TestHost.SoloModel(), null); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException ex) { Check.Contains(ex.Message, "No agent runs fake/solo."); }

        // switched off: new runs are refused at once, waiters are told
        var b2 = s.AcquireAsync(Req("b", "B2"), CancellationToken.None).AsTask();
        h.Settings.Set("agents.b.disabled", JsonValue.Create(true));
        await h.Bus.DrainAsync();
        try { await b2.WaitAsync(TimeSpan.FromSeconds(2)); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException ex) { Check.Contains(ex.Message, "The agent \"b\" is disabled."); }
        try { await s.AcquireAsync(Req("b", "B3"), CancellationToken.None); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException) { }
        Check.False(s.TryAcquire(Req("b", "B4"), out _));
        Check.Equal("a", s.ChooseAgent(TestHost.LocalModel(), null), "an active agent first");
        a1.Dispose();
        la2.Dispose();
        Check.Equal(0, s.Snapshot().Sum(p => p.Busy + p.Queued));
    }

    private static async Task AgentRemoved()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.a", J("""{ "model": "fake/local", "instances": 1 }"""));
            x.Settings.SetQuiet("agents.b", J("""{ "model": "fake/local", "instances": 1 }"""));
            x.Settings.SetQuiet("agents.c", J("""{ "model": "fake/local", "instances": 1 }"""));
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        Check.Equal("a", s.Resolve(TestHost.LocalModel(), "a"));

        // "a" runs one call, and a second one waits for its single instance
        var a1 = await s.AcquireAsync(Req("a", "A1"), CancellationToken.None);
        var a2 = s.AcquireAsync(Req("a", "A2"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.False(a2.IsCompleted, "the second run waits for the instance");

        // the agent is removed while its call is running and a waiter is queued for it
        h.Settings.Set("agents.a", null);
        await h.Bus.DrainAsync();
        var pool = s.Snapshot().Single(p => p.Key == "a");
        Check.Equal(1, pool.Busy, "the running call is still counted");
        Check.Equal(0, pool.Queued, "the waiter was told, not left in the queue");
        Check.False(pool.Configured);
        Check.False(pool.Available);
        Check.Equal("removed", pool.Unavailable);
        Check.Equal("retiring", pool.Status);
        Check.Equal("fake/local", pool.Model, "the model stays while the run finishes");
        try { await a2.WaitAsync(TimeSpan.FromSeconds(2)); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException ex) { Check.Contains(ex.Message, "The agent \"a\" was removed"); }
        try { await s.AcquireAsync(Req("a", "A3"), CancellationToken.None); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException) { }
        Check.False(s.TryAcquire(Req("a", "A4"), out _), "no new work on a removed agent");

        // The model serves two at once, and the removed agent's call is one of them: "b" has a free instance, "c"
        // does not get a third concurrent call on a two-slot model.
        var b1 = await s.AcquireAsync(Req("b", "B1"), CancellationToken.None);
        Check.False(s.TryAcquire(Req("c", "C1"), out _), "the removed agent's running call still counts on the model");

        // the call finishes, and the retired pool leaves with it
        a1.Dispose();
        b1.Dispose();
        await Wait.Until(() => s.Snapshot().All(p => p.Key != "a"), "the retired pool is gone once its last owner released");
        Check.False(s.Snapshot().Single(p => p.Key == "b").Available is false, "the other agents are untouched");
    }

    private static async Task ChatsOnAgents()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.a", J("""{ "model": "fake/local", "instances": 1 }"""));
            x.Settings.SetQuiet("agents.b", J("""{ "model": "fake/local", "instances": 1 }"""));
            x.Settings.SetQuiet("agents.solo", J("""{ "model": "fake/solo" }"""));
        });
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("x", c => gate.Task.WaitAsync(c));
        // chats without an agent take a free agent on their model, and keep it
        var s1 = h.NewSession();
        var s2 = h.NewSession();
        await h.SendAsync(s1.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first running");
        await h.SendAsync(s2.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 2, "the second chat runs on the other agent");
        Check.Equal("a", SessionAgent.Of(h.Sessions.GetSession(s1.Id)));
        Check.Equal("b", SessionAgent.Of(h.Sessions.GetSession(s2.Id)));
        Check.Equal("a", h.Runtime.GetBySession(s1.Id)!.Agent);
        gate.SetResult();
        await h.IdleAsync(s1.Id);
        await h.IdleAsync(s2.Id);

        // agents.use: the chat runs on the agent and its model
        var s3 = h.NewSession();
        var used = (await h.Rpc.CallAsync("agents.use", new { sessionId = s3.Id, agent = "solo" }))!;
        Check.Equal("fake/solo", (string?)used["model"]);
        Check.Equal("solo", SessionAgent.Of(h.Sessions.GetSession(s3.Id)));
        await h.SendAsync(s3.Id, "go");
        Check.Equal("solo", (await h.IdleAsync(s3.Id)).Agent);
        Check.Equal(3, h.Catalog.Calls);

        // switched off (agents.setEnabled): the chat stops at once with a notice
        await h.Rpc.CallAsync("agents.setEnabled", new { id = "solo", enabled = false });
        Check.True(h.Settings.Get<bool>("agents.solo.disabled"));
        await h.SendAsync(s3.Id, "again");
        var off = await h.IdleAsync(s3.Id);
        Check.Contains(off.Error, "The agent \"solo\" is disabled.");
        Check.Equal("error", h.Messages(s3.Id)[^1].MetaString("kind"));
        Check.Equal(3, h.Catalog.Calls, "no model call");

        // on again, but its model isn't loaded
        var listed = (JsonArray)(await h.Rpc.CallAsync("agents.setEnabled", new { id = "solo", enabled = true }))!;
        Check.True(listed.Single(p => (string?)p!["key"] == "solo")!["available"]!.GetValue<bool>());
        h.Catalog.Cached.Single(m => m.Ref == "fake/solo").Status = "unloaded";
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        await h.SendAsync(s3.Id, "and again");
        Check.Contains((await h.IdleAsync(s3.Id)).Error, "solo isn't loaded. Load its model (AiSwitcher) or choose another agent.");
        Check.Equal(3, h.Catalog.Calls);

        // a model no agent runs
        var s4 = h.NewSession(model: "cloud/big");
        await h.SendAsync(s4.Id, "hi");
        Check.Contains((await h.IdleAsync(s4.Id)).Error, "No agent runs cloud/big.");

        // an unknown agent is refused
        try { await h.Rpc.CallAsync("agents.use", new { sessionId = s4.Id, agent = "nope" }); throw new AssertException("expected not_found"); }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
    }

    private static async Task Upgrade()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("defaultModel", "fake/solo");
            x.Settings.SetQuiet("lanes.pools", new JsonObject());
            x.Settings.SetQuiet("models.cloudSlots", 4);
            x.Settings.SetQuiet("lanes.Fast One", J("""{ "model": "fake/local", "capacity": 2, "use": "Quick.", "budget": { "limitUsd": 1 } }"""));
            x.Settings.SetQuiet("agents.maxDepth", 2);
        }, plugins: TestHost.Plugins.Agents);
        var agents = (JsonObject)h.Settings.GetNode("agents")!;
        Check.Equal("fast-one,maxDepth,solo", string.Join(",", agents.Select(kv => kv.Key).Order(StringComparer.Ordinal)));
        Check.Equal("""{"model":"fake/local","instances":2,"use":"Quick.","budget":{"limitUsd":1}}""", agents["fast-one"]!.ToJsonString());
        Check.Equal("""{"model":"fake/solo"}""", agents["solo"]!.ToJsonString(), "an agent for the default model");
        Check.Equal(null, h.Settings.GetNode("lanes"), "the lanes are gone");
        Check.Equal(4, h.Settings.Get<int>("models.cloudSlots"), "lanes.cloudDefaultCapacity moved");
        Check.Equal("fast-one|solo", string.Join("|", h.Scheduler!.Snapshot().Select(p => p.Key)));
        Check.Equal(0, AgentUpgrade.Run(h.Settings).Count, "nothing to do the next time");
    }

    private static async Task PriorityAndCancel()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var pool = s.Resolve(TestHost.SoloModel()); // capacity 1
        var a = await s.AcquireAsync(Req(pool, "A"), CancellationToken.None);
        Check.False(s.TryAcquire(Req(pool, "X"), out _), "full");
        var b = s.AcquireAsync(Req(pool, "B"), CancellationToken.None).AsTask();
        var c = s.AcquireAsync(Req(pool, "C"), CancellationToken.None).AsTask();
        using var cts = new CancellationTokenSource();
        var e = s.AcquireAsync(Req(pool, "E"), cts.Token).AsTask();
        var d = s.AcquireAsync(Req(pool, "D", priority: 100), CancellationToken.None).AsTask();
        Check.Equal("D,B,C,E", string.Join(",", s.Snapshot().Single(p => p.Key == pool).Waiters.Select(w => w.AgentId)));
        cts.Cancel();
        try { await e; throw new AssertException("expected cancellation"); } catch (OperationCanceledException) { }
        Check.Equal(3, s.Snapshot().Single(p => p.Key == pool).Queued);

        a.Dispose();
        a.Dispose(); // idempotent: must not free a second slot
        var ld = await d.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal("D", ld.AgentId);
        Check.True(a.IsReleased);
        Check.False(b.IsCompleted || c.IsCompleted, "B and C still wait");
        ld.Dispose();
        var lb = await b.WaitAsync(TimeSpan.FromSeconds(2));
        Check.False(c.IsCompleted);
        lb.Dispose();
        var lc = await c.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(1, s.Snapshot().Single(p => p.Key == pool).Busy);
        lc.Dispose();
        Check.Equal(0, s.Snapshot().Where(p => p.Key == pool).Sum(p => p.Busy));
        Check.True(s.TryAcquire(Req(pool, "Z"), out var z) && z is not null);
        z!.Dispose();
    }

    private static async Task CapacityIncrease()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.a", J("""{ "model": "fake/local", "instances": 1 }""")),
            plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var a = await s.AcquireAsync(Req("a", "A"), CancellationToken.None);
        var b = s.AcquireAsync(Req("a", "B"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.False(b.IsCompleted);
        h.Settings.Set("agents.a.instances", 2);
        var lb = await b.WaitAsync(TimeSpan.FromSeconds(3));
        var info = s.Snapshot().Single(p => p.Key == "a");
        Check.Equal(2, info.Capacity);
        Check.Equal(2, info.Busy);
        a.Dispose();
        lb.Dispose();

        // models.changed: without instances an agent follows the catalog's concurrency
        h.Settings.Set("agents.a.instances", null);
        await h.Bus.DrainAsync();
        h.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = 3;
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        Check.Equal(3, s.Snapshot().Single(p => p.Key == "a").Capacity);
    }

    private static async Task Debounce()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        await Task.Delay(250);
        var before = h.Bus.OfType(AgentSchedulerEvents.Changed).Count;
        var pool = s.Resolve(TestHost.LocalModel());
        var leases = new List<IAgentSlot>();
        for (var i = 0; i < 2; i++) leases.Add(await s.AcquireAsync(Req(pool, "A" + i), CancellationToken.None));
        foreach (var l in leases) l.Dispose();
        await Task.Delay(300);
        await h.Bus.DrainAsync();
        var after = h.Bus.OfType(AgentSchedulerEvents.Changed).Count;
        Check.True(after - before is >= 1 and <= 2, $"coalesced into {after - before} event(s)");
        var pools = (JsonArray)FakeBus.Data(h.Bus.OfType(AgentSchedulerEvents.Changed).Last())["agents"]!;
        Check.Equal(0, pools.Count, "nothing busy");
        // a settings change that changes nothing here sends nothing
        h.Settings.Set("ui.theme", "dark");
        await Task.Delay(250);
        await h.Bus.DrainAsync();
        Check.Equal(after, h.Bus.OfType(AgentSchedulerEvents.Changed).Count);
    }

    private static async Task QueueCapAndTimeout()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.solo", J("""{ "model": "fake/solo" }"""));
            x.Settings.SetQuiet("agents.queueMax", JsonValue.Create(1));
            x.Settings.SetQuiet("agents.queueTimeoutSeconds", JsonValue.Create(2));
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var listed = (JsonArray)(await h.Rpc.CallAsync("agents.list"))!;
        Check.Equal("solo", string.Join("|", listed.Select(p => (string?)p!["key"])), "the queue settings are not agents");

        var a = await s.AcquireAsync(Req("solo", "A"), CancellationToken.None);
        var w1 = s.AcquireAsync(Req("solo", "W1"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.Equal(1, s.Snapshot().Single(p => p.Key == "solo").Queued, "one waiter in line");

        // beyond the cap: refused at once (the callers handle it and tell the user), not parked
        try { await s.AcquireAsync(Req("solo", "W2"), CancellationToken.None); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException ex) { Check.Contains(ex.Message, "agents.queueMax"); }
        Check.Equal(1, s.Snapshot().Single(p => p.Key == "solo").Queued, "the refused one is not queued");

        // a free slot goes to the waiter in line: the cap does not disturb the normal flow
        a.Dispose();
        var lw1 = await w1.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Equal("W1", lw1.AgentId);

        // a waiter that waits longer than queueTimeoutSeconds fails with a clear error instead of waiting forever
        lw1.Dispose();
        var a2 = await s.AcquireAsync(Req("solo", "A2"), CancellationToken.None);
        var w3 = s.AcquireAsync(Req("solo", "W3"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.Equal("queued", s.Snapshot().Single(p => p.Key == "solo").Status);
        try { await w3.WaitAsync(TimeSpan.FromSeconds(10)); throw new AssertException("expected CallRefusedException"); }
        catch (CallRefusedException ex) { Check.Contains(ex.Message, "agents.queueTimeoutSeconds"); Check.Contains(ex.Message, "2 s"); }
        Check.True(s.Snapshot().Single(p => p.Key == "solo").Waiters.Count == 0, "the timed-out waiter is gone");
        a2.Dispose();
        Check.Equal(0, s.Snapshot().Sum(p => p.Busy + p.Queued));
    }

    private static async Task QueueHasNoTimeoutByDefault()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.solo", J("""{ "model": "fake/solo" }""")),
            plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var a = await s.AcquireAsync(Req("solo", "A"), CancellationToken.None);
        var w = s.AcquireAsync(Req("solo", "W"), CancellationToken.None).AsTask();
        await Task.Delay(3000); // longer than any timeout a test would set (the explicit one above is 2 s)
        Check.False(w.IsCompleted, "a waiter is not failed just for waiting");
        Check.Equal(1, s.Snapshot().Single(p => p.Key == "solo").Queued, "it is still in line");
        a.Dispose();
        var got = await w.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Equal("W", got.AgentId);
        got.Dispose();
    }

    private static async Task ReloadDuringWait()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("x", c => gate.Task.WaitAsync(c));
        var s1 = h.NewSession(model: "fake/solo");
        var s2 = h.NewSession(model: "fake/solo");
        await h.SendAsync(s1.Id, "go");
        await Wait.Until(() => h.Catalog.Calls == 1, "first running");
        await h.SendAsync(s2.Id, "go");
        await Wait.Until(() => h.Runtime.GetBySession(s2.Id)!.Status == AgentStatus.Queued, "second queued");

        // direct: waiters of a stopped scheduler fail with OperationCanceledException
        var old = h.Scheduler!;
        var extra = old.AcquireAsync(Req("fake/solo", "extra"), CancellationToken.None).AsTask();
        await h.StopPluginAsync("netpi.agents");
        try { await extra; throw new AssertException("expected cancellation"); } catch (OperationCanceledException) { }

        await h.StartPluginAsync(new AgentsPlugin());
        // The old physical call still occupies the model after reload, including any scheduler gap.
        Check.Equal(1, h.Catalog.Calls);
        Check.Equal(1, h.Scheduler!.Resources().Single(r => r.Model == "fake/solo").Busy);
        gate.SetResult();
        await Wait.Until(() => h.Catalog.Calls == 2, "second run got a slot after the old call ended");
        await h.IdleAsync(s1.Id);
        await h.IdleAsync(s2.Id);
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(p => p.Busy + p.Queued));
    }

    private static async Task Budget()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("budget.providers", JsonNode.Parse("""{ "fake": { "dailyTokens": 50 } }""")));
        var s = h.NewSession();
        await h.SendAsync(s.Id, "first");
        await h.IdleAsync(s.Id);
        await h.Bus.DrainAsync(); // usage.recorded reached the agents plugin

        var summary = (await h.Rpc.CallAsync("usage.summary"))!;
        var fake = ((JsonArray)summary["providers"]!).Single(p => (string?)p!["provider"] == "fake")!;
        Check.Equal(100L, fake["inputTokens"]!.GetValue<long>());
        Check.Equal(10L, fake["outputTokens"]!.GetValue<long>());
        Check.Equal(1L, fake["calls"]!.GetValue<long>());
        Check.Equal(50L, fake["budgetTokens"]!.GetValue<long>());
        Check.Equal(DateTime.Now.ToString("yyyy-MM-dd"), (string?)summary["day"]);

        await h.SendAsync(s.Id, "second");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(1, h.Catalog.Calls, "second call blocked by the budget");
        var last = h.Messages(s.Id)[^1];
        Check.Equal("error", last.MetaString("kind"));
        Check.Contains(last.Text, "budget");
        Check.Contains(last.Text, "budget.providers.fake.dailyTokens");
        Check.Contains(a.Error, "budget");

        // another provider is unaffected
        h.SetModel(s.Id, "cloud/big");
        await h.SendAsync(s.Id, "third");
        await h.IdleAsync(s.Id);
        Check.Equal(2, h.Catalog.Calls);
    }

    private static async Task NoAgentsPlugin()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.All & ~TestHost.Plugins.Agents);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hi");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(null, a.Agent);
        Check.Equal("ok", h.Messages(s.Id)[^1].Text);
    }
}
