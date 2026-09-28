using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

public static class ReservationTests
{
    public static void Register(TestRunner t)
    {
        t.Add("budget: concurrent reservations share a cap and survive ledger reload", Concurrent);
        t.Add("budget: concurrent first-use recording never loses spend", ConcurrentRecording);
        t.Add("budget: day and period changes recalculate spending", Rollover);
        t.Add("budget: unknown prices are blocked by caps, free calls remain allowed", UnknownPrice);
        t.Add("budget: interrupted usage, rejected calls and cancellation settle reservations", Interrupted);
    }

    private static ModelRequest Request() => new()
    {
        Model = new ModelInfo { Provider = "cloud", Id = "test", ContextWindow = 32000,
            Extra = new JsonObject { ["pricing"] = new JsonObject { ["prompt"] = "0.000001", ["completion"] = "0.000002" } } },
        Messages = [ChatMessage.UserText("hi")], MaxOutputTokens = 100,
    };
    private static Ledger Create(TestHost h)
    {
        var l = new Ledger(new TestPluginContext(h, "ledger-test"));
        l.Initialize();
        return l;
    }

    private static async Task Concurrent()
    {
        using var db = TestSqlite.TryCreate() ?? throw new Exception("SQLite required");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None, db: db);
        var l = Create(h);
        var req = Request();
        var estimate = Ledger.Estimate(req, Ledger.PriceOf(req.Model, null));
        h.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(estimate * 1.5));
        var won = new System.Collections.Concurrent.ConcurrentBag<Ledger.Reservation>();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try { won.Add(l.Reserve(req, "a", null)); } catch (BudgetExceededException) { }
        })));
        Check.Equal(1, won.Count, "only one simultaneous call fits");
        Check.Equal(estimate, l.Spent().Period);
        var reloaded = Create(h);
        await Check.ThrowsAsync<BudgetExceededException>(() => Task.Run(() => reloaded.Reserve(req, "a", null)));
        l.Settle(won.Single(), new Usage { CostUsd = 0.00001 }, true, false);
        var next = reloaded.Reserve(req, "a", null);
        reloaded.Settle(next, null, false, true);
        Check.Equal(0.00001, reloaded.Spent().Period);
        Check.Equal(0.0, reloaded.BudgetStatus()["reservedOrUnsettledUsd"]!.GetValue<double>());
    }

    private static async Task ConcurrentRecording()
    {
        using var db = TestSqlite.TryCreate() ?? throw new Exception("SQLite required");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None, db: db);
        var a = Create(h); var b = Create(h);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            (i % 2 == 0 ? a : b).RecordCall(Request(), new ChatMessage { Usage = new Usage { CostUsd = 1 } }, "a", null);
            _ = a.Spent(); _ = b.SpentToday("a");
        })));
        Check.Equal(100.0, a.Spent().Period);
        Check.Equal(100.0, b.SpentToday("a"));
        Check.Equal(100.0, db.Scalar<double>("SELECT SUM(cost_usd) FROM usage_calls"));
    }

    private static async Task Rollover()
    {
        using var db = TestSqlite.TryCreate() ?? throw new Exception("SQLite required");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None, db: db);
        var l = Create(h);
        var req = Request();
        var r = l.Reserve(req, "a", null);
        l.Settle(r, new Usage { CostUsd = 2 }, true, false);
        Check.Equal(2.0, l.Spent().Today);
        var old = DateTimeOffset.Now.AddMonths(-2);
        db.Execute("UPDATE usage_calls SET ts=@ts, day=@day", new { ts = old.ToUnixTimeMilliseconds(), day = old.ToString("yyyy-MM-dd") });
        Check.Equal(0.0, l.Spent().Period);
        Check.Equal(0.0, l.SpentToday("a"));
        l.RecordCall(req, new ChatMessage { Usage = new Usage { CostUsd = 3 } }, "a", null);
        Check.Equal(3.0, l.Spent().Today);
    }

    private static async Task UnknownPrice()
    {
        using var db = TestSqlite.TryCreate() ?? throw new Exception("SQLite required");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None, db: db);
        h.Settings.SetQuiet("budget.dailyUsd", JsonValue.Create(1));
        var l = Create(h); var r = Request(); r.Model.Extra = null;
        await Check.ThrowsAsync<BudgetExceededException>(() => Task.Run(() => l.Reserve(r, null, null)));
        r.Model.IsLocal = true;
        var local = l.Reserve(r, null, null); l.Settle(local, new Usage(), true, false);
        Check.Equal(0.0, l.Spent().Today);
    }

    private static async Task Interrupted()
    {
        using var db = TestSqlite.TryCreate() ?? throw new Exception("SQLite required");
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None, db: db);
        var ctx = new TestPluginContext(h, "ledger-test");
        var l = Create(h); var m = new LedgerMiddleware(l, new AgentScheduler(ctx, l));
        var req = Request();
        static async IAsyncEnumerable<ModelStreamEvent> Partial(ModelRequest r, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UsageUpdate(new Usage { InputTokens = 25, CostUsd = 0.003 });
            await Task.Yield();
            throw new IOException("connection dropped");
        }
        await Check.ThrowsAsync<IOException>(async () => { await foreach (var e in m.InvokeAsync(req, Partial, default)) { } });
        Check.Equal(0.003, l.Spent().Period);
        Check.Equal(25L, db.Scalar<long>("SELECT SUM(input_tokens) FROM usage_calls"));
        static async IAsyncEnumerable<ModelStreamEvent> Reject(ModelRequest r, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new ModelException("rate limited", true, 429);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        await Check.ThrowsAsync<ModelException>(async () => { await foreach (var e in m.InvokeAsync(req, Reject, default)) { } });
        Check.Equal(0.003, l.Spent().Period);
        static async IAsyncEnumerable<ModelStreamEvent> Cancel(ModelRequest r, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextDelta("partial");
            await Task.Yield();
            throw new OperationCanceledException();
        }
        await Check.ThrowsAsync<OperationCanceledException>(async () => { await foreach (var e in m.InvokeAsync(req, Cancel, default)) { } });
        Check.True(l.Spent().Period > 0.003, "unbilled interruption keeps the reservation estimate");
        Check.True(l.BudgetStatus()["interruptedEstimateUsd"]!.GetValue<double>() > 0);
        Check.Equal(3L, db.Scalar<long>("SELECT COUNT(*) FROM usage_calls"));
    }
}
