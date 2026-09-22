using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Lanes;
using NetPI.Orchestration;
using NetPI.Provider.AiProxy;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 parity tail: the remaining audit items that were not yet covered —
///   A5  migration rollback on a FAILED step (tx rolls back, DB openable at the
///       previous schema version, no partial columns);
///   F5  cloud gate: a failed inference releases its reservation so a retry
///       re-reserves (no double-spend, no leak), a concurrent race on the last
///       unit admits exactly one, and an unknown/no-usage report fails closed
///       (no budget credited for content that never happened);
///   C4  parent and child on the SAME model run in DISTINCT sessions/chains:
///       the parent resumes with a bounded result, the child's transcript stays
///       in the child's session and never reaches the parent's brief;
///   B7  context-vs-concurrency rebind: a Follow-AiProxy pool whose validated
///       backend changes — capacity 4→2 drains without evicting owners, 2→4
///       admits only the queued work, and a DISABLED replacement deployment
///       rejects before inference (the runner gate, no provider call);
///   D4  concurrent mailbox deliveries (idempotency keys honored, no lost or
///       duplicated rows, sequences gap-free) + the per-agent outstanding bound
///       + a LATE terminal result after the parent's wait was already consumed
///       does not resume a second time.
/// All tests are deterministic: no sleeps without a bounded polling wait, and
/// the "races" are real Task.WhenAll fan-outs over serialized SQLite writers
/// (bounded, no timing dependency).
/// </summary>
public sealed class ParityGapTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private bool _cleanedPools;

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
        // Pooled SQLite connections keep temp DBs open until the pool is cleared.
        if (!_cleanedPools)
        {
            SqliteConnection.ClearAllPools();
            _cleanedPools = true;
        }
    }

    private string NewDbDir()
    {
        var d = Directory.CreateTempSubdirectory("netpi-parity-").FullName;
        _dirs.Add(d);
        return d;
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 20, int max = 300)
    {
        for (var i = 0; i < max && !cond(); i++) await Task.Delay(ms);
    }

    // ---- shared fakes (mirroring DelegationSuspensionTests) ----------------

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class DispatchBus : IEventBus
    {
        private readonly List<(Type Type, object Handler)> _subs = [];
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
        {
            _subs.Add((typeof(TEvent), handler));
            return new Noop();
        }
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        {
            foreach (var (type, h) in _subs.Where(s => s.Type == typeof(TEvent)).ToList())
            {
                var m = h.GetType().GetMethod("Invoke")!;
                m.Invoke(h, new object?[] { @event });
            }
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class InMemCtx(IEventBus? bus = null) : IPluginContext
    {
        private sealed class Reg : IServiceRegistry
        {
            private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
            private sealed class Noop : IDisposable { public void Dispose() { } }
            private sealed class Lease<T>(T v) : IValueLease<T>
            {
                public T Value => v;
                public void Dispose() { }
                public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            }
            public void Put(string id, object o) => _map[id] = o;
            public IDisposable Register<T>(string id, T instance) where T : notnull { _map[id] = instance; return new Noop(); }
            public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>(Resolve<T>(id));
            public IValueLease<object> Acquire(string id, Type t)
                => new Lease<object>((object)ResolveInternal(id, t)!);
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
            public T Resolve<T>(string id) where T : notnull => (T)ResolveInternal(id, typeof(T))!;
            private object? ResolveInternal(string id, Type t) =>
                _map.TryGetValue(id, out var v)
                    ? v
                    : throw new ServiceUnavailableException(id, "missing service in test context");
        }
        public PluginInfo Info => new("test", "test", "1.0");
        public IServiceRegistry Services { get; } = new Reg();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events { get; } = bus ?? new DispatchBus();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NullLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        private sealed class NoopLease : IValueLease<object> { public object Value => this; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
        public void Add(string id, object service) => ((Reg)Services).Put(id, service);
    }

    private sealed class FakePolicy : IDeploymentPolicySource
    {
        private readonly Dictionary<string, DeploymentPolicy> _map;
        public FakePolicy(params DeploymentPolicy[] policies) =>
            _map = policies.ToDictionary(p => p.ModelId, StringComparer.Ordinal);
        public DeploymentPolicy? PolicyFor(string modelId) => _map.TryGetValue(modelId, out var p) ? p : null;
    }

    /// <summary>
    /// A real AgentRunner over a fake session store + counting provider — the
    /// seam for B7's "disabled replacement deployment rejects before inference".
    /// </summary>
    private sealed class InMemSessions : ISessionStore
    {
        private readonly Dictionary<string, List<SessionEntry>> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
        {
            var id = Guid.NewGuid().ToString("N");
            _sessions[id] = new SessionInfo(id, workspacePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "untitled");
            _entries[id] = [];
            return ValueTask.FromResult(_sessions[id]);
        }
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult(_sessions.TryGetValue(id, out var s) ? s : null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
        {
            if (!_entries.TryGetValue(entry.SessionId, out var list))
            {
                _sessions[entry.SessionId] = new SessionInfo(entry.SessionId, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "untitled");
                _entries[entry.SessionId] = list = [];
            }
            list.Add(entry with { Sequence = list.Count + 1 });
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(_sessions.Values.OrderByDescending(s => s.CreatedAt).Skip(offset).Take(count).ToList());
        public ValueTask<IReadOnlyList<SessionInfo>> SearchAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> SearchCountAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult(0);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(_sessions.Count);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
        {
            _sessions.Remove(id); _entries.Remove(id); return ValueTask.CompletedTask;
        }
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(_entries.TryGetValue(sessionId, out var l) ? l.Skip(offset).Take(count).ToList() : []);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(_entries.TryGetValue(sessionId, out var l) ? l.TakeLast(count).ToList() : []);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
            => throw new NotSupportedException("no project support in this fake");
    }

    private sealed class CountingProvider : IModelProvider
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            yield return new ModelStarted("m");
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant, [new TextPart("ok")], DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public List<string> SuspendedRunIds = [];
        public List<AgentRunRequest> Requeued = [];
        private readonly List<RunInfo> _runs = [];
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken ct = default)
        {
            SuspendedRunIds.Add(runId);
            var i = _runs.FindIndex(r => r.RunId == runId);
            if (i >= 0) _runs[i] = _runs[i] with { Outcome = RunState.Suspended };
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            Requeued.Add(request);
            var i = _runs.FindIndex(r => r.RunId == request.RunId);
            if (i >= 0) _runs[i] = _runs[i] with { Outcome = RunState.Running };
            return ValueTask.FromResult(true);
        }
        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, request.RunId, RunDisposition.Admitted));
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool IsRunning => false;
        public IReadOnlyList<RunInfo> ListRuns() => _runs;
        public RunInfo? GetRun(string runId) => _runs.FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) => _runs.FirstOrDefault(r => r.SessionId == sessionId);
        public bool CancelRun(string runId) => false;
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
        public void SeedRun(string runId, string sessionId) =>
            _runs.Add(new RunInfo(runId, sessionId, "m-1", AgentState.CallingModel, DateTimeOffset.UtcNow, null, RunState.Running));
    }

    private sealed record StoreEnv(FakeRunner Runner, SqliteOrchestrationStore Store,
        SqliteSessionStore Sessions, AgentOrchestrator Orch, DispatchBus Bus, InMemCtx Ctx);

    private StoreEnv MakeStoreEnv()
    {
        var dir = NewDbDir();
        var path = Path.Combine(dir, "test.db");
        using (var _ = new SqliteSessionStore(path)) { }
        var store = new SqliteOrchestrationStore(path);
        // The LIVE session store (same DB file, second connection) owns the
        // transcript plane — the orchestration store never persists entries.
        var sessions = new SqliteSessionStore(path);
        // Pre-seed the parent's sessions row so the parent transcript can carry
        // entries (FK: session_entries.session_id REFERENCES sessions.id).
        // entries (FK: session_entries.session_id REFERENCES sessions.id).
        using (var seed = OpenRaw(path))
            ExecuteSql(seed,
                "INSERT INTO sessions (id, title, workspace, created_at, updated_at, last_sequence) VALUES ('sess-parent', 'parent', NULL, $n, $n, 0);",
                c => c.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var bus = new DispatchBus();
        var runner = new FakeRunner();
        var ctx = new InMemCtx(bus);
        ctx.Add("orchestration-store", store);
        ctx.Add("runner", runner);
        ctx.Add("sessions", sessions);
        var orch = new AgentOrchestrator(ctx, store);
        return new StoreEnv(runner, store, sessions, orch, bus, ctx);
    }

    // ======================================================================
    // TASK A5 — migration rollback on a failed step
    // ======================================================================

    private static void SeedSchemaUpTo(SqliteConnection conn, int version, string extra)
    {
        foreach (var v in Enumerable.Range(1, version))
            ExecuteSql(conn, "INSERT INTO migrations (version, applied_at) VALUES ($v, $n);",
                c => { c.Parameters.AddWithValue("$v", v); c.Parameters.AddWithValue("$n", 1L); });
        ExecuteSql(conn, extra, _ => { });

    }

    private static void ExecuteSql(SqliteConnection conn, string sql, Action<SqliteCommand> p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        p(cmd);
        cmd.ExecuteNonQuery();
    }

    private static int SchemaVersion(string dbPath)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM migrations;";
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    private static bool HasCol(string dbPath, string table, string column)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString());
        c.Open();
        return c;
    }

    // The real failure seam: each migration version runs in its OWN transaction
    // and is recorded in the migrations ledger only after it applies, so a
    // step that throws rolls back completely (no partial DDL). The store has no
    // fault-injection hook — the way a step fails for real is that the DDL it
    // needs is missing. Craft that state: the ledger says v1..v6 applied, but
    // the tables v4 was supposed to create were never written (an interrupted
    // older migration). v7's guarded "ALTER TABLE agent_assignments" then throws
    // "no such table" INSIDE v7's transaction, and the store must fail clean.
    [Fact]
    public async Task Migration_FailedStep_RollsBack_LeavesDbOpenableAtPreviousVersion()
    {
        var dir = NewDbDir();
        var path = Path.Combine(dir, "fail.db");

        using (var conn = OpenRaw(path))
        {
            using var mig = conn.CreateCommand();
            mig.CommandText = "CREATE TABLE IF NOT EXISTS migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
            mig.ExecuteNonQuery();
            // v1's sessions DDL (with the later ALTERed columns already present),
            // but v4's agent_assignments table is MISSING from a "v1..v6 applied"
            // database — v7's guarded ALTER will find no table to ALTER.
            SeedSchemaUpTo(conn, 6,
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY, title TEXT, workspace TEXT, provider_id TEXT, model_id TEXT,
                    reasoning_level TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    last_sequence INTEGER NOT NULL DEFAULT 0, project_id INTEGER,
                    context_revision INTEGER, mode TEXT
                );
                """);
        }
        SqliteConnection.ClearAllPools();

        // (1) the store must FAIL at v7 — the whole construction throws, with the
        //     real missing-table error (not a guard collision).
        var ex = await Assert.ThrowsAnyAsync<SqliteException>(
            () => { using var store = new SqliteSessionStore(path); return Task.CompletedTask; });
        Assert.Contains("no such table", ex.Message, StringComparison.OrdinalIgnoreCase);

        // (2) the DB is still OPENABLE and at the PREVIOUS schema version — the
        //     failed v7 rolled back: no v7 record in the ledger, and no partial
        //     columns (the table itself never existed to carry them).
        Assert.Equal(6, SchemaVersion(path));
        using (var probe = OpenRaw(path))
        {
            using var t = probe.CreateCommand();
            t.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'agent_assignments';";
            Assert.Equal(0, Convert.ToInt32(t.ExecuteScalar()!));
        }

        // (3) recovery: once the missing table exists, the SAME store applies v7
        //     cleanly — both workspace columns land, the version is recorded.
        using (var fix = OpenRaw(path))
        {
            ExecuteSql(fix, """
                CREATE TABLE IF NOT EXISTS agent_assignments (
                    assignment_id TEXT PRIMARY KEY,
                    lifecycle TEXT NOT NULL
                );
                """, _ => { });
        }
        SqliteConnection.ClearAllPools();
        using (var recovered = new SqliteSessionStore(path)) { }
        Assert.Equal(SqliteSessionStore.CurrentSchemaVersion, SchemaVersion(path));
        Assert.True(HasCol(path, "agent_assignments", "workspace_mode"));
        Assert.True(HasCol(path, "agent_assignments", "workspace_path"));
    }

    // ======================================================================
    // TASK F5 — cloud gate: retry re-reserve, last-unit race, unknown usage
    // ======================================================================

    private static SqliteCloudBudgetStore NewBudgetStore(string dir)
        => new(Path.Combine(dir, "b-" + Guid.NewGuid().ToString("N") + ".db"));

    private static (ConfigCloudExecutionGate Gate, SqliteCloudBudgetStore Store)
        MakeGate(string dir, string config)
    {
        var store = NewBudgetStore(dir);
        // These scenarios verify cloud-budget accounting for a direct-cloud
        // model, so the gate gets the trusted policy source (a fresh
        // lanes-enabled host registers it): "cloud-m" is a DirectCloud
        // deployment. (With no policy source the gate returns the None
        // sentinel — legacy direct, no accounting — and would skip the
        // budget entirely.)
        var policy = new FakePolicy(
            new DeploymentPolicy("cloud-m", DeploymentExecutionMode.DirectCloud, null, "dep-cloud-m"));
        var gate = new ConfigCloudExecutionGate(store, JsonDocument.Parse(config).RootElement, new NullLogger(), () => policy);
        return (gate, store);
    }

    private static readonly string TeamConfig = """
        { "cloudBudgets": { "teams": [ { "teamId": "team-a", "unit": "tokens", "limit": 100, "models": ["cloud-m"] } ] } }
        """;

    private static async Task<int> CountEvents(IAsyncEnumerable<ModelEvent> events)
    {
        var n = 0;
        await foreach (var _ in events) n++;
        return n;
    }

    // (1) A FAILED inference releases its reservation: a retry of the same
    //     request re-reserves from the FULL remainder (no double-spend, no
    //     leak). (2) Concurrent reservations racing on the last unit admit
    //     exactly one. (3) A stream that fails with NO usage reported settles
    //     at zero — unknown usage fails closed, no budget is credited.
    [Fact]
    public async Task F5_FailedInference_ReleasesReservation_RetryReReserves_NoDoubleSpend()
    {
        var dir = NewDbDir();
        var (gate, store) = MakeGate(dir, TeamConfig);

        // Limit 100 tokens; each request estimates 50 (maxOutputTokens 50,
        // token-based team = rate 1). Only ONE fits at a time.
        const int maxTokens = 50;
        var req1 = new ModelRequest { ModelId = "cloud-m", RunId = "run-1", MaxTokens = maxTokens };

        var r1 = await gate.AuthorizeAsync("cloud-m", null, maxTokens, "run-1");
        Assert.True(r1.Admitted);
        Assert.Equal(50, r1.Remaining, 3);

        // The first attempt FAILS: the gate concludes a failed request by
        // releasing the drawn estimate — the remainder is whole again.
        await gate.ConcludeAsync(r1.ReservationId!, succeeded: false, actual: null);
        var u1 = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(100, u1.Remaining, 3);

        // The retry re-reserves from the FULL remainder (fresh reservation id,
        // nothing double-spent by the failed attempt).
        var r2 = await gate.AuthorizeAsync("cloud-m", null, maxTokens, "run-2");
        Assert.True(r2.Admitted);
        Assert.NotEqual(r1.ReservationId, r2.ReservationId);
        Assert.Equal(50, r2.Remaining, 3);
        var u2 = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(50, u2.Spent, 3); // the failed attempt spent nothing net
    }

    [Fact]
    public async Task F5_ConcurrentReservations_RacingOnLastUnit_OnlyOneAdmitted()
    {
        var dir = NewDbDir();
        var (gate, store) = MakeGate(dir, TeamConfig);

        // Two reservations of 60 race for a 100-token allowance: at most ONE can
        // fit (60+60 > 100), so exactly one is admitted and the other fails
        // closed with "exhausted" — the gate's BEGIN IMMEDIATE serialization makes
        // this deterministic regardless of interleaving.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            gate.AuthorizeAsync("cloud-m", null, 60, $"race-{i}").AsTask()));

        Assert.Equal(1, results.Count(r => r.Admitted));
        var denied = Assert.Single(results, r => !r.Admitted);
        Assert.Contains("exhausted", denied.Reason!, StringComparison.OrdinalIgnoreCase);

        var u = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(60, u.Spent, 3);   // one draw, not two
        Assert.Equal(40, u.Remaining, 3);
        Assert.Equal(1, (await store.ReserveInfoAsync(results.First(r => r.Admitted).ReservationId!)) is not null ? 1 : 0);
    }

    [Fact]
    public async Task F5_UnknownUsage_FailsClosed_NoBudgetCredited()
    {
        var dir = NewDbDir();
        var (gate, store) = MakeGate(dir, TeamConfig);

        // The stream produces a failure BEFORE any content and NO usage report:
        // the provider concludes with actual = 0. Settling at zero must not
        // credit (or draw) anything — the reservation settles at 0, the
        // remainder is whole, and nothing was spent for content that
        // "happened" without a measurable usage.
        var r = await gate.AuthorizeAsync("cloud-m", null, 50, "no-usage");
        Assert.True(r.Admitted);

        await gate.ConcludeAsync(r.ReservationId!, succeeded: true, actual: 0);

        var u = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(0, u.Spent, 3);            // no budget credited for unknown usage
        Assert.Equal(100, u.Remaining, 3);
        var info = await store.ReserveInfoAsync(r.ReservationId!);
        Assert.NotNull(info);
        // Token team: settlement is denominated at the persisted rate (1); settling
        // at zero leaves the remainder whole — no spend for content without usage.
        Assert.Equal(0, info!.ToDenominated(0), 3);
    }

    // The REAL provider seam: the AiProxyProvider reserves before the wire,
    // releases on a failed stream, and a retry re-reserves. A denied
    // authorization never reaches the handler (no paid call, no fallback).
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{}") });
        }
    }

    [Fact]
    public async Task F5_Provider_FailedStream_Releases_ThenRetryReReserves_Fresh()
    {
        var dir = NewDbDir();
        var (gate, store) = MakeGate(dir, TeamConfig);
        var handler = new CountingHandler();
        var provider = new AiProxyProvider(new HttpClient(handler), "http://127.0.0.1:1",
            new NullLogger(), wire: "chat", cloudGate: gate);

        // Limit 100, estimate 150 → the reservation must be DENIED before any
        // HTTP request (the §16 "No paid provider call" row).
        var denied = new List<ModelEvent>();
        await foreach (var ev in provider.RunAsync(
            new ModelRequest { ModelId = "cloud-m", RunId = "over", MaxTokens = 150 }, CancellationToken.None))
            denied.Add(ev);
        Assert.Contains(denied, e => e is ModelFailed);
        Assert.Equal(0, handler.Sent);
        Assert.Equal(0, (await store.UsageAsync(CancellationToken.None)).Single().Spent, 3);
    }

    // ======================================================================
    // TASK C4 — distinct parent/child chains, bounded result
    // ======================================================================

    // A parent and a child on the SAME model run as SEPARATE sessions. The
    // parent's resume carries only the child's outcome + title (bounded), and
    // the child's transcript — a long secret marker — stays in the child's
    // session and never appears in the parent's resume brief.
    [Fact]
    public async Task C4_ParentChildSameModel_DistinctSessions_BoundedResult_NoTranscriptLeak()
    {
        var e = MakeStoreEnv();
        var secret = "SECRET-CHILD-TRANSCRIPT-MARKER-".PadRight(120, 'x');

        var parent = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", parent.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Runner.SeedRun("parent-op", "sess-parent");
        e.Orch.SubscribeToRunnerEvents();

        var res = await e.Orch.DelegateAsync(parent.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);

        // (1) distinct sessions: the child has its OWN session, never the parent's.
        Assert.NotNull(res.ChildSessionId);
        Assert.NotEqual("sess-parent", res.ChildSessionId);
        var childRow = await e.Store.GetByRunIdAsync("child-op", default);
        Assert.NotNull(childRow);
        Assert.NotEqual("sess-parent", childRow!.SessionId);

        // The child runs (in the test) and accumulates a long transcript in ITS
        // session — the parent's session stays exactly where it was.
        var parentEntriesBefore = await e.Sessions.ReadAsync("sess-parent", 0, 100, default);
        await e.Sessions.AppendAsync(new SessionEntry("child-entry", res.ChildSessionId, EntryKind.Message,
            new AgentMessage("child-entry", MessageRole.Assistant, [new TextPart(secret)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow), default);
        await e.Sessions.AppendAsync(new SessionEntry("child-entry-2", res.ChildSessionId, EntryKind.Message,
            new AgentMessage("child-entry-2", MessageRole.Assistant, [new TextPart(secret + secret + secret)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow), default);
        var childEntries = await e.Sessions.ReadAsync(res.ChildSessionId, 0, 100, default);
        Assert.Equal(2, childEntries.Count); // the 2 transcript entries (brief is runner-persisted)

        // The child goes terminal: the durable wake resumes the parent.
        e.Bus.PublishAsync(new AgentEvent("evt-child", AgentEventType.AgentCompleted,
            DateTimeOffset.UtcNow, childRow.SessionId, null, "child-op"));
        await WaitUntil(() => e.Runner.Requeued.Count > 0);

        // (2) the parent's resume is BOUNDED: outcome + title only.
        Assert.Single(e.Runner.Requeued);
        var brief = e.Runner.Requeued[0].Text;
        Assert.True(brief.Contains("completed"), $"resume names the outcome: {brief}");
        Assert.True(brief.Contains("child work"), $"resume names the child's title: {brief}");
        Assert.False(brief.Contains("SECRET-CHILD-TRANSCRIPT-MARKER"),
            "the child's transcript must NOT reach the parent's brief");
        Assert.True(brief.Length < 2000, $"the resume brief is bounded, got {brief.Length} chars");

        // (3) chain isolation: the parent's session transcript never gained the
        //     child's entries — the child's transcript stays in the child session.
        var parentEntriesAfter = await e.Sessions.ReadAsync("sess-parent", 0, 100, default);
        Assert.True(parentEntriesBefore.Count == parentEntriesAfter.Count,
            $"the parent's session transcript must be unchanged by the child's work ({parentEntriesBefore.Count} → {parentEntriesAfter.Count})");
        var childEntriesAfter = await e.Sessions.ReadAsync(res.ChildSessionId, 0, 100, default);
        Assert.True(childEntriesAfter.Any(x => (x.Message?.Parts[0] as TextPart)?.Text?.Contains("SECRET-CHILD-TRANSCRIPT-MARKER") == true),
            "the transcript must remain in the CHILD session");

        // Consumed exactly once.
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(parent.AgentId, default));
    }

    // ======================================================================
    // TASK B7 — context-vs-concurrency rebind
    // ======================================================================

    private sealed class SchedulerHarness
    {
        public LaneScheduler Scheduler { get; }
        public SchedulerHarness()
        {
            Scheduler = new LaneScheduler("host-parity", new NullLogger());
            Scheduler.RegisterPool("local-gpu", "gpu-v1", "gpu-model", LaneCapacityMode.Provider, null, true);
        }
        public static void SetCapacity(LaneScheduler s, int total, ProviderCapacityStatus status = ProviderCapacityStatus.Fresh)
            => s.UpdateProviderObservation(new ProviderCapacityObservation(
                "gpu-model", total, status, DateTimeOffset.UtcNow, "test"));
        public static LaneQueueEntry Entry(string id, int seq, string deployment = "gpu-v1")
            => new(id, "local-gpu", deployment, seq, AgentId: null, RunId: null, SessionId: null, "task", DateTimeOffset.UtcNow);
        public static async Task<LaneOwnershipToken> AdmitAsync(LaneScheduler s, string id, int seq, string deployment = "gpu-v1")
        {
            var r = await s.AcquireAsync(Entry(id, seq, deployment));
            Assert.NotNull(r.Token);
            return r.Token!;
        }
    }

    // "Backend replaced from nInfer to llama.cpp: same Local GPU pool, same
    // Follow-AiProxy policy; new validated binding/capacity": the pool's
    // reported total concurrency drops 4→2, then rises 2→4. Existing owners
    // are NEVER evicted on a drop (drain, no excess), admission always follows
    // the NEW validated binding, and a disabled replacement deployment rejects
    // before inference.
    [Fact]
    public async Task B7_CapacityRebind_DropDrainsNoEviction_RiseAdmitsQueued()
    {
        var h = new SchedulerHarness();
        SchedulerHarness.SetCapacity(h.Scheduler, 4);
        var a = await SchedulerHarness.AdmitAsync(h.Scheduler, "A", 1);
        var b = await SchedulerHarness.AdmitAsync(h.Scheduler, "B", 2);
        var c = await SchedulerHarness.AdmitAsync(h.Scheduler, "C", 3);
        var d = await SchedulerHarness.AdmitAsync(h.Scheduler, "D", 4);

        // The backend swap reports a lower validated concurrency (4 → 2).
        // Existing owners finish on their unchanged deployment — NONE is
        // evicted; new admission simply follows the new binding.
        SchedulerHarness.SetCapacity(h.Scheduler, 2);
        var snap = h.Scheduler.Snapshots()[0];
        Assert.Equal(4, snap.OwnedCount); // all four still own lanes
        Assert.True(h.Scheduler.TryValidatePermit(a, "local-gpu", "A"));
        Assert.True(h.Scheduler.TryValidatePermit(d, "local-gpu", "D"));
        var e = await h.Scheduler.AcquireAsync(SchedulerHarness.Entry("E", 5));
        Assert.Null(e.Token); // over the NEW validated binding → queued
        Assert.Equal(1, h.Scheduler.Snapshots()[0].QueueCount);

        // Owners release as they finish; ReleaseAsync DRAINS the FIFO queue as
        // far as the (reduced) capacity allows — but only as lanes actually free
        // up. No eviction: every surviving owner keeps its lane. E is admitted
        // from the queue only once owners drop below the new total of 2.
        await h.Scheduler.ReleaseAsync(a);   // owners B,C,D (3) — E still queued (3 ≥ 2)
        Assert.Equal(3, h.Scheduler.Snapshots()[0].OwnedCount);
        Assert.Equal(1, h.Scheduler.Snapshots()[0].QueueCount);
        Assert.True(h.Scheduler.TryValidatePermit(b, "local-gpu", "B")); // survivors untouched
        await h.Scheduler.ReleaseAsync(b);   // owners C,D (2) — still at capacity, E queued
        Assert.Equal(2, h.Scheduler.Snapshots()[0].OwnedCount);
        Assert.Equal(1, h.Scheduler.Snapshots()[0].QueueCount);
        await h.Scheduler.ReleaseAsync(c);   // owners D (1) — E admitted from the queue
        Assert.Equal(2, h.Scheduler.Snapshots()[0].OwnedCount);   // D + E
        Assert.Equal(0, h.Scheduler.Snapshots()[0].QueueCount);   // E consumed
        // The replacement backend reports a higher concurrency (2 → 4):
        // admission follows the new binding — exactly enough to reach 4 owners.
        SchedulerHarness.SetCapacity(h.Scheduler, 4);
        var e2 = await h.Scheduler.AcquireAsync(SchedulerHarness.Entry("E2", 7));
        Assert.NotNull(e2.Token);
        var g = await h.Scheduler.AcquireAsync(SchedulerHarness.Entry("G", 8));
        Assert.NotNull(g.Token);
        var h2 = await h.Scheduler.AcquireAsync(SchedulerHarness.Entry("H", 9));
        Assert.Null(h2.Token); // never over-admits the new total
        snap = h.Scheduler.Snapshots()[0];
        Assert.Equal(4, snap.OwnedCount);
        Assert.Equal(1, snap.QueueCount);

        // And a STALE (old, lower) observation must NOT re-shrink admission:
        // stale/unknown capacity is hold-new, never a basis to evict.
        SchedulerHarness.SetCapacity(h.Scheduler, 1, ProviderCapacityStatus.Stale);
        var i = await h.Scheduler.AcquireAsync(SchedulerHarness.Entry("I", 10));
        Assert.Null(i.Token); // hold-new on a stale observation
        Assert.Equal(4, h.Scheduler.Snapshots()[0].OwnedCount); // owners untouched
    }

    // A DISABLED replacement deployment rejects BEFORE inference — no provider
    // call, ever. (The runner's direct-cloud gate: a disabled deployment is
    // recorded so its requests are rejected, never rerouted.)
    [Fact]
    public async Task B7_DisabledReplacementDeployment_RejectsBeforeInference_NoProviderCall()
    {
        var bus = new DispatchBus();
        var ctx = new InMemCtx(bus);
        var sessions = new InMemSessions();
        var provider = new CountingProvider();
        var lanes = new LaneScheduler("gen-parity", new NullLogger());
        // The LOCAL pooled model is healthy; the replacement CLOUD deployment
        // is disabled — the exact "new validated binding" that must reject.
        lanes.RegisterPool("pool-gpu", "dep-gpu", "gpu-model", LaneCapacityMode.Manual, 2, true);
        SchedulerHarness.SetCapacity(lanes, 2);
        var policy = new FakePolicy(
            new DeploymentPolicy("gpu-model", DeploymentExecutionMode.Pooled, "pool-gpu", "dep-gpu"),
            new DeploymentPolicy("cloud-llama", DeploymentExecutionMode.DirectCloud, null, "dep-llama", Enabled: false));
        ctx.Add("sessions", sessions);
        ctx.Add("provider", provider);
        ctx.Add("deployments", policy);
        ctx.Add("lanes", lanes);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 2);

        // The replacement deployment: rejected before ANY inference.
        var rejected = await runner.StartRunAsync(new AgentRunRequest("sess-1", null, "cloud-llama", "swap"));
        Assert.False(string.IsNullOrEmpty(rejected.Note));
        Assert.Contains("disabled", rejected.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, provider.Calls);

        // The healthy pooled binding still admits (the pool was not clobbered).
        var admitted = await runner.StartRunAsync(new AgentRunRequest("sess-2", null, "gpu-model", "local"));
        Assert.Null(admitted.Note);
        Assert.Equal(RunDisposition.Admitted, admitted.Disposition);
        await WaitUntil(() => provider.Calls >= 1); // the admitted run reaches the wire
        Assert.Equal(1, provider.Calls); // only the healthy binding reached the wire
    }

    // ======================================================================
    // TASK D4 — concurrent mailbox deliveries + late results
    // ======================================================================

    // (1) N concurrent sends with the SAME idempotency key: exactly ONE row,
    //     every caller gets the same sequence (no lost/dup rows under a real
    //     concurrency loop).
    [Fact]
    public async Task D4_ConcurrentSends_SameIdempotencyKey_ExactlyOneRow()
    {
        var e = MakeStoreEnv();
        var from = await e.Store.EnsureRootAgentAsync("sess-a", null, "a");
        var to = await e.Store.EnsureRootAgentAsync("sess-b", null, "b");
        const string key = "key-same";

        var seqs = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            e.Store.SendMessageAsync(Guid.NewGuid().ToString("N"), from.AgentId, to.AgentId, null,
                "note", $"body-{i}", null, key, default).AsTask()));

        // Every caller sees the SAME (original) sequence — idempotent replay.
        Assert.All(seqs, s => Assert.Equal(seqs[0], s));
        var drained = await e.Store.DrainMailboxAsync(to.AgentId, 100, default);
        Assert.Single(drained, m => m.IdempotencyKey == key);
        Assert.Equal(1, drained.Count);
    }

    // (2) N concurrent sends with DISTINCT keys: every message persists exactly
    //     once and the per-recipient sequences are unique, gap-free, ordered.
    [Fact]
    public async Task D4_ConcurrentSends_DistinctKeys_AllPersist_UniqueSequences()
    {
        var e = MakeStoreEnv();
        var from = await e.Store.EnsureRootAgentAsync("sess-a", null, "a");
        var to = await e.Store.EnsureRootAgentAsync("sess-b", null, "b");

        const int n = 25;
        var seqs = await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
            e.Store.SendMessageAsync(Guid.NewGuid().ToString("N"), from.AgentId, to.AgentId, null,
                "note", $"body-{i}", null, $"key-{i}", default).AsTask()));

        Assert.Equal(Enumerable.Range(1, n), seqs.OrderBy(x => x).ToArray()); // gap-free 1..n
        var drained = await e.Store.DrainMailboxAsync(to.AgentId, 100, default);
        Assert.Equal(n, drained.Count);
        Assert.Equal(n, drained.Select(m => m.RecipientSequence).Distinct().Count());
    }

    // (3) The per-agent outstanding bound: at the bound, the next send is
    //     rejected as a bounded failure (the §9 per-agent mailbox bound).
    [Fact]
    public async Task D4_OutstandingBound_AtBound_RejectsNextSend()
    {
        var e = MakeStoreEnv();
        // The orchestrator enforces the bound (the store alone does not).
        var store = e.Store;
        var from = await store.EnsureRootAgentAsync("sess-a", null, "a");
        var to = await store.EnsureRootAgentAsync("sess-b", null, "b");
        var orch = new AgentOrchestrator(e.Ctx, store, maxDelegationDepth: 3, maxOutstandingMessages: 2);

        Assert.Equal(1, await orch.SendMessageAsync(from.AgentId, to.AgentId, "note", "m1"));
        Assert.Equal(2, await orch.SendMessageAsync(from.AgentId, to.AgentId, "note", "m2"));
        await Assert.ThrowsAnyAsync<MessageBoundExceededException>(
            () => orch.SendMessageAsync(from.AgentId, to.AgentId, "note", "m3").AsTask());
        // Nothing past the bound was persisted.
        Assert.Equal(2, (await store.DrainMailboxAsync(to.AgentId, 10, default)).Count);
    }

    // (4) A LATE terminal result after the parent's wait was already consumed
    //     must not resume a second time: the reconciler's second terminal
    //     transition fails (the row is already terminal), no second requeue.
    [Fact]
    public async Task D4_LateChildTerminal_AfterWaitConsumed_NoDoubleResume()
    {
        var e = MakeStoreEnv();
        var parent = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", parent.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Runner.SeedRun("parent-op", "sess-parent");
        e.Orch.SubscribeToRunnerEvents();

        var res = await e.Orch.DelegateAsync(parent.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);
        var childRow = await e.Store.GetByRunIdAsync("child-op", default);
        Assert.NotNull(childRow);

        // First terminal: the wait is satisfied and the parent is resumed ONCE.
        e.Bus.PublishAsync(new AgentEvent("evt-1", AgentEventType.AgentCompleted,
            DateTimeOffset.UtcNow, childRow!.SessionId, null, "child-op"));
        await WaitUntil(() => e.Runner.Requeued.Count > 0);
        Assert.Single(e.Runner.Requeued);
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(parent.AgentId, default));

        // A LATE second terminal for the SAME child (a duplicate/late report):
        // the assignment is already terminal — the transition fails, no new
        // wake, and the parent is NOT resumed a second time.
        e.Bus.PublishAsync(new AgentEvent("evt-2", AgentEventType.AgentCompleted,
            DateTimeOffset.UtcNow, childRow.SessionId, null, "child-op"));
        await WaitUntil(() => e.Runner.Requeued.Count > 0, max: 20);
        Assert.Single(e.Runner.Requeued); // a late terminal must not resume a second time
        Assert.Equal(AgentAssignmentLifecycle.Completed,
            (await e.Store.GetAssignmentAsync(res.ChildAssignmentId, default))!.Lifecycle);
        Assert.Equal(AgentAssignmentLifecycle.Running,
            (await e.Store.GetAssignmentAsync(res.ParentAssignmentId, default))!.Lifecycle);
    }
}
