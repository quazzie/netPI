using System.Text.Json.Nodes;
using NetPI.Lanes;

namespace NetPI.Agent.Tests;

public static class LaneTests
{
    public static void Register(TestRunner t)
    {
        t.Add("lanes: capacity 2 → third agent queues", ThirdQueues);
        t.Add("lanes: snapshot lists idle pools from the catalog", SnapshotIdlePools);
        t.Add("lanes: settings pools with globs and capacity overrides", SettingsPools);
        t.Add("lanes: priority, FIFO, cancellation, idempotent release", PriorityAndCancel);
        t.Add("lanes: live capacity increase wakes waiters", CapacityIncrease);
        t.Add("lanes: lanes.changed is debounced", Debounce);
        t.Add("lanes: stop fails waiters; runs survive a lanes reload", ReloadDuringWait);
        t.Add("lanes: budget exceeded + usage.summary", Budget);
        t.Add("lanes: runs without the lanes plugin", NoLanes);
    }

    private static LaneRequest Req(string pool, string agent, int priority = 0) => new() { PoolKey = pool, AgentId = agent, Priority = priority, Label = agent };

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
            return st.Count(x => x == AgentStatus.Running) == 2 && st.Count(x => x == AgentStatus.Queued) == 1;
        }, "two running, one queued");
        var queued = sessions.Select(s => h.Runtime.GetBySession(s.Id)!).Single(a => a.Status == AgentStatus.Queued);
        Check.Equal("waiting for lane fake/local", queued.Activity);
        Check.Equal(2, h.Catalog.Calls, "queued agent has not called the model");
        var pool = h.Lanes!.Snapshot().Single(p => p.Key == "fake/local");
        Check.Equal(2, pool.Capacity);
        Check.Equal(2, pool.Busy);
        Check.Equal(1, pool.Queued);
        Check.Equal(queued.Id, pool.Waiters.Single().AgentId);
        Check.Equal("catalog", pool.Source);
        Check.Equal("queued", pool.Status);
        var rpc = (JsonArray)(await h.Rpc.CallAsync("lanes.list"))!;
        Check.Equal(2, rpc.Single(p => (string?)p!["key"] == "fake/local")!["busy"]!.GetValue<int>());

        gate.SetResult();
        foreach (var s in sessions) await h.IdleAsync(s.Id);
        Check.Equal(3, h.Catalog.Calls);
        await Task.Delay(250);
        await h.Bus.DrainAsync();
        var last = FakeBus.Data(h.Bus.OfType(EventTypes.LanesChanged).Last());
        var lp = ((JsonArray)last["pools"]!).Single(p => (string?)p!["key"] == "fake/local")!;
        Check.Equal(0, lp["busy"]!.GetValue<int>());
        Check.Equal(0, lp["queued"]!.GetValue<int>());
    }

    private static async Task SnapshotIdlePools()
    {
        await using var h = await TestHost.StartAsync();
        var pools = h.Lanes!.Snapshot();
        Check.Equal("cloud|fake/local|fake/solo", string.Join("|", pools.Select(p => p.Key)));
        Check.Equal(4, pools[0].Capacity);
        Check.Equal("default", pools[0].Source);
        Check.Equal("cloud", pools[0].Provider);
        Check.Equal(2, pools[1].Capacity);
        Check.Equal(1, pools[2].Capacity);
        Check.True(pools.All(p => p.Busy == 0 && p.Status == "idle"));
        // a model that appears later shows up too
        h.Catalog.AddModel(new ModelInfo { Provider = "fake", Id = "new", IsLocal = true, Status = "offline" });
        var again = h.Lanes.Snapshot();
        var np = again.Single(p => p.Key == "fake/new");
        Check.Equal(1, np.Capacity, "local default capacity");
        Check.Equal("offline", np.Status);
    }

    private static async Task SettingsPools()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("lanes.pools", JsonNode.Parse("""
                {
                  "gpu": { "capacity": 3, "models": ["fake/*"] },
                  "cloud": { "capacity": 7 }
                }
                """));
            x.Settings.SetQuiet("lanes.cloudDefaultCapacity", 5);
        });
        var s = h.Lanes!;
        Check.Equal("gpu", s.ResolvePool(TestHost.LocalModel()));
        Check.Equal("gpu", s.ResolvePool(TestHost.SoloModel()));
        Check.Equal("cloud", s.ResolvePool(TestHost.CloudModel()));
        var pools = s.Snapshot();
        var gpu = pools.Single(p => p.Key == "gpu");
        Check.Equal(3, gpu.Capacity);
        Check.Equal("settings", gpu.Source);
        Check.Equal("fake/local,fake/solo", string.Join(",", gpu.Models.OrderBy(m => m)));
        Check.Equal(7, pools.Single(p => p.Key == "cloud").Capacity, "capacity override without models");
        Check.True(LaneScheduler.GlobMatch("anthropic/*", "Anthropic/claude-x"));
        Check.False(LaneScheduler.GlobMatch("fake/l?cal", "fake/solo"));
        // another provider uses the cloud default
        h.Catalog.AddModel(new ModelInfo { Provider = "other", Id = "m", IsLocal = false });
        Check.Equal(5, h.Lanes!.Snapshot().Single(p => p.Key == "other").Capacity);
    }

    private static async Task PriorityAndCancel()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Lanes);
        var s = h.Lanes!;
        var pool = s.ResolvePool(TestHost.SoloModel()); // capacity 1
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
        Check.Equal(0, s.Snapshot().Single(p => p.Key == pool).Busy);
        Check.True(s.TryAcquire(Req(pool, "Z"), out var z) && z is not null);
        z!.Dispose();
    }

    private static async Task CapacityIncrease()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Lanes);
        var s = h.Lanes!;
        var pool = s.ResolvePool(TestHost.SoloModel());
        var a = await s.AcquireAsync(Req(pool, "A"), CancellationToken.None);
        var b = s.AcquireAsync(Req(pool, "B"), CancellationToken.None).AsTask();
        await Task.Delay(50);
        Check.False(b.IsCompleted);
        h.Settings.Set("lanes.pools", JsonNode.Parse("""{ "fake/solo": { "capacity": 2 } }"""));
        var lb = await b.WaitAsync(TimeSpan.FromSeconds(3));
        var info = s.Snapshot().Single(p => p.Key == pool);
        Check.Equal(2, info.Capacity);
        Check.Equal(2, info.Busy);
        Check.Equal("settings", info.Source);
        a.Dispose();
        lb.Dispose();

        // models.changed: the catalog reports a new concurrency
        h.Settings.Set("lanes.pools", null);
        await h.Bus.DrainAsync();
        h.Catalog.Cached.Single(m => m.Ref == "fake/solo").Concurrency = 3;
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        Check.Equal(3, s.Snapshot().Single(p => p.Key == pool).Capacity);
    }

    private static async Task Debounce()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Lanes);
        var s = h.Lanes!;
        await Task.Delay(250);
        var before = h.Bus.OfType(EventTypes.LanesChanged).Count;
        var pool = s.ResolvePool(TestHost.LocalModel());
        var leases = new List<ILaneLease>();
        for (var i = 0; i < 2; i++) leases.Add(await s.AcquireAsync(Req(pool, "A" + i), CancellationToken.None));
        foreach (var l in leases) l.Dispose();
        await Task.Delay(300);
        await h.Bus.DrainAsync();
        var after = h.Bus.OfType(EventTypes.LanesChanged).Count;
        Check.True(after - before is >= 1 and <= 2, $"coalesced into {after - before} event(s)");
        var pools = (JsonArray)FakeBus.Data(h.Bus.OfType(EventTypes.LanesChanged).Last())["pools"]!;
        Check.True(pools.Count >= 3);
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
        var old = h.Lanes!;
        var extra = old.AcquireAsync(Req("fake/solo", "extra"), CancellationToken.None).AsTask();
        await h.StopPluginAsync("netpi.lanes");
        try { await extra; throw new AssertException("expected cancellation"); } catch (OperationCanceledException) { }

        await h.StartPluginAsync(new LanesPlugin());
        // the queued agent re-resolved the scheduler (or ran without lanes during the gap) and gets to run
        await Wait.Until(() => h.Catalog.Calls == 2, "second agent got a lane after the reload");
        gate.SetResult();
        await h.IdleAsync(s1.Id);
        await h.IdleAsync(s2.Id);
        Check.Equal(0, h.Lanes!.Snapshot().Sum(p => p.Busy + p.Queued));
    }

    private static async Task Budget()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("lanes.budgets", JsonNode.Parse("""{ "fake": { "dailyTokens": 50 } }""")));
        var s = h.NewSession();
        await h.SendAsync(s.Id, "first");
        await h.IdleAsync(s.Id);
        await h.Bus.DrainAsync(); // usage.recorded reached the lanes plugin

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
        Check.Contains(last.Text, "lanes.budgets.fake.dailyTokens");
        Check.Contains(a.Error, "budget");

        // another provider is unaffected
        h.SetModel(s.Id, "cloud/big");
        await h.SendAsync(s.Id, "third");
        await h.IdleAsync(s.Id);
        Check.Equal(2, h.Catalog.Calls);
    }

    private static async Task NoLanes()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.All & ~TestHost.Plugins.Lanes);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hi");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(null, a.Pool);
        Check.Equal("ok", h.Messages(s.Id)[^1].Text);
    }
}
