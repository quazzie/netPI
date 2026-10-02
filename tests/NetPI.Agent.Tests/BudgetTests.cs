using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

/// <summary>The ledger of model calls with their cost, the budget, and the agents (the agents plugin).</summary>
public static class BudgetTests
{
    public static void Register(TestRunner t)
    {
        t.Add("ledger: every call with its cost (reported, from the price, free for local); a chat's cost with its subagents", Ledger);
        t.Add("budget: a spent monthly budget stops paid calls, not local ones; \"ask\" lets a chat go over and continues it", MonthlyBudget);
        t.Add("agents: agent_choices shows the budget, state, price, spend and note; agent_spawn needs an active agent; an agent's daily cap", AgentsAndCap);
        t.Add("budget: the period starts on budget.resetDay", Period);
        t.Add("budget: settings changes refresh usage without a model call, including file reloads", SettingsRefresh);
        t.Add("ledger: the in-flight usage.changed does not outlive the plugin's stop", StopMidNotification);
    }

    /// <summary>The ledger's call collection, read by the tests the way the migration tool will (the same declaration the ledger makes).</summary>
    private static IDataCollection Calls(TestHost h) =>
        h.Storage.Plugins.For("netpi.agents").Collection("usage_calls", new CollectionSpec()
            .Integer("ts").Text("day").Text("sessionId").Text("rootSessionId").Text("lane").Real("costUsd").Text("costSource"));

    private static DataDoc CallOf(TestHost h, string sessionId) =>
        Calls(h).Find(new DataQuery().Eq("sessionId", sessionId)).Single();

    /// <summary>cloud/big costs $3 / $15 per million tokens (the catalog's pricing, as OpenRouter lists it).</summary>
    private static void Priced(TestHost h) =>
        h.Catalog.Cached.First(m => m.Ref == "cloud/big").Extra = new JsonObject
        {
            ["pricing"] = new JsonObject { ["prompt"] = "0.000003", ["completion"] = "0.000015" },
        };

    /// <summary>A text reply whose usage carries a cost the provider reported.</summary>
    private static IAsyncEnumerable<ModelStreamEvent> Costing(string text, double costUsd)
    {
        var m = Reply.Message(new TextPart { Text = text });
        m.Usage!.CostUsd = costUsd;
        return Reply.Stream(m);
    }

    private static JsonNode Summary(TestHost h) => h.Rpc.CallAsync("usage.summary").GetAwaiter().GetResult()!;

