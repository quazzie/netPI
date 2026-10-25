using System.Text.Json.Nodes;
using NetPI.IdleCompaction;

namespace NetPI.Aux.Tests;

/// <summary>
/// The idle compaction's sweep: an open session idle for the lead share of its provider's prompt-cache TTL gets its
/// idle compaction — and nothing else does (a closed, a running, a goal-bound, a small, a non-cached or a TTL-less
/// one). One attempt per idle period, a backoff after a failure, a cancel when a run starts.
/// </summary>
public static class IdleCompactionTests
{
    /// <summary>A presence a test sets by hand.</summary>
    private sealed class Presence : ISessionPresence
    {
        public HashSet<string> Open { get; } = new(StringComparer.Ordinal);
        public void MarkOpen(string sessionId) => Open.Add(sessionId);
        public void MarkClosed(string sessionId) => Open.Remove(sessionId);
        public bool IsOpen(string sessionId) => Open.Contains(sessionId);
        public IReadOnlyCollection<string> OpenSessions() => Open;
    }

    private sealed class Env : IDisposable
    {
        public FakePluginContext Ctx { get; } = new();
        public Presence Presence { get; } = new();
        public FakeAgentRuntime Runtime { get; } = new();
        public ModelInfo Model { get; }
        public SessionInfo Session { get; }
        public IdleCompactionPlugin Plugin { get; }
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public List<JsonObject> Calls { get; } = [];
        public Func<string> CompactionResult { get; set; } = () => "Context compacted: ~50000 → ~10000 tokens (8 messages summarized).";
        public Exception? CompactionFailure { get; set; }
        public TaskCompletionSource<bool>? CompactionGate { get; set; }
        public CancellationToken? InFlightToken { get; set; }
        private double _ttl;

