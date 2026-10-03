using System.Text.Json.Nodes;
using NetPI.Schedules;

namespace NetPI.Aux.Tests;

/// <summary>
/// Scheduled runs (idea-1y1174): the cadence arithmetic, a due run starting a real chat through the runtime, the checks that
/// skip a run without leaving a chat behind, missed runs after downtime, the persisted state across a restart, and the
/// claim that keeps two ticks (or two plugin generations) from starting the same run twice.
/// </summary>
public static class SchedulesTests
{
    /// <summary>A clock the test moves.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>A runtime that records what it was sent; <see cref="Fail"/> makes the next send throw.</summary>
    private sealed class RecordingRuntime : IAgentRuntime
    {
        public List<(string SessionId, UserInput Input)> Sent { get; } = [];
        public List<AgentInfo> Agents { get; } = [];
        public bool Fail { get; set; }
        public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => Agents;
        public AgentInfo? Get(string agentId) => Agents.FirstOrDefault(a => a.Id == agentId);
        public AgentInfo? GetBySession(string sessionId) => Agents.LastOrDefault(a => a.SessionId == sessionId);
        public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("the model is not loaded");
            lock (Sent) Sent.Add((sessionId, input));
            var agent = new AgentInfo { Id = Ids.New("agt"), SessionId = sessionId, Status = AgentStatus.Running };
            lock (Agents) Agents.Add(agent);
            return Task.FromResult(agent);
        }
        public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> AbortAsync(string agentOrSessionId) => Task.FromResult(false);
        public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => [];
        public bool RemoveQueued(string sessionId, string inputId) => false;
    }

    private sealed class Env
    {
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero));
        public FakePluginContext Ctx { get; private set; }
        public RecordingRuntime Runtime { get; } = new();
        public SchedulesPlugin Plugin { get; private set; } = null!;
        public ProjectInfo Project { get; }
        private readonly string _home = T.TempDir("schedules-home");

        public Env()
        {
            Ctx = new FakePluginContext(_home, "netpi.schedules");
            Project = Ctx.SessionsFake.CreateProject("demo", _home);
            Ctx.ServicesFake.Register<IAgentRuntime>(Runtime);
        }

        public async Task<Env> StartAsync()
        {
            Plugin = new SchedulesPlugin(Time, loop: false);
            await Plugin.StartAsync(Ctx, CancellationToken.None);
            return this;
        }

        /// <summary>A host restart: the store closes, a new context opens the same home, a new plugin starts on it.</summary>
        public async Task RestartAsync()
        {
            var projects = Ctx.SessionsFake.Projects.ToList();
            Ctx.Unload();
            Ctx = new FakePluginContext(_home, "netpi.schedules");
            Ctx.SessionsFake.Projects.AddRange(projects);
            Ctx.ServicesFake.Register<IAgentRuntime>(Runtime);
            await StartAsync();
        }

        public Task<int> Tick() => Plugin.Runner!.TickAsync(CancellationToken.None);

        public async Task<JsonObject> Add(object cadence, string prompt = "Summarise yesterday's commits", object? extra = null)
        {
            var p = new JsonObject { ["prompt"] = prompt, ["cadence"] = NetPiJson.ToNode(cadence), ["projectId"] = Project.Id };
            if (extra is not null) foreach (var kv in NetPiJson.ToNode(extra)!.AsObject()) p[kv.Key] = kv.Value?.DeepClone();
            return (await Ctx.RpcFake.CallAsync("schedules.add", p))!.AsObject();
        }

        public async Task<JsonObject> Get(string id) => (await Ctx.RpcFake.CallAsync("schedules.get", new { id }))!.AsObject();

        public async Task<JsonArray> History(string id) => (await Ctx.RpcFake.CallAsync("schedules.history", new { id }))!.AsArray();
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static void Register(TestRunner r)
    {
        r.Add("schedules: the cadences' next runs — every counts from the run, daily from the wall clock, across a DST gap", () =>
        {
            var now = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
            var every = Cadence.Parse(JsonNode.Parse("""{ "kind": "every", "minutes": 90 }"""), 5);
            Check.Equal(now.AddMinutes(90), every.First(now));
            Check.Equal(now.AddHours(10).AddMinutes(90), every.After(now.AddHours(10)), "from the late run, not from the old due time: no backlog");

            var daily = Cadence.Parse(JsonNode.Parse("""{ "kind": "daily", "time": "07:30", "tz": "UTC" }"""), 5);
            Check.Equal(new DateTimeOffset(2026, 10, 4, 7, 30, 0, TimeSpan.Zero), daily.First(now), "07:30 has passed today: tomorrow");
            Check.Equal(new DateTimeOffset(2026, 10, 3, 7, 30, 0, TimeSpan.Zero), daily.First(now.AddHours(-1)), "before 07:30: today");

            // 2026-03-29 the clocks in Stockholm go from 02:00 to 03:00: a 02:30 run that day is at 03:30 local (01:30 UTC)
            var gap = Cadence.Parse(JsonNode.Parse("""{ "kind": "daily", "time": "02:30", "tz": "Europe/Stockholm" }"""), 5);
            var spring = gap.First(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero));
            Check.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), spring, "the skipped half hour moves an hour on");
            var summer = gap.First(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
            Check.Equal(new DateTimeOffset(2026, 6, 2, 0, 30, 0, TimeSpan.Zero), summer, "02:30 CEST is 00:30 UTC");

            var once = Cadence.Parse(JsonNode.Parse("""{ "kind": "once", "at": "2026-10-04T06:00:00+02:00" }"""), 5);
            Check.Equal(new DateTimeOffset(2026, 10, 4, 4, 0, 0, TimeSpan.Zero), once.First(now));
            Check.Equal(null, once.After(now), "a once has no run after its run");

            foreach (var (bad, what) in new[]
            {
                ("""{ "kind": "every", "minutes": 2 }""", "minutes"), ("""{ "kind": "daily", "time": "25:00" }""", "HH:mm"),
                ("""{ "kind": "daily", "time": "07:00", "tz": "Mars/Olympus" }""", "unknown time zone"), ("""{ "kind": "once" }""", "ISO 8601"),
                ("""{ "kind": "cron", "expr": "* * * * *" }""", "once, every or daily"),
            })
            {
                var ex = Check.Throws<RpcException>(() => Cadence.Parse(JsonNode.Parse(bad), 5));
                Check.Equal("bad_request", ex.Code);
                Check.Contains(ex.Message, what);
            }
        });

        r.Add("schedules: a due run starts a fresh chat on the project through the runtime, once, and the next run moves on", async () =>
        {
            var env = await new Env().StartAsync();
            var s = await env.Add(new { kind = "every", minutes = 30 }, extra: new { name = "Digest" });
            var id = S(s["id"])!;
            Check.Equal("every 30 min", S(s["when"]));
            Check.Equal(0, await env.Tick(), "not due yet");

            env.Time.Advance(TimeSpan.FromMinutes(30));
            Check.Equal(1, await env.Tick());
            Check.Equal(0, await env.Tick(), "the claim moved the next run on: a second tick starts nothing");

            var (sessionId, input) = env.Runtime.Sent.Single();
            var chat = env.Ctx.SessionsFake.GetSession(sessionId)!;
            Check.Equal(env.Project.Id, chat.ProjectId);
            Check.Equal("Scheduled: Digest", chat.Title);
            Check.Equal(id, S(chat.Meta?["schedule"]), "the chat says which schedule started it");
            Check.Equal("Summarise yesterday's commits", input.Text);
            Check.Equal("system", input.Source);

            var after = await env.Get(id);
            Check.Equal("ran", S(after["lastStatus"]));
            Check.Equal(sessionId, S(after["lastSessionId"]));
            Check.Equal(ScheduleStoreIso(env.Time.Now.AddMinutes(30)), S(after["nextRunAt"]));
            var history = await env.History(id);
            Check.Equal(1, history.Count);
            Check.Equal(sessionId, S(history[0]!["sessionId"]));
            Check.True(env.Ctx.Bus.Events.Any(e => e.Type == "schedules.ran"), "schedules.ran was published");
        });

        r.Add("schedules: a once runs once and is finished; it cannot be resumed without a new cadence", async () =>
        {
            var env = await new Env().StartAsync();
            var s = await env.Add(new { kind = "once", at = env.Time.Now.AddHours(1).ToString("O") });
            var id = S(s["id"])!;
            env.Time.Advance(TimeSpan.FromHours(2));
            Check.Equal(1, await env.Tick());
            var done = await env.Get(id);
            Check.False(done["enabled"]!.GetValue<bool>());
            Check.True(done["finishedAt"] is not null);
            Check.True(done["nextRunAt"] is null);
            env.Time.Advance(TimeSpan.FromDays(1));
            Check.Equal(0, await env.Tick(), "a finished once never runs again");

            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("schedules.update", new { id, enabled = true }));
            Check.Contains(ex.Message, "new cadence");
            var again = (await env.Ctx.RpcFake.CallAsync("schedules.update", new { id, cadence = new { kind = "once", at = env.Time.Now.AddMinutes(10).ToString("O") } }))!;
            Check.True(again["enabled"]!.GetValue<bool>(), "a new cadence re-arms it");

            var past = await Check.ThrowsAsync<RpcException>(() => env.Add(new { kind = "once", at = env.Time.Now.AddMinutes(-1).ToString("O") }));
            Check.Equal("bad_request", past.Code);
        });

        r.Add("schedules: after downtime a run within the grace runs once, a later one is recorded as missed", async () =>
        {
            var env = await new Env().StartAsync();
            var hourly = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            var once = S((await env.Add(new { kind = "once", at = env.Time.Now.AddHours(1).ToString("O") }))["id"])!;

            env.Time.Advance(TimeSpan.FromHours(30));   // the host was down for a day and a bit
            Check.Equal(0, await env.Tick(), "both were due more than 24 h ago: neither runs");
            Check.Equal("missed", S((await env.Get(once))["lastStatus"]));
            Check.True((await env.Get(once))["finishedAt"] is not null, "a missed once is finished");
            var h = await env.Get(hourly);
            Check.Equal("missed", S(h["lastStatus"]));
            Check.Equal(ScheduleStoreIso(env.Time.Now.AddMinutes(60)), S(h["nextRunAt"]), "the interval resumes from now, without catch-up runs");

            env.Time.Advance(TimeSpan.FromHours(3));    // 2 h late now: within the grace
            Check.Equal(1, await env.Tick(), "a late run within the grace runs once");
            Check.Equal(1, env.Runtime.Sent.Count, "once, not one per hour it slept through");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count(s => S(s.Meta?["schedule"]) == once), "the missed once made no chat");
        });

        r.Add("schedules: a run is skipped without a chat when its project is gone, its previous run is going, the agent is out or the budget is spent", async () =>
        {
            var env = await new Env().StartAsync();
            var agents = new JsonArray
            {
                new JsonObject { ["key"] = "local", ["available"] = false, ["unavailable"] = "the model is not loaded", ["free"] = true },
                new JsonObject { ["key"] = "paid", ["available"] = true, ["free"] = false },
                new JsonObject { ["key"] = "home", ["available"] = true, ["free"] = true },
            };
            var exhausted = true;
            env.Ctx.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(agents.DeepClone()));
            env.Ctx.Rpc.Register("budget.status", (_, _) => Task.FromResult<object?>(new JsonObject { ["exhausted"] = exhausted }));
            env.Ctx.Rpc.Register("agents.use", (_, _) => Task.FromResult<object?>(null));

            async Task<JsonNode> Run(string id) => (await env.Ctx.RpcFake.CallAsync("schedules.run", new { id }))!;
            var local = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { agent = "local" }))["id"])!;
            var paid = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { agent = "paid" }))["id"])!;
            var home = S((await env.Add(new { kind = "every", minutes = 60 }, extra: new { agent = "home" }))["id"])!;

            var r1 = await Run(local);
            Check.Equal("skipped", S(r1["status"]));
            Check.Contains(S(r1["reason"]), "not loaded");
            var r2 = await Run(paid);
            Check.Equal("skipped", S(r2["status"]));
            Check.Contains(S(r2["reason"]), "budget");
            Check.Equal("ran", S((await Run(home))["status"]), "a free agent runs on a spent budget");
            exhausted = false;
            Check.Equal("ran", S((await Run(paid))["status"]), "the budget back: the paid agent runs");

            var busy = await Run(paid);
            Check.Equal("skipped", S(busy["status"]), "the previous run of the same schedule is still going");
            Check.Contains(S(busy["reason"]), "still going");
            foreach (var a in env.Runtime.Agents) a.Status = AgentStatus.Completed;
            Check.Equal("ran", S((await Run(paid))["status"]), "finished: the next one starts");

            env.Ctx.SessionsFake.Projects.Clear();
            var gone = await Run(home);
            Check.Equal("skipped", S(gone["status"]));
            Check.Contains(S(gone["reason"]), "no longer exists");

            Check.Equal(3, env.Ctx.SessionsFake.Sessions.Count, "a skipped run left no chat: only the three that ran made one");
            await Check.ThrowsAsync<RpcException>(() => env.Add(new { kind = "every", minutes = 60 }, extra: new { agent = "nobody" }));
        });

        r.Add("schedules: a run that cannot start is failed and its chat is removed; the daily cap skips", async () =>
        {
            var env = await new Env().StartAsync();
            var id = S((await env.Add(new { kind = "every", minutes = 60 }))["id"])!;
            env.Runtime.Fail = true;
            var failed = (await env.Ctx.RpcFake.CallAsync("schedules.run", new { id }))!;
            Check.Equal("failed", S(failed["status"]));
            Check.Contains(S(failed["reason"]), "not loaded");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "the chat the failed run made is gone");

            env.Runtime.Fail = false;
            env.Ctx.SettingsFake.Set("schedules.maxRunsPerDay", 2);
            for (var i = 0; i < 2; i++)
            {
                Check.Equal("ran", S((await env.Ctx.RpcFake.CallAsync("schedules.run", new { id }))!["status"]));
                foreach (var a in env.Runtime.Agents) a.Status = AgentStatus.Completed;
            }
            var capped = (await env.Ctx.RpcFake.CallAsync("schedules.run", new { id }))!;
            Check.Equal("skipped", S(capped["status"]));
            Check.Contains(S(capped["reason"]), "maxRunsPerDay");
            env.Time.Advance(TimeSpan.FromHours(25));
            Check.Equal("ran", S((await env.Ctx.RpcFake.CallAsync("schedules.run", new { id }))!["status"]), "a day later the cap has room again");
        });

        r.Add("schedules: the schedules survive a restart, pausing keeps them, and two generations ticking at once start a run once", async () =>
        {
            var env = await new Env().StartAsync();
            var id = S((await env.Add(new { kind = "daily", time = "09:00", tz = "UTC" }))["id"])!;
            await env.RestartAsync();
            var back = await env.Get(id);
            Check.Equal("2026-10-03T09:00:00.0000000+00:00", S(back["nextRunAt"]), "the next run is the persisted one");

            await env.Ctx.RpcFake.CallAsync("schedules.update", new { id, enabled = false });
            env.Time.Advance(TimeSpan.FromHours(2));
            Check.Equal(0, await env.Tick(), "a paused schedule does not run");
            var resumed = (await env.Ctx.RpcFake.CallAsync("schedules.update", new { id, enabled = true }))!;
            Check.Equal("2026-10-04T09:00:00.0000000+00:00", S(resumed["nextRunAt"]), "resuming counts from now: the run it slept through is not owed");

            // a hot reload runs two generations over the same data for a moment: the claim is one transaction
            env.Time.Advance(TimeSpan.FromDays(1));
            var second = new SchedulesPlugin(env.Time, loop: false);
            await second.StartAsync(env.Ctx, CancellationToken.None);
            var started = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => (i % 2 == 0 ? env.Plugin : second).Runner!.TickAsync(CancellationToken.None))));
            Check.Equal(1, started.Sum(), "one run, whichever generation claimed it");

            Check.Equal(true, await env.Ctx.RpcFake.InvokeAsync("schedules.delete", new { id }));
            Check.Equal(0, (await env.History(id)).Count, "its history went with it");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "the chat its run started stays");
        });

        r.Add("schedules: requests are checked against the declared parameters", async () =>
        {
            var env = await new Env().StartAsync();
            var unknown = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("schedules.add", new { prompt = "x", cadence = new { kind = "every", minutes = 60 }, every = 5 }));
            Check.Contains(unknown.Message, "unknown parameter 'every'");
            var shortInterval = await Check.ThrowsAsync<RpcException>(() => env.Add(new { kind = "every", minutes = 1 }));
            Check.Contains(shortInterval.Message, "at least 5");
            var noProject = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("schedules.add", new { prompt = "x", cadence = new { kind = "every", minutes = 60 }, projectId = "prj_nope" }));
            Check.Equal("not_found", noProject.Code);
            var byName = await env.Ctx.RpcFake.CallAsync("schedules.add", new { prompt = "x", cadence = new { kind = "every", minutes = 60 }, projectId = "demo" });
            Check.Equal(env.Project.Id, S(byName!["projectId"]), "a project may be named");
        });
    }

    private static string ScheduleStoreIso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
