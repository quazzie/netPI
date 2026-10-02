using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class ResourceSchedulerTests
{
    public static void Register(TestRunner t)
    {
        t.Add("scheduler resource: missing concurrency shares the fallback across named and plain calls", () => Fallback(null));
        t.Add("scheduler resource: zero concurrency uses the fallback instead of blocking every call", () => Fallback(0));
        t.Add("scheduler resource: negative concurrency uses the fallback", () => Fallback(-1));
        t.Add("scheduler resource: waiters across pools follow priority then FIFO", Fairness);
        t.Add("scheduler resource: a pool at its own cap does not block an eligible peer", SkipCappedPool);
        t.Add("scheduler resource: catalog and fallback changes refresh a plain pool without another resolve", RefreshCapacity);
        t.Add("scheduler resource: changing an agent model keeps its old running call counted", Rebind);
        t.Add("scheduler resource: named chats without concurrency metadata never exceed two provider calls", ProviderBound);
    }

    private static AgentSlotRequest Req(string key, string id, int priority = 0) =>
        new() { Key = key, AgentId = id, Priority = priority, Label = id };

    private static void Configure(TestHost h, string id, int instances) =>
        h.Settings.SetQuiet("agents." + id, new JsonObject { ["model"] = "fake/local", ["instances"] = instances });

    private static void Blocked(IAgentScheduler s, string key, string id, string reason)
    {
        var acquired = s.TryAcquire(Req(key, id), out var unexpected);
        unexpected?.Dispose();
        Check.False(acquired, reason);
    }

    private static async Task<IAgentSlot> Take(IAgentScheduler s, string key, string id) =>
        // 10 s, not 3: a deadlock is still caught long before the runner's per-test timeout, and a 3 s wall-clock
        // budget is tighter than a box running two suite processes and a build at once (2026-10-02: Rebind tripped it
        // once there and passed 15 of 16 runs, the logic being clean every time).
        await s.AcquireAsync(Req(key, id), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task Fallback(int? concurrency)
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = concurrency;
            x.Settings.SetQuiet("models.localSlots", 2);
            Configure(x, "a", 3);
            Configure(x, "b", 3);
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var model = h.Catalog.Cached.Single(m => m.Ref == "fake/local");
        var plain = s.Resolve(model);
        // A refresh can evict the idle pool before the caller acquires its returned key.
        ((NetPI.Agents.AgentScheduler)s).Refresh();
        using var a = await Take(s, "a", "a-running");
        using var b = await Take(s, "b", "b-running");
        Blocked(s, "a", "a-extra", "named agents share the fallback physical cap");
        Blocked(s, plain, "plain-extra", "plain calls share that same cap");
        var waiting = s.AcquireAsync(Req(plain, "plain-waiting"), CancellationToken.None).AsTask();
        Check.False(waiting.IsCompleted, "the third call queues even though its pool is empty");
        a.Dispose();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Blocked(s, "b", "still-extra", "granting a plain call keeps total occupancy at two");
        a.Dispose();
        Blocked(s, "a", "double-release", "releasing the old lease twice cannot free another slot");
    }

    private static async Task Fairness()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = 1;
            Configure(x, "a", 1);
            Configure(x, "b", 1);
            Configure(x, "c", 1);
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        using var holder = await Take(s, "a", "holder");
        var oldest = s.AcquireAsync(Req("b", "oldest"), CancellationToken.None).AsTask();
        var newest = s.AcquireAsync(Req("a", "newest"), CancellationToken.None).AsTask();
        var resume = s.AcquireAsync(Req("c", "resume", 100), CancellationToken.None).AsTask();
        holder.Dispose();
        Check.True(resume.IsCompletedSuccessfully, "parent-resume priority wins across pools");
        Check.False(oldest.IsCompleted || newest.IsCompleted, "lower-priority work still waits");
        using var resumed = await resume;
        resumed.Dispose();
        Check.True(oldest.IsCompletedSuccessfully, "equal priority is FIFO across pools, regardless of the releasing pool");
        Check.False(newest.IsCompleted);
        using var first = await oldest;
        first.Dispose();
        using var last = await newest.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task SkipCappedPool()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            Configure(x, "a", 1);
            Configure(x, "b", 2);
            Configure(x, "c", 1);
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        using var a = await Take(s, "a", "a-running");
        using var b = await Take(s, "b", "b-running");
        var capped = s.AcquireAsync(Req("a", "capped", 100), CancellationToken.None).AsTask();
        var eligible = s.AcquireAsync(Req("c", "eligible", 10), CancellationToken.None).AsTask();
        var low = s.AcquireAsync(Req("b", "low"), CancellationToken.None).AsTask();
        b.Dispose();
        Check.True(eligible.IsCompletedSuccessfully, "an ineligible high-priority pool does not obstruct an eligible peer");
        Check.False(capped.IsCompleted || low.IsCompleted);
        using var c = await eligible;
        a.Dispose();
        using var next = await capped.WaitAsync(TimeSpan.FromSeconds(3));
        c.Dispose();
        using var last = await low.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task RefreshCapacity()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = null;
            x.Settings.SetQuiet("models.localSlots", 1);
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        var model = h.Catalog.Cached.Single(m => m.Ref == "fake/local");
        var key = s.Resolve(model);
        using var first = await Take(s, key, "first");
        var second = s.AcquireAsync(Req(key, "second"), CancellationToken.None).AsTask();
        Check.False(second.IsCompleted);
        h.Settings.Set("models.localSlots", 2);
        await h.Bus.DrainAsync();
        Check.True(second.IsCompletedSuccessfully, "a fallback increase wakes an already-resolved plain pool");
        using var granted = await second;
        Check.Equal(2, s.Snapshot().Single(p => p.Key == key).Capacity);

        model.Concurrency = 3;
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        Check.Equal(3, s.Snapshot().Single(p => p.Key == key).Capacity);
        using var third = await Take(s, key, "third");
        model.Concurrency = 1;
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        Check.Equal(1, s.Snapshot().Single(p => p.Key == key).Capacity);
        Blocked(s, key, "extra", "shrinking capacity does not interrupt owners or admit extra work");
        first.Dispose();
        granted.Dispose();
        Blocked(s, key, "still-full", "the final active owner fills the reduced capacity");
        third.Dispose();
        h.Settings.Set("models.localSlots", 0);
        model.Concurrency = null;
        ((IEventBus)h.Bus).Publish(EventTypes.ModelsChanged, new JsonObject());
        await h.Bus.DrainAsync();
        using var clamped = await Take(s, s.Resolve(model), "clamped");
        Check.Equal(1, s.Snapshot().Single(p => p.Key == key).Capacity, "invalid fallback is clamped to one");
    }

    private static async Task Rebind()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = 1;
            Configure(x, "a", 2);
            Configure(x, "b", 2);
        }, plugins: TestHost.Plugins.Agents);
        var s = h.Scheduler!;
        using var oldCall = await Take(s, "a", "old-model-call");
        h.Settings.Set("agents.a.model", JsonValue.Create("fake/solo"));
        await h.Bus.DrainAsync();
        Blocked(s, "b", "old-model-extra", "rebinding an agent must not stop counting its active call on the original model");
        using var newCall = await Take(s, "a", "new-model-call");
        var queued = s.AcquireAsync(Req("b", "old-model-waiter"), CancellationToken.None).AsTask();
        Check.False(queued.IsCompleted);
        oldCall.Dispose();
        using var waiter = await queued.WaitAsync(TimeSpan.FromSeconds(3));
        Check.Equal(2, s.Snapshot().Sum(p => p.Busy), "one call per model after the old lease finishes");
    }

    private static async Task ProviderBound()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = null;
            x.Settings.SetQuiet("models.localSlots", 2);
            Configure(x, "a", 3);
            Configure(x, "b", 3);
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new object();
        var active = 0;
        var maximum = 0;
        h.Catalog.Handler = (_, _) => Reply.Text("done", async ct =>
        {
            lock (counts)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == 2) twoEntered.TrySetResult();
            }
            try { await release.Task.WaitAsync(ct); }
            finally { lock (counts) active--; }
        });
        var sessions = Enumerable.Range(0, 4).Select(i => h.NewSession(title: "bounded-" + i)).ToList();
        try
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                h.Sessions.UpdateSession(sessions[i].Id, s => (s.Meta ??= new JsonObject())[SessionAgent.MetaKey] = i % 2 == 0 ? "a" : "b");
                await h.SendAsync(sessions[i].Id, "go");
            }
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Wait.Until(() => sessions.Count(s => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Queued) == 2,
                "the other named chats queue behind the physical bound");
            Check.Equal(2, h.Catalog.Calls, "only admitted calls reached the provider");
            release.TrySetResult();
            foreach (var session in sessions) await h.IdleAsync(session.Id);
            Check.Equal(4, h.Catalog.Calls, "every queued chat eventually completes");
            lock (counts)
            {
                Check.Equal(2, maximum, "observed provider-call concurrency, not only scheduler counters");
                Check.Equal(0, active);
            }
        }
        finally { release.TrySetResult(); }
    }
}