        public Env(double ttl = 100)
        {
            _ttl = ttl;
            Model = T.Model("m1", 100_000, "test");
            Ctx.ModelsFake.Models.Add(Model);
            Ctx.ModelsFake.DefaultModelRef = Model.Ref;
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", Model = Model.Ref, ContextTokens = 50_000 });
            Ctx.ServicesFake.Register<ISessionPresence>(Presence);
            Ctx.ServicesFake.Register<IAgentRuntime>(Runtime);
            Ctx.SettingsFake.SetQuiet("idleCompaction.ttlSeconds", JsonValue.Create($"{{\"test\": {ttl}}}"));
            Ctx.SettingsFake.SetQuiet("idleCompaction.minTokens", JsonValue.Create(5000));
            Ctx.SettingsFake.SetQuiet("idleCompaction.minWindowShare", JsonValue.Create(0.1));
            Ctx.RpcFake.RegisterFor("budget.status", (_, _) => Task.FromResult<object?>(new { exhausted = false }), "test", readOnly: true);
            Ctx.RpcFake.RegisterFor("compaction.run", CompactionAsync, "test", readOnly: false);
            Plugin = new IdleCompactionPlugin { Clock = () => Now };
            Plugin.StartAsync(Ctx, CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task<object?> CompactionAsync(RpcRequest req, CancellationToken ct)
        {
            Calls.Add(new JsonObject { ["sessionId"] = req.Str("sessionId"), ["mode"] = req.Str("mode") });
            if (CompactionGate is { } gate)
            {
                InFlightToken = ct;
                await gate.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            if (CompactionFailure is { } ex) throw ex;
            return CompactionResult();
        }

        /// <summary>The last activity: a user message and its answer (with the cache usage), <paramref name="idleFor"/> ago.</summary>
        public void Activity(TimeSpan idleFor, bool cached = true)
        {
            var u = Ctx.SessionsFake.AppendMessage(Session.Id, T.User("keep going"));
            u.CreatedAt = Now - idleFor;
            var a = Ctx.SessionsFake.AppendMessage(Session.Id, T.Assistant("done"));
            a.CreatedAt = (Now - idleFor).AddSeconds(-1);
            a.Usage = new Usage { InputTokens = 1000, CacheReadTokens = cached ? 800 : 0, OutputTokens = 100 };
        }

        public int CallCount => Calls.Count;
        public void Dispose() => Ctx.Unload();
    }

    public static void Register(TestRunner r)
    {
        r.Add("idle compaction: an open session idle for the lead share of its TTL is compacted in the idle mode", async () =>
        {
            using var env = new Env();   // ttl 100, lead 0.8: due at 80 s
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "one attempt");
            Check.Equal("idle", env.Calls[0]["mode"]?.GetValue<string>(), "the idle mode");
            Check.Equal(env.Session.Id, env.Calls[0]["sessionId"]?.GetValue<string>());
        });

        r.Add("idle compaction: before the lead share, and past the TTL, nothing happens", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(70));   // 70 < 80: the cache is not due
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "too early");

            env.Activity(TimeSpan.FromSeconds(110));  // 110 ≥ 100: the cache is already gone
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "too late: nothing to beat any more");
        });

        r.Add("idle compaction: a session no window has open is not compacted", async () =>
        {
            using var env = new Env();
            env.Activity(TimeSpan.FromSeconds(85));
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "not open: nothing to do");
        });

        r.Add("idle compaction: a running agent's session is not compacted", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Runtime.Agents.Add(new AgentInfo { Id = "agt", SessionId = env.Session.Id, Status = AgentStatus.Running });
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "the context is in use");
        });

        r.Add("idle compaction: an open goal's session is not compacted", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Session.Meta = new JsonObject { ["goal"] = new JsonObject { ["status"] = "active" } };
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "the goal's next run is about to use the context");
        });

        r.Add("idle compaction: a context below the minimum is not worth a summarizer call", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Ctx.SessionsFake.UpdateSession(env.Session.Id, s => s.ContextTokens = 4000);   // < minTokens 5000
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "too small");
        });

        r.Add("idle compaction: a provider with no TTL has no cache to beat", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Ctx.SettingsFake.SetQuiet("idleCompaction.ttlSeconds", JsonValue.Create("{\"other\": 100}"));
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "no TTL for this provider");
        });

        r.Add("idle compaction: a model that manages its own context is left alone", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Model.ManagesOwnContext = true;
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "the backend compacts its own session");
        });

        r.Add("idle compaction: a spent budget is not spent on a summarizer call", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Ctx.RpcFake.RegisterFor("budget.status", (_, _) => Task.FromResult<object?>(new { exhausted = true }), "test", readOnly: true);
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "the budget is exhausted");
        });

        r.Add("idle compaction: a chat the provider does not cache is not compacted", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85), cached: false);   // no cache reads or writes: no TTL to beat
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "the provider is not caching this chat");
        });

        r.Add("idle compaction: disabled does nothing", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.Ctx.SettingsFake.SetQuiet("idleCompaction.enabled", JsonValue.Create(false));
            await env.Plugin.TickAsync();
            Check.Equal(0, env.CallCount, "disabled");
        });

        r.Add("idle compaction: one attempt per idle period, and a new message starts a new one", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "the first attempt");
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "the same idle period is not attempted again");

            // a new message: a new idle period, not due yet
            env.Activity(TimeSpan.FromSeconds(5));
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "the new period is not due");

            // due: attempted again
            env.Now += TimeSpan.FromSeconds(80);
            env.Activity(TimeSpan.FromSeconds(85));
            await env.Plugin.TickAsync();
            Check.Equal(2, env.CallCount, "the new period is due");
        });

        r.Add("idle compaction: a failed attempt backs off, a short TTL's backoff floor blocks a fast new period", async () =>
        {
            // ttl 20, lead 0.8: due at 16 s; a failure's backoff floor (30 s) outlasts the new period's lead
            using var env = new Env(ttl: 20);
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(17));
            env.CompactionFailure = new RpcException("compaction_failed", "the summarizer is down");
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "the failed attempt");

            env.Now += TimeSpan.FromSeconds(10);
            env.Activity(TimeSpan.FromSeconds(5));   // a new period, 5 s old
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "the new period is not due yet");

            env.Now += TimeSpan.FromSeconds(12);     // the new period is due (17 s), the backoff (30 s) is not over
            env.Activity(TimeSpan.FromSeconds(17));
            await env.Plugin.TickAsync();
            Check.Equal(1, env.CallCount, "still inside the backoff");

            env.Now += TimeSpan.FromSeconds(20);     // the backoff is over
            env.Activity(TimeSpan.FromSeconds(17));
            await env.Plugin.TickAsync();
            Check.Equal(2, env.CallCount, "after the backoff, the attempt runs again");
        });

        r.Add("idle compaction: a run that starts cancels the in-flight compaction", async () =>
        {
            using var env = new Env();
            env.Presence.Open.Add(env.Session.Id);
            env.Activity(TimeSpan.FromSeconds(85));
            env.CompactionGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var tick = env.Plugin.TickAsync();
            await Wait.Until(() => env.InFlightToken is not null, "the compaction to start in flight", 5000);

            // a run starts in the session: the agent.status event cancels the in-flight compaction
            env.Ctx.Bus.Publish(new BusEvent
            {
                Type = EventTypes.AgentStatus,
                Data = new JsonObject { ["agent"] = new JsonObject { ["sessionId"] = env.Session.Id, ["status"] = "running" } },
            });
            env.CompactionGate.SetResult(true);
            await tick;
            Check.True(env.InFlightToken is { IsCancellationRequested: true }, "the in-flight compaction was cancelled with the run");
        });

        r.Add("idle compaction: the backoff is half of what is left of the TTL, clamped to 30 s … 30 min", () =>
        {
            Check.Equal(TimeSpan.FromSeconds(30), IdleCompactionEngine.Backoff(TimeSpan.FromSeconds(85), 100), "7.5 s clamps up to the floor");
            Check.Equal(TimeSpan.FromSeconds(35), IdleCompactionEngine.Backoff(TimeSpan.FromSeconds(30), 100), "half of the 70 s left");
            Check.Equal(TimeSpan.FromSeconds(45), IdleCompactionEngine.Backoff(TimeSpan.FromSeconds(10), 100), "half of the 90 s left");
            Check.Equal(TimeSpan.FromMinutes(30), IdleCompactionEngine.Backoff(TimeSpan.FromMinutes(16.67), 7200), "clamps down to the ceiling");
            Check.Equal(TimeSpan.FromSeconds(30), IdleCompactionEngine.Backoff(TimeSpan.FromSeconds(7300), 7200), "nothing left: the floor");
        });

        r.Add("idle compaction: the TTL map is a JSON map by provider, a bare number for all, and a default of Anthropic's hour", () =>
        {
            var settings = new FakeSettings();
            Check.Equal(3600, IdleCompactionEngine.Ttls(settings)["anthropic"], "the default is Anthropic's 1-hour cache");
            Check.True(IdleCompactionEngine.TtlFor(IdleCompactionEngine.Ttls(settings), "test") is null, "a provider not listed has no TTL");

            settings.SetQuiet("idleCompaction.ttlSeconds", JsonValue.Create("{\"anthropic\": 3600, \"test\": 100}"));
            var map = IdleCompactionEngine.Ttls(settings);
            Check.Equal(100, map["test"]);
            Check.Equal(3600, map["anthropic"]);
            Check.Equal(100.0, IdleCompactionEngine.TtlFor(map, "test"));

            settings.SetQuiet("idleCompaction.ttlSeconds", JsonValue.Create("120"));
            Check.Equal(120, IdleCompactionEngine.TtlFor(IdleCompactionEngine.Ttls(settings), "anyone"), "a bare number applies to every provider");

            settings.SetQuiet("idleCompaction.ttlSeconds", JsonValue.Create("not json"));
            Check.Equal(0, IdleCompactionEngine.Ttls(settings).Count, "an unreadable map is none, not a guess");
        });
    }
}
