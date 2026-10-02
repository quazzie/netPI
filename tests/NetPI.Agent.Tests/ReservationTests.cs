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
        t.Add("budget: interrupted usage, rejected calls and cancellation settle from what they used, not the reservation", Interrupted);
        t.Add("budget: a 529 storm under a dollar cap no longer locks the day", FailedBeforeFirstByte);
        t.Add("budget: a hot swap runs two ledger generations: one reservation, one settle", Swap);
        t.Add("budget: budget.allow runs alongside reservations without wedging the gates", AllowParallel);
        t.Add("budget: the reservation holds what the provider can actually send beside a full window", WindowClamp);
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

    /// <summary>The ledger's call collection of the test's own plugin id (the same declaration the ledger makes).</summary>
    private static IDataCollection Calls(TestHost h) =>
        h.Storage.Plugins.For("ledger-test").Collection("usage_calls", new CollectionSpec()
            .Integer("ts").Text("day").Text("sessionId").Text("rootSessionId").Text("lane").Real("costUsd").Text("costSource"));

    private static async Task Concurrent()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var l = Create(h);
        var req = Request();
        var estimate = Ledger.Estimate(req, Ledger.PriceOf(req.Model, null));
        h.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(estimate * 1.5));
        var won = new System.Collections.Concurrent.ConcurrentBag<Ledger.Reservation>();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try { won.Add(l.Reserve(req, "a", null)); } catch (CallRefusedException) { }
        })));
        Check.Equal(1, won.Count, "only one simultaneous call fits");
        Check.Equal(estimate, l.Spent().Period);
        var reloaded = Create(h);
        await Check.ThrowsAsync<CallRefusedException>(() => Task.Run(() => reloaded.Reserve(req, "a", null)));
        l.Settle(won.Single(), new Usage { CostUsd = 0.00001 }, true, false);
        var next = reloaded.Reserve(req, "a", null);
        reloaded.Settle(next, null, false, true);
        Check.Equal(0.00001, reloaded.Spent().Period);
        Check.Equal(0.0, reloaded.BudgetStatus()["reservedOrUnsettledUsd"]!.GetValue<double>());
    }

    private static async Task ConcurrentRecording()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var a = Create(h); var b = Create(h);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            (i % 2 == 0 ? a : b).RecordCall(Request(), new ChatMessage { Usage = new Usage { CostUsd = 1 } }, "a", null);
            _ = a.Spent(); _ = b.SpentToday("a");
        })));
        Check.Equal(100.0, a.Spent().Period);
        Check.Equal(100.0, b.SpentToday("a"));
        Check.Equal(100.0, Calls(h).Sum("costUsd", null));
    }

    private static async Task Rollover()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var l = Create(h);
        var req = Request();
        var r = l.Reserve(req, "a", null);
        l.Settle(r, new Usage { CostUsd = 2 }, true, false);
        Check.Equal(2.0, l.Spent().Today);
        var old = DateTimeOffset.Now.AddMonths(-2);
        // move the charge out of the period: its call, and the lane-day roll-up that read it from (the old store
        // recomputed the roll-up from the calls; now the roll-up is the read, so it moves with them)
        var calls = Calls(h);
        var doc = calls.Get(r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        doc["ts"] = old.ToUnixTimeMilliseconds();
        doc["day"] = old.ToString("yyyy-MM-dd");
        calls.Put(r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), doc);
        var lanes = h.Storage.Plugins.For("ledger-test").Collection("lane_usage", new CollectionSpec().Text("day").Text("laneKey").Real("costUsd"));
        var lane = lanes.Find(new DataQuery().Eq("laneKey", "a")).Single();
        lane.Doc["day"] = old.ToString("yyyy-MM-dd");
        lanes.Put(lane.Key, lane.Doc);
        Check.Equal(0.0, l.Spent().Period);
        Check.Equal(0.0, l.SpentToday("a"));
        l.RecordCall(req, new ChatMessage { Usage = new Usage { CostUsd = 3 } }, "a", null);
        Check.Equal(3.0, l.Spent().Today);
    }

    private static async Task UnknownPrice()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        h.Settings.SetQuiet("budget.dailyUsd", JsonValue.Create(1));
        var l = Create(h); var r = Request(); r.Model.Extra = null;
        await Check.ThrowsAsync<CallRefusedException>(() => Task.Run(() => l.Reserve(r, null, null)));
        r.Model.IsLocal = true;
        var local = l.Reserve(r, null, null); l.Settle(local, new Usage(), true, false);
        Check.Equal(0.0, l.Spent().Today);
    }

    private static async Task Interrupted()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
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
        Check.Equal(25L, Calls(h).Find(null).Sum(d => d.Doc["inputTokens"]?.GetValue<long>() ?? 0));
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
        var estimate = Ledger.Estimate(req, Ledger.PriceOf(req.Model, null));
        Check.True(l.Spent().Period > 0.003, "a stop that streamed settles from the partial work");
        Check.True(l.Spent().Period < 0.003 + estimate, "no more than the reservation (the old code charged the full reservation)");
        Check.Equal(1L, Calls(h).Count(new DataQuery().Eq("costSource", "interrupted-estimate")));
        Check.Equal(1L, l.BudgetStatus()["interruptedEstimateCalls"]!.GetValue<long>(), "the settled-from-estimate count is visible");
        Check.Equal(3L, Calls(h).Count(null));
    }

    private static async Task FailedBeforeFirstByte()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var l = Create(h);
        var ctx = new TestPluginContext(h, "ledger-test");
        var m = new LedgerMiddleware(l, new AgentScheduler(ctx, l));
        h.Settings.SetQuiet("budget.dailyUsd", JsonValue.Create(1));
        var req = new ModelRequest
        {
            Model = new ModelInfo { Provider = "cloud", Id = "storm",
                Extra = new JsonObject { ["pricing"] = new JsonObject { ["prompt"] = "0.000001", ["completion"] = "0.000002" } } },
            // A million characters ≈ 250 k tokens. The old byte-based estimate (≈ $1.00 at $1/M) did not fit under the
            // $1 cap, so the first 529's reservation locked the day's budget although the call was never billed.
            Messages = [ChatMessage.UserText(new string('x', 1_000_000))],
            MaxOutputTokens = 100,
        };
        // 503, 529 and a dead connection, all before the first byte: nothing was billed, nothing is settled.
        await Check.ThrowsAsync<ModelException>(async () => { await foreach (var _ in m.InvokeAsync(req, (r, ct) => Reply.Fail(new ModelException("overloaded", true, 529)), default)) { } });
        await Check.ThrowsAsync<ModelException>(async () => { await foreach (var _ in m.InvokeAsync(req, (r, ct) => Reply.Fail(new ModelException("overloaded", true, 503)), default)) { } });
        await Check.ThrowsAsync<IOException>(async () => { await foreach (var _ in m.InvokeAsync(req, (r, ct) => Reply.Fail(new IOException("connect failed")), default)) { } });
        Check.Equal(0.0, l.Spent().Today, "failed calls settle at $0, not at the reservation");
        Check.Equal(0L, Calls(h).Count(new DataQuery().Ne("costSource", "rejected")), "no phantom spend");

        // The storm itself: six more 529s, all refused-by-the-provider, none of them locking the cap.
        for (var i = 0; i < 3; i++)
        {
            var failed = false;
            try { await foreach (var _ in m.InvokeAsync(req, (r, ct) => Reply.Fail(new ModelException("overloaded", true, 529)), default)) { } }
            catch (ModelException) { failed = true; }
            Check.True(failed, "the provider failed");
        }
        Check.Equal(0.0, l.Spent().Today, "the cap is still free (the old estimate refused the second storm call)");
        Check.Equal(6L, Calls(h).Count(null));

        // A call that does answer settles from its real usage and fits the cap.
        await foreach (var _ in m.InvokeAsync(req, (r, ct) => Reply.Text("ok"), CancellationToken.None)) { }
        Check.True(l.Spent().Today > 0, "the answered call is billed");
    }

    private static async Task Swap()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var l = Create(h);
        var ctx = new TestPluginContext(h, "ledger-test");
        // A hot swap keeps the old generation in the pipeline until the new one serves: two middlewares, one call.
        var m1 = new LedgerMiddleware(l, new AgentScheduler(ctx, l));
        var m2 = new LedgerMiddleware(l, new AgentScheduler(ctx, l));
        IAsyncEnumerable<ModelStreamEvent> Provider(ModelRequest r, CancellationToken ct) => Reply.Text("ok");
        await foreach (var _ in m2.InvokeAsync(Request(), (r, ct) => m1.InvokeAsync(r, Provider, ct), CancellationToken.None)) { }
        Check.Equal(1L, Calls(h).Count(null), "one call, one reservation");
        Check.Equal(1L, Calls(h).Count(new DataQuery().Ne("costSource", "reserved")), "and settled once");
        // The other order: the first to run reserves, the other passes through either way.
        await foreach (var _ in m1.InvokeAsync(Request(), (r, ct) => m2.InvokeAsync(r, Provider, ct), CancellationToken.None)) { }
        Check.Equal(2L, Calls(h).Count(null), "the next call meters again");
    }

    private static async Task AllowParallel()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        var l = Create(h);
        var sid = h.NewSession().Id;
        var req = Request();
        // Allow takes the session update (a database transaction) and the ledger's period; Reserve the other way
        // round. Interleaved, the old order wedged the two gates across threads.
        var allow = Task.Run(() => { for (var i = 0; i < 300; i++) l.Allow(sid); });
        var reserve = Task.Run(() => { for (var i = 0; i < 300; i++) { var r = l.Reserve(req, null, null); l.Settle(r, new Usage { CostUsd = 0.00001 }, true, false); } });
        await Task.WhenAll(allow, reserve).WaitAsync(TimeSpan.FromSeconds(10));
        Check.True(Math.Abs(l.Spent().Today - 0.003) < 1e-9, $"every reservation settled ({l.Spent().Today})");
        Check.True(h.Sessions.GetSession(sid)!.Meta?["budgetAllowedFrom"] is JsonValue, "the allow landed in the session");
    }

    // The transports cut max_tokens to what the window has left beside the request, so a reservation that used the
    // caller's value alone held more than the call could ever spend (idea-begg3v).
    private static void WindowClamp()
    {
        var price = new Ledger.Price(0.000001, 0.000002, 0.000001, 0.000002, "test");
        var req = Request();
        req.MaxOutputTokens = 100;
        Check.True(Ledger.Estimate(req, price) > 0, "a small request reserves something");

        // A prompt that fills most of the window: the transport sends the room that is left, not the 16k the caller
        // asked for, and the reservation holds exactly that much output.
        req.MaxOutputTokens = 16_000;
        req.Messages = [ChatMessage.UserText(new string('w', 100_000))];
        var estimate = Ledger.Estimate(req, price);
        var input = ModelMessages.EstimateInputTokens(req);
        var room = req.Model.ContextWindow!.Value - input - Math.Max(ModelMessages.WindowMargin, req.Model.ContextWindow.Value * ModelMessages.WindowMarginShare);
        Check.True(room > 0 && room < 16_000, $"the window leaves {room} output tokens");
        Check.True(Math.Abs(estimate - (input * price.CacheRead + room * price.Output) / 1_000_000) < 1e-12,
            $"the reservation holds what the transport would send ({estimate})");
    }
}
