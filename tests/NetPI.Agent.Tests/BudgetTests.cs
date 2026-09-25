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
    }

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

    private static async Task Ledger()
    {
        using var db = TestSqlite.TryCreate();
        if (db is null) { Console.WriteLine("    (no SQLite library: skipped)"); return; }
        await using var h = await TestHost.StartAsync(Priced, db: db);

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

        Check.Equal("estimated", db.Scalar<string>("SELECT cost_source FROM usage_calls WHERE session_id = @s", new { s = priced.Id }));
        Check.Equal(0.00045, Math.Round(db.Scalar<double>("SELECT cost_usd FROM usage_calls WHERE session_id = @s", new { s = priced.Id }), 8));
        Check.Equal("reported", db.Scalar<string>("SELECT cost_source FROM usage_calls WHERE session_id = @s", new { s = reported.Id }));
        Check.Equal("free", db.Scalar<string>("SELECT cost_source FROM usage_calls WHERE session_id = @s", new { s = local.Id }));

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
            return Reply.Tool("agent_spawn", new { task = "do it", model = "cloud/big" });
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
        using var db = TestSqlite.TryCreate();
        if (db is null) { Console.WriteLine("    (no SQLite library: skipped)"); return; }
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(0.01)), db: db);
        h.Catalog.Handler = (r, ct) => Costing("spent", 0.02);
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
        Check.Contains(notice.Text, "The monthly budget is spent: $0.02 of $0.01");
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
            return Reply.Tool("agent_spawn", new { task = "paid work", model = "cloud/big" });
        };
        await h.SendAsync(parent.Id, "delegate");
        await Wait.Until(() => h.Runtime.GetBySession(parent.Id)?.Children.Count == 1, "spawned");
        var child = await h.StatusAsync(h.Runtime.GetBySession(parent.Id)!.Children[0], AgentStatus.Failed);
        Check.Contains(child.Error, "budget");
    }

    private static async Task AgentsAndCap()
    {
        using var db = TestSqlite.TryCreate();
        if (db is null) { Console.WriteLine("    (no SQLite library: skipped)"); return; }
        await using var h = await TestHost.StartAsync(x =>
        {
            Priced(x);
            x.Settings.SetQuiet("budget.monthlyUsd", JsonValue.Create(50));
            x.Settings.SetQuiet("agents.big", JsonNode.Parse("""{ "model": "cloud/big", "use": "Costly: hard problems only.", "budget": { "limitUsd": 0.01 } }"""));
            x.Settings.SetQuiet("agents.small", JsonNode.Parse("""{ "model": "fake/local", "use": "Free: searches and small edits." }"""));
            x.Settings.SetQuiet("agents.solo", JsonNode.Parse("""{ "model": "fake/solo", "disabled": true }"""));
        }, db: db);

        var agents = h.Scheduler!.Snapshot().Where(p => p.Configured).OrderBy(p => p.Key).ToList();
        Check.Equal("big,small,solo", string.Join(",", agents.Select(p => p.Key)));
        Check.Equal(1, agents[0].Capacity);
        Check.Equal(3.0, agents[0].PriceInput);
        Check.False(agents[0].Free);
        Check.True(agents[1].Free);
        Check.Equal(0.01, agents[0].DailyLimitUsd);

        // the orchestrator asks for the agents, then delegates without an agent and on a switched-off one (refused, the
        // error lists them), then on one
        string? listing = null, refused = null, off = null;
        var step = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Costing("big report", 0.02);
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
                4 => Reply.Tool("agent_spawn", new { task = "hard problem", agent = "big" }),
                _ => Reply.Text("waiting for it"),
            };
        };
        var s = h.NewSession(); // on fake/local = the agent "small"
        await h.SendAsync(s.Id, "solve it");
        await Wait.Until(() => h.Messages(s.Id).Any(m => m.Text == "thanks"), "report back");
        await h.IdleAsync(s.Id);

        Check.Contains(listing, "Budget: $0.00 of $50 this month (0 %), $0.00 today. Free models don't count.");
        Check.Contains(listing, "Agents (pass the id to agent_spawn):");
        Check.Contains(listing, "- small · fake/local · 1/2 busy · local · free · 100k ctx · \"Free: searches and small edits.\"");
        Check.Contains(listing, "- big · cloud/big · 0/1 busy · $3 / $15 per Mtok in/out · $0.00 today (cap $0.01) · 200k ctx · \"Costly: hard problems only.\"");
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
        Check.Equal(0.02, Math.Round(h.Scheduler!.Snapshot().Single(p => p.Key == "big").SpentTodayUsd, 8));

        // the agent's daily cap ($0.01) is spent: the next call on it is refused, other agents go on
        var direct = h.NewSession(model: "cloud/big");
        await h.SendAsync(direct.Id, "more");
        await h.IdleAsync(direct.Id);
        Check.Contains(h.Messages(direct.Id)[^1].Text, "The agent big has spent its $0.01 for today ($0.02).");
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
