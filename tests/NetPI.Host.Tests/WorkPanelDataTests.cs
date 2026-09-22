using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Activity;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §12 data-contract tests for the combined Work panel (LANE 2, package E).
/// Real Kestrel (port 0) with the real ActivityWebApp; the consumed services
/// (orchestration, lanes, background, foreground) are faked through Abstractions.
/// Covers the §12.1/§12.2 invariants:
///   - agents are newest-first by createdAt with a stable id tiebreak;
///   - background processes are startedAt-DESC with a stable id tiebreak (NO
///     running-first regrouping);
///   - the agents payload carries a monotonic revision and explicit per-service
///     availability; a missing service degrades ONLY its own section;
///   - cancel delegates through the orchestration contract (subtree semantics)
///     and falls back to the legacy runner when the orchestrator is absent;
///   - a background job's output tail is the LATEST requested tail (the ring end),
///     not the tail of the first fetched chunk.
/// </summary>
public class WorkPanelDataTests
{
    private static readonly HttpClient Http = new();

    // ---- fakes ---------------------------------------------------------------

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class FakeRegistry : IServiceRegistry
    {
        private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
        public void Add(string id, object service) => _services[id] = service;
        public T Resolve<T>(string id) where T : notnull
            => (T)(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "not registered"));
        public IDisposable Register<T>(string id, T instance) where T : notnull { _services[id] = instance; return new Noop(); }
        public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>(Resolve<T>(id));
        public IValueLease<object> Acquire(string id, Type expectedType)
            => new Lease<object>(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "no"));
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakePanels : IWebPanelRegistry
    {
        public List<WebPanelDefinition> Registered { get; } = [];
        public IDisposable Register(WebPanelDefinition panel) { Registered.Add(panel); return new Handle(Registered, panel); }
        private sealed class Handle(List<WebPanelDefinition> list, WebPanelDefinition panel) : IDisposable { public void Dispose() => list.Remove(panel); }
        public IReadOnlyList<WebPanelDefinition> All() => Registered;
    }

    private sealed class FakeContext(FakeRegistry services, IWebPanelRegistry? panels = null, JsonElement? config = null) : IPluginContext
    {
        private readonly IWebPanelRegistry _panels = panels ?? new FakePanels();
        private readonly JsonElement _config = config ?? JsonDocument.Parse("{}").RootElement.Clone();
        public PluginInfo Info { get; } = new("netPI.Activity", "Activity Test", "0.1.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => _panels;
        public IEventBus Events => throw new NotSupportedException();
        public JsonElement OwnConfig => _config;
        public IPluginLogger Log => new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    private static AgentAssignmentRow Row(
        string id, string sid, AgentAssignmentLifecycle lc, DateTimeOffset createdAt,
        DateTimeOffset? ended = null, string? pool = null,
        DeploymentExecutionMode mode = DeploymentExecutionMode.Pooled) =>
        new(id, "agent-" + id, null, sid, null, lc,
            lc == AgentAssignmentLifecycle.Running ? AgentState.ExecutingTools : AgentState.Idle,
            mode, pool, LaneId: pool is null ? null : "lane-" + pool, null, "m1", "t-" + id,
            createdAt, StartedAt: lc == AgentAssignmentLifecycle.Running ? createdAt : null,
            EndedAt: ended, Reason: null);

    /// <summary>Fakes the orchestrator: a fixed row set + cancel recording.</summary>
    private sealed class FakeOrch : IAgentOrchestrator
    {
        public IReadOnlyList<AgentAssignmentRow> Rows { get; }
        public (string Id, bool Subtree)? Cancelled { get; private set; }
        public FakeOrch(IEnumerable<AgentAssignmentRow> rows) => Rows = rows.ToList();

        public ValueTask<AgentSpawnResult> SpawnChildAsync(string p, AgentSpawnRequest r, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask<int> SendMessageAsync(string f, string t, string k, string b, string? idem = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string a, int n, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask RegisterWaitAsync(string a, AgentWaitCondition c, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string a, CancellationToken ct) =>
            ValueTask.FromResult(Rows.FirstOrDefault(r => r.AssignmentId == a));
        public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string s, CancellationToken ct) =>
            ValueTask.FromResult(Rows.FirstOrDefault(r => r.SessionId == s && r.IsNonTerminal));
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListAssignmentsAsync(CancellationToken ct) =>
            ValueTask.FromResult(Rows);
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentAssignmentRow>>(Rows.Where(r => r.AgentId == agentId).ToList());
        public ValueTask<AgentAssignmentLifecycle> CancelAsync(string assignmentId, bool subtree, CancellationToken ct)
        {
            Cancelled = (assignmentId, subtree);
            return ValueTask.FromResult(AgentAssignmentLifecycle.Cancelled);
        }
        public ValueTask<AgentSpawnResult> ContinueAsync(string a, string t, string? op, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Fakes the lane scheduler with a fixed pool set.</summary>
    private sealed class FakeLanes : ILaneScheduler
    {
        public IReadOnlyList<LanePoolSnapshot> Pools { get; }
        public FakeLanes(IEnumerable<LanePoolSnapshot> pools) => Pools = pools.ToList();
        public ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry q) => throw new NotSupportedException();
        public ValueTask ReleaseAsync(LaneOwnershipToken t) => throw new NotSupportedException();
        public ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken from, LaneQueueEntry to) => throw new NotSupportedException();
        public bool TryValidatePermit(LaneOwnershipToken? t, string pool, string assignment) => false;
        public ValueTask<bool> CancelQueuedAsync(string a) => ValueTask.FromResult(false);
        public IReadOnlyList<LanePoolSnapshot> Snapshots() => Pools;
        public ValueTask SetPoolEnabledAsync(string pool, bool enabled) => throw new NotSupportedException();
    }

    /// <summary>
    /// Fakes the background-job manager with a multi-slice output ring: the
    /// LATEST tail must come from the end of the ring, served across slice fetches.
    /// </summary>
    private sealed class RingJobs : IBackgroundJobManager
    {
        private readonly List<BackgroundJobInfo> _jobs;
        public List<string> Killed { get; } = [];
        public string Full { get; } // the whole ring for "bg-1"
        public RingJobs(IEnumerable<BackgroundJobInfo> jobs, string full)
        {
            _jobs = jobs.ToList();
            Full = full;
        }
        public ValueTask<BackgroundJobInfo> StartAsync(string s, string c, string w, JsonElement? o, JobOwnership? n = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken ct = default) =>
            ValueTask.FromResult(_jobs.FirstOrDefault(j => j.JobId == jobId));
        public ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<BackgroundJobInfo>>(_jobs);
        public ValueTask<bool> KillAsync(string jobId, CancellationToken ct = default)
        {
            var found = _jobs.Any(j => j.JobId == jobId);
            if (found) Killed.Add(jobId);
            return ValueTask.FromResult(found);
        }
        // Serves "full" in 64k per-call slices with a cursor (NextOffset).
        public ValueTask<BackgroundJobOutput> GetOutputAsync(string jobId, int offset, CancellationToken ct = default)
        {
            if (jobId != "bg-1")
                return ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Succeeded, 0, "no such job", offset, false));
            var full = Full;
            if (offset >= full.Length)
                return ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Succeeded, 0, "", offset, false));
            int len = Math.Min(64_000, full.Length - offset);
            var slice = full.Substring(offset, len);
            int next = offset + len;
            return ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Succeeded, 0, slice, next, false));
        }
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public List<RunInfo> Runs { get; } = [];
        public List<string> Cancelled { get; } = [];
        public bool IsRunning => false;
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, null));
        public ValueTask CancelRunAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public IReadOnlyList<RunInfo> ListRuns() => Runs;
        public RunInfo? GetRun(string runId) => Runs.FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) => Runs.FirstOrDefault(r => r.SessionId == sessionId);
        public bool CancelRun(string runId)
        {
            Cancelled.Add(runId);
            return Runs.Any(r => r.RunId == runId);
        }
        public SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

    // ---- harness -------------------------------------------------------------

    private static async Task<(ActivityWebApp app, string baseUrl)> MakeAsync(FakeRegistry reg)
    {
        var app = new ActivityWebApp(new FakeContext(reg), 0);
        await app.StartAsync(CancellationToken.None);
        return (app, app.BoundUrl!);
    }

    private static async Task<JsonElement> GetJsonAsync(string url)
    {
        var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(string url, string? body = null)
    {
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        var resp = content is null ? await Http.PostAsync(url, null) : await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---- tests ---------------------------------------------------------------

    [Fact]
    public async Task Agents_NewestFirstWithStableIdTiebreak()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            // same createdAt (tie) -> id DESC decides: "asg-b" before "asg-a".
            Row("asg-a", "s1", AgentAssignmentLifecycle.Running, now.AddMinutes(-2)),
            Row("asg-b", "s2", AgentAssignmentLifecycle.Running, now.AddMinutes(-2)),
            Row("asg-c", "s3", AgentAssignmentLifecycle.Queued, now.AddMinutes(-5)),   // oldest, last.
        };
        var reg = new FakeRegistry();
        reg.Add("orchestration", new FakeOrch(rows));
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/agents");
            var agents = d.GetProperty("agents");
            // All three are nonterminal → all appear in `agents`.
            Assert.Equal(3, agents.GetArrayLength());
            Assert.Equal("asg-b", agents[0].GetProperty("assignmentId").GetString());
            Assert.Equal("asg-a", agents[1].GetProperty("assignmentId").GetString());
            Assert.Equal("asg-c", agents[2].GetProperty("assignmentId").GetString());
            // lifecycle strings are the documented lower-case wire names.
            Assert.Equal("running", agents[0].GetProperty("lifecycle").GetString());
            Assert.True(agents[0].GetProperty("nonTerminal").GetBoolean());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Agents_RevisionMonotonic_AndHistoryBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            Row("asg-1", "s1", AgentAssignmentLifecycle.Running, now.AddMinutes(-1)),
            Row("asg-2", "s2", AgentAssignmentLifecycle.Completed, now.AddMinutes(-60)),
            Row("asg-3", "s3", AgentAssignmentLifecycle.Failed, now.AddMinutes(-59)),
        };
        var reg = new FakeRegistry();
        reg.Add("orchestration", new FakeOrch(rows));
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d1 = await GetJsonAsync(url + "/api/activity/agents");
            long r1 = d1.GetProperty("revision").GetInt32();
            Assert.True(r1 > 0, "revision must be set on a successful orchestrator query (was 0)");

            // Nonterminal `agents` = only the running one; the two terminal rows
            // go to bounded `history` (newest-ended first).
            var agents = d1.GetProperty("agents");
            Assert.Equal(1, agents.GetArrayLength());
            Assert.Equal("asg-1", agents[0].GetProperty("assignmentId").GetString());
            var hist = d1.GetProperty("history");
            Assert.Equal(2, hist.GetArrayLength());
            Assert.Equal("asg-3", hist[0].GetProperty("assignmentId").GetString()); // ended -59
            Assert.Equal("asg-2", hist[1].GetProperty("assignmentId").GetString()); // ended -60

            // A second query bumps the revision monotonically.
            var d2 = await GetJsonAsync(url + "/api/activity/agents");
            Assert.True(d2.GetProperty("revision").GetInt32() > r1);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Processes_NewestStartedFirst_NoRunningFirstRegrouping()
    {
        // A SUCCESSFUL (not running) job started most recently must sort FIRST —
        // the old background page's "running first" regrouping must not resurface.
        var now = DateTimeOffset.UtcNow;
        var jobs = new RingJobs(new[]
        {
            new BackgroundJobInfo("bg-old", "bash", "build", BackgroundJobState.Succeeded, now.AddMinutes(-10), now.AddMinutes(-9), 0, null, SessionId: "s1", WorkingDirectory: "/w"),
            new BackgroundJobInfo("bg-new", "bash", "node s", BackgroundJobState.Succeeded, now.AddSeconds(-30), now.AddSeconds(-20), 0, null, SessionId: "s1", WorkingDirectory: "/w"),
            new BackgroundJobInfo("bg-run", "bash", "server", BackgroundJobState.Running, now.AddSeconds(-40), null, null, null, SessionId: "s1", WorkingDirectory: "/w"),
        }, full: "x");
        var reg = new FakeRegistry();
        reg.Add("background", jobs);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/processes");
            var bg = d.GetProperty("background").GetProperty("jobs");
            Assert.Equal("bg-new", bg[0].GetProperty("id").GetString());   // started -30s (newest)
            Assert.Equal("bg-run", bg[1].GetProperty("id").GetString());   // started -40s
            Assert.Equal("bg-old", bg[2].GetProperty("id").GetString());   // started -10m
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancel_DelegatesToOrchestrator_WithSubtree()
    {
        var orch = new FakeOrch(new[] { Row("asg-1", "s1", AgentAssignmentLifecycle.Running, DateTimeOffset.UtcNow) });
        var reg = new FakeRegistry();
        reg.Add("orchestration", orch);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await PostAsync(url + "/api/activity/agents/asg-1/cancel?subtree=false", "{}");
            Assert.True(d.GetProperty("ok").GetBoolean());
            Assert.Equal("cancelled", d.GetProperty("outcome").GetString());
            Assert.Equal(("asg-1", false), orch.Cancelled); // subtree honored from the query
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancel_FallsBackToRunner_WhenOrchestratorAbsent()
    {
        var runner = new FakeRunner();
        runner.Runs.Add(new RunInfo("run-1", "s1", "m1", AgentState.CallingModel, DateTimeOffset.UtcNow, null, RunState.Running));
        var reg = new FakeRegistry();
        reg.Add("runner", runner); // NO orchestration
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await PostAsync(url + "/api/activity/agents/run-1/cancel", "{}");
            Assert.True(d.GetProperty("ok").GetBoolean());
            Assert.Equal(["run-1"], runner.Cancelled);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Sections_DegradeIndependently()
    {
        // Agent services present, process services ABSENT:
        //   agents available=true, lanes available=true, processes unavailable,
        //   and the whole payload still serializes (never a 500).
        var now = DateTimeOffset.UtcNow;
        var orch = new FakeOrch(new[] { Row("asg-1", "s1", AgentAssignmentLifecycle.Running, now) });
        var lanes = new FakeLanes(new[]
        {
            new LanePoolSnapshot("p1", true, false, 1, 4, LaneCapacityMode.Provider, 4, 8,
                ProviderCapacityStatus.Fresh, DateTimeOffset.UtcNow, 0, null),
        });
        var reg = new FakeRegistry();
        reg.Add("orchestration", orch);
        reg.Add("lanes", lanes);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/agents");
            Assert.True(d.GetProperty("agentsAvailable").GetBoolean());
            Assert.True(d.GetProperty("lanesAvailable").GetBoolean());
            Assert.False(d.GetProperty("runsAvailable").GetBoolean());
            Assert.NotEmpty(d.GetProperty("pools").EnumerateArray());

            var work = await GetJsonAsync(url + "/api/activity/work");
            Assert.True(work.GetProperty("agents").GetProperty("agentsAvailable").GetBoolean());
            Assert.False(work.GetProperty("processes").GetProperty("background").GetProperty("available").GetBoolean());
            Assert.False(work.GetProperty("processes").GetProperty("foreground").GetProperty("available").GetBoolean());
        }
        finally { await app.StopAsync(CancellationToken.None); }

        // The inverse: process services present, agent services ABSENT.
        var reg2 = new FakeRegistry();
        var jobs = new RingJobs(new[]
        {
            new BackgroundJobInfo("bg-1", "bash", "node", BackgroundJobState.Running, now, null, null, null, SessionId: "s1", WorkingDirectory: "/w"),
        }, full: "hello");
        reg2.Add("background", jobs);
        var (app2, url2) = await MakeAsync(reg2);
        try
        {
            var d = await GetJsonAsync(url2 + "/api/activity/agents");
            Assert.False(d.GetProperty("agentsAvailable").GetBoolean());
            Assert.False(d.GetProperty("lanesAvailable").GetBoolean());
            // The surface is still alive and the processes section still works.
            var procs = await GetJsonAsync(url2 + "/api/activity/processes");
            Assert.True(procs.GetProperty("background").GetProperty("available").GetBoolean());
            Assert.Equal("bg-1", procs.GetProperty("background").GetProperty("jobs")[0].GetProperty("id").GetString());
        }
        finally { await app2.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Output_IsLatestTailOfRing_NotFirstChunk()
    {
        // The ring is 65,000 'x' chars followed by "ENDMARKER" (65,009 total —
        // crosses a 64k slice boundary). The LATEST tail must contain ENDMARKER
        // (the ring END), proving we paged to the ring end rather than stopping
        // at the first fetched chunk.
        var full = new string('x', 65_000) + "ENDMARKER";
        var now = DateTimeOffset.UtcNow;
        var jobs = new RingJobs(new[]
        {
            new BackgroundJobInfo("bg-1", "bash", "node", BackgroundJobState.Succeeded, now, now.AddSeconds(5), 0, null, SessionId: "s1", WorkingDirectory: "/w"),
        }, full: full);
        var reg = new FakeRegistry();
        reg.Add("background", jobs);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/processes/background/bg-1/output?chars=20");
            Assert.True(d.GetProperty("available").GetBoolean());
            var text = d.GetProperty("text").GetString()!;
            Assert.Equal(20, text.Length);
            Assert.True(text.EndsWith("ENDMARKER"), "expected the ring tail (last slice), got: " + text);
            Assert.True(d.GetProperty("truncated").GetBoolean()); // we dropped front chars to fit the bound
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Panel_RegistersWorkTab_WithCompatRedirect()
    {
        // astra-2 §12.1: Activity registers the ONE combined Work panel
        // (id "background", title "Work", order 5) and /panel/background keeps
        // resolving (a 302 to /panel/activity). BackgroundTasks keeps its own
        // surface but no longer registers a panel.
        var panels = new FakePanels();
        var reg = new FakeRegistry();
        var cfg = JsonDocument.Parse("{\"port\":0}").RootElement.Clone();
        var plugin = new ActivityPlugin();
        await plugin.LoadAsync(new FakeContext(reg, panels, cfg), CancellationToken.None);
        await plugin.StartAsync(CancellationToken.None);
        try
        {
            var p = panels.Registered.Single();
            Assert.Equal("background", p.Id);
            Assert.Equal("Work", p.Title);
            Assert.Equal(5, p.Order);
            // The entry URL is the real bound URL + /panel/activity.
            Assert.EndsWith("/panel/activity", p.EntryUrl);
            Assert.Contains("/panel/activity", p.EntryUrl, StringComparison.Ordinal);

            // Compat redirect: /panel/background 302 → /panel/activity.
            // Use a no-redirect client so we can assert the 302 itself.
            using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var resp = await noRedirect.GetAsync(p.EntryUrl[..p.EntryUrl.LastIndexOf("/panel/activity")] + "/panel/background");
            Assert.Equal(System.Net.HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal("/panel/activity", resp.Headers.Location!.ToString());
        }
        finally { await plugin.StopAsync(CancellationToken.None); await plugin.UnloadAsync(CancellationToken.None); }
    }

}