    private static double Num(JsonNode? n) => double.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task SettingsRefresh()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents);
        await h.Bus.DrainAsync();

        async Task<JsonObject> Changed(Action edit)
        {
            var before = h.Bus.OfType("usage.changed").Count;
            edit();
            await h.Bus.DrainAsync();
            var events = h.Bus.OfType("usage.changed");
            Check.Equal(before + 1, events.Count, "one fresh budget event, without waiting for recorded usage");
            var status = FakeBus.Data(events[^1]);
            Check.Equal((await h.Rpc.CallAsync("budget.status"))!.ToJsonString(), status.ToJsonString(), "the event is the current budget snapshot");
            return status;
        }

        var status = await Changed(() => h.Settings.Set("budget.monthlyUsd", JsonValue.Create(50.0)));
        Check.Equal(50.0, Num(status["monthlyUsd"]));
        status = await Changed(() => h.Settings.Set("budget.dailyUsd", JsonValue.Create(5.0)));
        Check.Equal(5.0, Num(status["dailyUsd"]));
        status = await Changed(() => h.Settings.Set("budget.monthlyUsd", null));
        Check.True(status["monthlyUsd"] is null, "removing a cap is visible too");

        status = await Changed(() => h.Settings.Set("budget", JsonNode.Parse("""{ "monthlyUsd": 25, "warnPercent": 60, "resetDay": 15, "onLimit": "ask" }""")));
        Check.Equal(25.0, Num(status["monthlyUsd"]));
        Check.Equal(60, status["warnPercent"]!.GetValue<int>());
        Check.Equal(15, status["resetDay"]!.GetValue<int>());
        Check.Equal("ask", status["onLimit"]!.GetValue<string>());
        Check.True(status["dailyUsd"] is null);

        // A whole-file reload/replacement carries no path: budget changes must still reach an already open tab.
        status = await Changed(() => h.Settings.Replace(JsonNode.Parse("""{ "budget": { "monthlyUsd": 75, "dailyUsd": 7 } }""")!.AsObject()));
        Check.Equal(75.0, Num(status["monthlyUsd"]));
        Check.Equal(7.0, Num(status["dailyUsd"]));
        Check.Equal(0, h.Catalog.Calls, "no model activity was needed to publish any refresh");

        var count = h.Bus.OfType("usage.changed").Count;
        h.Settings.Set("models.localSlots", JsonValue.Create(3));
        await h.Bus.DrainAsync();
        Check.Equal(count, h.Bus.OfType("usage.changed").Count, "an unrelated setting does not refresh usage");
        await h.StopPluginAsync("netpi.agents");
        h.Settings.Set("budget.monthlyUsd", JsonValue.Create(100));
        await h.Bus.DrainAsync();
        Check.Equal(count, h.Bus.OfType("usage.changed").Count, "unloading removes the settings listener");
    }

    private static async Task StopMidNotification()
    {
        // the usage ledger publishes usage.changed from a continuation nothing awaits: it must not touch the store
        // after the plugin is gone (an access violation in the suite, a torn-down store in a reload) (idea-sx4xxx)
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var ctx = new TestPluginContext(h, "ledger-test");
        var l = new Ledger(ctx) { ChangeDelayMs = 100 };
        l.Initialize();
        ModelRequest Req() => new() { Model = new ModelInfo { Provider = "fake", Id = "local", IsLocal = true }, Messages = [ChatMessage.UserText("hi")] };
        void Record() => l.RecordCall(Req(), new ChatMessage { Usage = new Usage { InputTokens = 1, OutputTokens = 1 } }, null, null);

        // while alive: the two calls coalesce into one debounced notification
        Record();
        Record();
        await Wait.Until(() => h.Bus.OfType("usage.changed").Count == 1, "the debounce publishes");

        // a change scheduled at the moment of the stop: the plugin unloads before the debounce fires
        Record();
        l.Stop();
        ctx.Unload();
        await Task.Delay(400);
        Check.Equal(1, h.Bus.OfType("usage.changed").Count, "the in-flight usage.changed does not outlive the plugin");
    }

    private static async Task Ledger()
    {
        await using var h = await TestHost.StartAsync(Priced);

        // 1. estimated from the price: 100 input + 10 output tokens at $3 / $15 per Mtok = $0.00045
        var priced = h.NewSession(model: "cloud/big");
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        await h.SendAsync(priced.Id, "hi");
        await h.IdleAsync(priced.Id);

        // 2. reported by the provider
        var reported = h.NewSession(model: "cloud/big");
        h.Catalog.Handler = (r, ct) => Costing("ok", 0.02);
        await h.SendAsync(reported.Id, "hi");
        await h.IdleAsync(reported.Id);

        // 3. local: free
        var local = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        await h.SendAsync(local.Id, "hi");
        await h.IdleAsync(local.Id);

        Check.Equal("estimated", CallOf(h, priced.Id).Doc["costSource"]?.GetValue<string>());
        Check.Equal(0.00045, Math.Round(CallOf(h, priced.Id).Doc["costUsd"]!.GetValue<double>(), 8));
        Check.Equal("reported", CallOf(h, reported.Id).Doc["costSource"]?.GetValue<string>());
        Check.Equal("free", CallOf(h, local.Id).Doc["costSource"]?.GetValue<string>());

        var summary = Summary(h);
        Check.Equal(0.02045, Math.Round(Num(summary["budget"]!["spentUsd"]), 8));
        var models = (JsonArray)summary["models"]!;
        Check.Equal(0.02045, Math.Round(Num(models.Single(m => (string?)m!["model"] == "big")!["costUsd"]), 8));
        Check.Equal(2L, models.Single(m => (string?)m!["model"] == "big")!["calls"]!.GetValue<long>());
        Check.Equal(1L, ((JsonArray)summary["providers"]!).Single(p => (string?)p!["provider"] == "fake")!["calls"]!.GetValue<long>(), "tokens per day still counted");

        // a chat's cost includes its subagents
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Costing("child report", 0.5);
            var last = r.Messages[^1];
            if (last.Role == MessageRole.Tool || last.Text.Contains("<agent-result")) return Reply.Text("done");
            return Reply.Tool("agent_spawn", new { task = "do it", model = "cloud/big", background = true });
        };
        await h.SendAsync(parent.Id, "delegate");
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Role == MessageRole.Assistant && m.Text == "done" && h.Messages(parent.Id).Any(x => x.MetaString("kind") == "agent-result")), "report back");
        await h.IdleAsync(parent.Id);
        var cost = await h.Rpc.CallAsync("usage.session", new { sessionId = parent.Id });
        Check.Equal(0.0, Num(cost!["costUsd"]), "the parent itself ran on the local model");
        Check.Equal(0.5, Num(cost["withSubagentsUsd"]));
    }

    private static async Task MonthlyBudget()
    {
        await using var h = await TestHost.StartAsync(x => { Priced(x); x.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(1.00)); });
        h.Catalog.Handler = (r, ct) => Costing("spent", 2.00);
        var s = h.NewSession(model: "cloud/big");
        await h.SendAsync(s.Id, "spend it");
        await h.IdleAsync(s.Id);
        Check.True(Summary(h)["budget"]!["exhausted"]!.GetValue<bool>(), "spent");

        // stop (default): the next paid call is refused, with a clear notice and no call made
        var calls = h.Catalog.Calls;
        await h.SendAsync(s.Id, "more");
        var a = await h.IdleAsync(s.Id);
        Check.Equal(calls, h.Catalog.Calls, "no model call");
        var notice = h.Messages(s.Id)[^1];
        Check.Equal("budget", notice.MetaString("kind"));
        Check.Contains(notice.Text, "The monthly budget is spent: $2 of $1");
        Check.Contains(notice.Text, "Raise the budget in Settings");
        Check.False(notice.Meta!["canOverride"]!.GetValue<bool>());
        Check.Contains(a.Error, "budget");

        // local models are never stopped
        var local = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Text("local ok");
        await h.SendAsync(local.Id, "hi");
        await h.IdleAsync(local.Id);
        Check.Equal("local ok", h.Messages(local.Id)[^1].Text);

        // ask: the user's chat may go over; budget.allow continues it
        h.Settings.Set("budget.onLimit", JsonValue.Create("ask"));
        await h.SendAsync(s.Id, "again");
        await h.IdleAsync(s.Id);
        notice = h.Messages(s.Id)[^1];
        Check.True(notice.Meta!["canOverride"]!.GetValue<bool>(), "the chat can be allowed");
        Check.Contains(notice.Text, "You can let this chat go over");
        h.Catalog.Handler = (r, ct) => Reply.Text("continued");
        await h.Rpc.CallAsync("budget.allow", new { sessionId = s.Id });
        await Wait.Until(() => h.Messages(s.Id)[^1].Text == "continued", "the chat continued");
        Check.True(h.Messages(s.Id).Any(m => m.MetaString("kind") == "budget" && m.Text.Contains("let this chat go over the budget")), "the model is told");

        // a subagent on a paid model still stops
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Reply.Text("child");
            var last = r.Messages[^1];
            if (last.Role == MessageRole.Tool || last.Text.Contains("<agent-result")) return Reply.Text("parent done");
            return Reply.Tool("agent_spawn", new { task = "paid work", model = "cloud/big", background = true });
        };
        await h.SendAsync(parent.Id, "delegate");
        await Wait.Until(() => h.Runtime.GetBySession(parent.Id)?.Children.Count == 1, "spawned");
        var child = await h.StatusAsync(h.Runtime.GetBySession(parent.Id)!.Children[0], AgentStatus.Failed);
        Check.Contains(child.Error, "budget");
    }

    private static async Task AgentsAndCap()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            Priced(x);
            x.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(50));
            x.Settings.SetQuiet("agents.big", JsonNode.Parse("""{ "model": "cloud/big", "use": "Costly: hard problems only.", "budget": { "limitUsd": 1.00 } }"""));
            x.Settings.SetQuiet("agents.small", JsonNode.Parse("""{ "model": "fake/local", "use": "Free: searches and small edits." }"""));
            x.Settings.SetQuiet("agents.solo", JsonNode.Parse("""{ "model": "fake/solo", "disabled": true }"""));
        });

        var agents = h.Scheduler!.Snapshot().Where(p => p.Configured).OrderBy(p => p.Key).ToList();
        Check.Equal("big,small,solo", string.Join(",", agents.Select(p => p.Key)));
        Check.Equal(1, agents[0].Capacity);
        Check.Equal(3.0, agents[0].PriceInput);
        Check.False(agents[0].Free);
        Check.True(agents[1].Free);
        Check.Equal(1.00, agents[0].DailyLimitUsd);

        // the orchestrator asks for the agents, then delegates without an agent and on a switched-off one (refused, the
        // error lists them), then on one
        string? listing = null, refused = null, off = null;
        var step = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Costing("big report", 2.00);
            var last = r.Messages[^1];
            if (last.Role == MessageRole.Tool)
            {
                var result = last.ToolResults.Single();
                if (result.Name == "agent_choices") listing = result.Content;
                if (result.Name == "agent_spawn" && result.IsError) { if (refused is null) refused = result.Content; else off = result.Content; }
            }
            if (last.Text.Contains("<agent-result")) return Reply.Text("thanks");
            return Interlocked.Increment(ref step) switch
            {
                1 => Reply.Tool("agent_choices"),
                2 => Reply.Tool("agent_spawn", new { task = "hard problem" }),
                3 => Reply.Tool("agent_spawn", new { task = "hard problem", agent = "solo" }),
                4 => Reply.Tool("agent_spawn", new { task = "hard problem", agent = "big", background = true }),
                _ => Reply.Text("waiting for it"),
            };
        };
        var s = h.NewSession(); // on fake/local = the agent "small"
        await h.SendAsync(s.Id, "solve it");
        await Wait.Until(() => h.Messages(s.Id).Any(m => m.Text == "thanks"), "report back");
        await h.IdleAsync(s.Id);

        Check.Contains(listing, "Budget: $0.00 of $50 this month (0 %), $0.00 today. Free models don't count.");
        Check.Contains(listing, "Agents (pass the id to agent_spawn, or \"any\" for the first one with a free instance):");
        Check.Contains(listing, "- small · fake/local · 1/2 busy (one is you: free for your subagents while you wait) · local · free · 100k ctx · \"Free: searches and small edits.\"");
        Check.Contains(listing, "- big · cloud/big · 0/1 busy · $3 / $15 per Mtok in/out · $0.00 today (cap $1) · 200k ctx · \"Costly: hard problems only.\"");
        Check.Contains(listing, "- solo · fake/solo · 0/1 busy · NOT ACTIVE: switched off by the user · local · free · 50k ctx");
        Check.True(listing!.IndexOf("- small", StringComparison.Ordinal) < listing.IndexOf("- big", StringComparison.Ordinal), "cheapest first");
        Check.True(listing.IndexOf("- big", StringComparison.Ordinal) < listing.IndexOf("- solo", StringComparison.Ordinal), "active first");
        Check.Contains(listing, "You run on the agent small.");
        Check.Contains(refused, "agent_spawn needs an agent. The agents:");
        Check.Contains(refused, "- big · cloud/big · 0/1 busy · $3 / $15 per Mtok · \"Costly: hard problems only.\"");
        Check.Contains(refused, "- solo · fake/solo · not active (switched off) · free");
        Check.Contains(off, "The agent \"solo\" can't take work now: the user switched it off.");
        var child = h.Runtime.List().Single(a => a.IsSubagent);
        Check.Equal("cloud/big", child.Model);
        Check.Equal("big", child.Agent);
        Check.Equal(2.00, Math.Round(h.Scheduler!.Snapshot().Single(p => p.Key == "big").SpentTodayUsd, 8));

        // the agent's daily cap ($1) is spent: the next call on it is refused, other agents go on
        var direct = h.NewSession(model: "cloud/big");
        await h.SendAsync(direct.Id, "more");
        await h.IdleAsync(direct.Id);
        Check.Contains(h.Messages(direct.Id)[^1].Text, "The agent big has spent its $1 for today ($2).");
    }

    private static void Period()
    {
        static string P(int y, int m, int d, int reset)
        {
            var (start, end) = NetPI.Agents.Ledger.Period(new DateTime(y, m, d, 12, 0, 0), reset);
            return $"{start:yyyy-MM-dd}..{end:yyyy-MM-dd}";
        }
        Check.Equal("2026-09-01..2026-10-01", P(2026, 9, 24, 1));
        Check.Equal("2026-08-15..2026-09-15", P(2026, 9, 10, 15));
        Check.Equal("2026-09-15..2026-10-15", P(2026, 9, 15, 15));
        Check.Equal("2025-12-28..2026-01-28", P(2026, 1, 3, 28));
    }
}
