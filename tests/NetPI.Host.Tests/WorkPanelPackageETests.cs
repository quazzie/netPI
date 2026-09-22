using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Activity;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §15.E (package E): the combined Work panel consolidation contract.
/// The Activity plugin is the PRESENTATION owner of the one Work view; the
/// separate "activity" panel id is gone (migrated to "background"), the query
/// payloads carry lanes + lifecycle + two newest-first lists (agents +
/// background processes), every nonterminal root/child with its
/// waiting/suspended reason is visible, and a missing service degrades only
/// its own section (the view never 500s). Real Kestrel (port 0) + the real
/// ActivityWebApp/ActivityPlugin; services are faked through Abstractions.
/// </summary>
public sealed class WorkPanelPackageETests
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
        string? parentAgentId = null, string? reason = null,
        string agentId = null) =>
        new(id, agentId ?? "agent-" + id, null, sid, parentAgentId, lc,
            lc == AgentAssignmentLifecycle.Running ? AgentState.ExecutingTools : AgentState.Idle,
            DeploymentExecutionMode.Pooled, "pool-1", "lane-1", "dep-1", "m1", "t-" + id,
            createdAt, StartedAt: lc == AgentAssignmentLifecycle.Running ? createdAt : null,
            EndedAt: null, Reason: reason);

    private sealed class FakeOrch : IAgentOrchestrator
    {
        public readonly List<AgentAssignmentRow> Rows;
        public FakeOrch(IEnumerable<AgentAssignmentRow> rows) => Rows = rows.ToList();
        public ValueTask<AgentSpawnResult> SpawnChildAsync(string p, AgentSpawnRequest r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<int> SendMessageAsync(string f, string t, string k, string b, string? idem = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string a, int n, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask RegisterWaitAsync(string a, AgentWaitCondition c, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string a, CancellationToken ct)
            => ValueTask.FromResult(Rows.FirstOrDefault(r => r.AssignmentId == a));
        public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string s, CancellationToken ct)
            => ValueTask.FromResult(Rows.FirstOrDefault(r => r.SessionId == s && r.IsNonTerminal));
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListAssignmentsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<AgentAssignmentRow>>(Rows);
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<AgentAssignmentRow>>(Rows.Where(r => r.AgentId == agentId).ToList());
        public ValueTask<AgentAssignmentLifecycle> CancelAsync(string assignmentId, bool subtree, CancellationToken ct)
            => ValueTask.FromResult(AgentAssignmentLifecycle.Cancelled);
        public ValueTask<AgentDelegateResult> DelegateAsync(string p, AgentSpawnRequest r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<int> ConsumeSatisfiedWaitsAsync(string a, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<AgentSpawnResult> ContinueAsync(string a, string t, string? op, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeLanes : ILaneScheduler
    {
        public readonly List<LanePoolSnapshot> Pools;
        public FakeLanes(IEnumerable<LanePoolSnapshot> pools) => Pools = pools.ToList();
        public ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry q) => throw new NotSupportedException();
        public ValueTask ReleaseAsync(LaneOwnershipToken t) => throw new NotSupportedException();
        public ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken from, LaneQueueEntry to) => throw new NotSupportedException();
        public bool TryValidatePermit(LaneOwnershipToken? t, string pool, string assignment) => false;
        public ValueTask<bool> CancelQueuedAsync(string a) => ValueTask.FromResult(false);
        public IReadOnlyList<LanePoolSnapshot> Snapshots() => Pools;
        public ValueTask SetPoolEnabledAsync(string pool, bool enabled) => throw new NotSupportedException();
    }


    private sealed class FakeJobs : IBackgroundJobManager
    {
        public readonly List<BackgroundJobInfo> Jobs;
        public List<string> Killed = [];
        public FakeJobs(IEnumerable<BackgroundJobInfo> jobs) => Jobs = jobs.ToList();
        public ValueTask<BackgroundJobInfo> StartAsync(string s, string c, string w, JsonElement? o, JobOwnership? n = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken ct = default)
            => ValueTask.FromResult(Jobs.FirstOrDefault(j => j.JobId == jobId));
        public ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<BackgroundJobInfo>>(Jobs);
        public ValueTask<bool> KillAsync(string jobId, CancellationToken ct = default)
        {
            var found = Jobs.Any(j => j.JobId == jobId);
            if (found) Killed.Add(jobId);
            return ValueTask.FromResult(found);
        }
        public ValueTask<BackgroundJobOutput> GetOutputAsync(string jobId, int offset, CancellationToken ct = default)
            => ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Succeeded, 0, "no such job", offset, false));
    }

    private sealed class FakeForeground : IForegroundProcessTracker
    {
        private readonly Dictionary<int, ForegroundProcessInfo> _procs = new();
        public void Started(string toolShellId, string command, string? workingDirectory, string? sessionId, int processId)
            => _procs[processId] = new ForegroundProcessInfo(toolShellId, command, workingDirectory, sessionId, processId, DateTimeOffset.UtcNow, null, null);
        public void Finished(int processId, int? exitCode)
        {
            if (_procs.TryGetValue(processId, out var p))
                _procs[processId] = p with { ExitCode = exitCode, ExitedAt = DateTimeOffset.UtcNow };
        }
        public IReadOnlyList<ForegroundProcessInfo> Running() => _procs.Values.Where(p => p.IsRunning).OrderBy(p => p.StartedAt).ToList();
        public IReadOnlyList<ForegroundProcessInfo> Recent(int max = 50) => _procs.Values.Where(p => !p.IsRunning).OrderByDescending(p => p.ExitedAt).Take(max).ToList();
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public IReadOnlyList<RunInfo> Runs { get; } = [];
        public bool IsRunning => false;
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, null));
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IReadOnlyList<RunInfo> ListRuns() => Runs;
        public RunInfo? GetRun(string runId) => Runs.FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) => Runs.FirstOrDefault(r => r.SessionId == sessionId);
        public bool CancelRun(string runId) => true;
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

    // ---- §15.E: the Work view is the ONE combined panel, id "background" ----
    [Fact]
    public async Task Panel_OneCombinedWorkView_RegisteredUnderBackgroundId()
    {
        // The separate "activity" panel is gone: Activity registers exactly ONE
        // panel (id "background", title "Work") pointing at /panel/activity.
        var panels = new FakePanels();
        var reg = new FakeRegistry();
        var cfg = JsonDocument.Parse("{\"port\":0}").RootElement.Clone();
        var plugin = new ActivityPlugin();
        await plugin.LoadAsync(new FakeContext(reg, panels, cfg), CancellationToken.None);
        await plugin.StartAsync(CancellationToken.None);
        try
        {
            var all = panels.All();
            Assert.Equal(1, all.Count);
            var p = all[0];
            Assert.Equal("background", p.Id);
            Assert.Equal("Work", p.Title);
            Assert.EndsWith("/panel/activity", p.EntryUrl);
        }
        finally { await plugin.StopAsync(CancellationToken.None); await plugin.UnloadAsync(CancellationToken.None); }
    }

    // ---- §15.E: query payloads carry lanes + two newest-first lists --------
    [Fact]
    public async Task WorkPayload_CarriesLanes_AgentsNewestFirst_ProcessesNewestStartedFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            Row("asg-oldest", "s1", AgentAssignmentLifecycle.Running, now.AddMinutes(-10)),
            Row("asg-newest", "s2", AgentAssignmentLifecycle.Running, now.AddMinutes(-1)),
            Row("asg-mid", "s3", AgentAssignmentLifecycle.Running, now.AddMinutes(-5)),
        };
        var pools = new[]
        {
            new LanePoolSnapshot("p1", true, false, 1, 4, LaneCapacityMode.Provider, 4, 8,
                ProviderCapacityStatus.Fresh, DateTimeOffset.UtcNow, 0, null),
            new LanePoolSnapshot("p2", true, false, 2, 2, LaneCapacityMode.Manual, null, 2,
                ProviderCapacityStatus.Unknown, null, 1, null),
        };
        var jobs = new FakeJobs(new[]
        {
            new BackgroundJobInfo("bg-new", "bash", "node", BackgroundJobState.Running, now.AddSeconds(-5), null, null, null, "s1", "/w"),
            new BackgroundJobInfo("bg-old", "bash", "build", BackgroundJobState.Succeeded, now.AddMinutes(-30), now.AddMinutes(-29), 0, null, "s1", "/w"),
        });
        var fg = new FakeForeground();
        fg.Started("pwsh", "npm run dev", "/w", "s1", 4242);
        var reg = new FakeRegistry();
        reg.Add("orchestration", new FakeOrch(rows));
        reg.Add("lanes", new FakeLanes(pools));
        reg.Add("background", jobs);
        reg.Add("foreground-processes", fg);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/work");
            // One combined snapshot: agents + lanes + processes, all present.
            Assert.True(d.GetProperty("agents").GetProperty("agentsAvailable").GetBoolean());
            Assert.True(d.GetProperty("lanes").GetProperty("available").GetBoolean());
            Assert.True(d.GetProperty("processes").GetProperty("background").GetProperty("available").GetBoolean());
            // Lanes: both pools, sorted by pool id.
            var lanePools = d.GetProperty("lanes").GetProperty("pools");
            Assert.Equal(2, lanePools.GetArrayLength());
            Assert.Equal("p1", lanePools[0].GetProperty("poolId").GetString());
            // Agents: newest CREATED first (the §12.1 ordering contract).
            var agents = d.GetProperty("agents").GetProperty("agents");
            Assert.Equal(3, agents.GetArrayLength());
            Assert.Equal("asg-newest", agents[0].GetProperty("assignmentId").GetString());
            Assert.Equal("asg-mid", agents[1].GetProperty("assignmentId").GetString());
            Assert.Equal("asg-oldest", agents[2].GetProperty("assignmentId").GetString());
            // Background processes: newest STARTED first (no running-first regrouping).
            var bg = d.GetProperty("processes").GetProperty("background").GetProperty("jobs");
            Assert.Equal("bg-new", bg[0].GetProperty("id").GetString());
            Assert.Equal("bg-old", bg[1].GetProperty("id").GetString());
            // Foreground section is independent: the running tool process shows.
            var fgRunning = d.GetProperty("processes").GetProperty("foreground").GetProperty("running");
            Assert.Equal(1, fgRunning.GetArrayLength());
            Assert.Equal("npm run dev", fgRunning[0].GetProperty("command").GetString());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    // ---- §15.E: every nonterminal root/child with waiting/suspended reasons -
    [Fact]
    public async Task EveryNonterminalRootAndChild_Visible_WithReasons()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            // a root (user-created) plus its delegated child — both nonterminal.
            Row("asg-root", "s1", AgentAssignmentLifecycle.Waiting, now.AddMinutes(-4), parentAgentId: null, reason: "delegated"),
            Row("asg-child", "s1", AgentAssignmentLifecycle.Running, now.AddMinutes(-3), parentAgentId: "agent-asg-root"),
            // queued (not yet admitted) — still a visible nonterminal row.
            Row("asg-queued", "s2", AgentAssignmentLifecycle.Queued, now.AddMinutes(-2), reason: "pool full"),
            // suspended with a reason — visible, never auto-resumed by the view.
            Row("asg-susp", "s3", AgentAssignmentLifecycle.Suspended, now.AddMinutes(-1), reason: "recovery-required"),
            // terminal rows are EXCLUDED from the live list.
            new AgentAssignmentRow("asg-done", "agent-done", null, "s4", null,
                AgentAssignmentLifecycle.Completed, AgentState.Idle, DeploymentExecutionMode.Pooled,
                "pool-1", null, "dep-1", "m1", "t", now.AddMinutes(-20), now.AddMinutes(-20), now.AddMinutes(-19), null),
        };
        var reg = new FakeRegistry();
        reg.Add("orchestration", new FakeOrch(rows));
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/agents");
            var agents = d.GetProperty("agents");
            // root + child + queued + suspended = 4 live rows; the terminal one is gone.
            Assert.Equal(4, agents.GetArrayLength());
            var ids = new List<string>();
            foreach (var r in agents.EnumerateArray())
                ids.Add(r.GetProperty("assignmentId").GetString()!);
            Assert.Contains("asg-root", ids);
            Assert.Contains("asg-child", ids);
            Assert.Contains("asg-queued", ids);
            Assert.Contains("asg-susp", ids);
            Assert.DoesNotContain("asg-done", ids);
            // The child row carries its parent link (the panel can group a tree).
            var child = agents.EnumerateArray().Single(r => r.GetProperty("assignmentId").GetString() == "asg-child");
            Assert.Equal("agent-asg-root", child.GetProperty("parentAgentId").GetString());
            // waiting/suspended reasons are exposed on the row.
            var waiting = agents.EnumerateArray().Single(r => r.GetProperty("lifecycle").GetString() == "waiting");
            Assert.Equal("delegated", waiting.GetProperty("reason").GetString());
            var susp = agents.EnumerateArray().Single(r => r.GetProperty("lifecycle").GetString() == "suspended");
            Assert.Equal("recovery-required", susp.GetProperty("reason").GetString());
            // A nonterminal row is flagged for the session-busy gate.
            Assert.True(waiting.GetProperty("nonTerminal").GetBoolean());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    // ---- §15.E: a missing service degrades ONLY its own section ------------
    [Fact]
    public async Task MissingService_DegradesOnlyItsOwnSection_ViewNeverFallsOver()
    {
        // Only the orchestrator is present: agents available, but lanes +
        // processes are unavailable — the whole payload still serializes
        // (never a 500) and the agents section is fully populated.
        var now = DateTimeOffset.UtcNow;
        var rows = new[] { Row("asg-1", "s1", AgentAssignmentLifecycle.Running, now) };
        var reg = new FakeRegistry();
        reg.Add("orchestration", new FakeOrch(rows));
        var (app, url) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(url + "/api/activity/work");
            Assert.True(d.GetProperty("agents").GetProperty("agentsAvailable").GetBoolean());
            Assert.False(d.GetProperty("lanes").GetProperty("available").GetBoolean());
            Assert.False(d.GetProperty("processes").GetProperty("background").GetProperty("available").GetBoolean());
            Assert.False(d.GetProperty("processes").GetProperty("foreground").GetProperty("available").GetBoolean());
            // The agents section still carries its rows.
            Assert.Equal(1, d.GetProperty("agents").GetProperty("agents").GetArrayLength());
            // And the surface is alive (a 200 with a full payload).
            Assert.True(d.TryGetProperty("ts", out _));
        }
        finally { await app.StopAsync(CancellationToken.None); }

        // The inverse: only process services present — the process section
        // works while the agents section reports unavailable (independently).
        var reg2 = new FakeRegistry();
        reg2.Add("background", new FakeJobs(new[]
        {
            new BackgroundJobInfo("bg-1", "bash", "node", BackgroundJobState.Running, now, null, null, null, "s1", "/w"),
        }));
        reg2.Add("foreground-processes", new FakeForeground());
        var (app2, url2) = await MakeAsync(reg2);
        try
        {
            var d = await GetJsonAsync(url2 + "/api/activity/work");
            Assert.False(d.GetProperty("agents").GetProperty("agentsAvailable").GetBoolean());
            Assert.True(d.GetProperty("processes").GetProperty("background").GetProperty("available").GetBoolean());
            Assert.Equal("bg-1", d.GetProperty("processes").GetProperty("background").GetProperty("jobs")[0].GetProperty("id").GetString());
        }
        finally { await app2.StopAsync(CancellationToken.None); }
    }

    // ---- §15.E: process stop routes through the background manager ----------
    [Fact]
    public async Task ProcessStop_DelegatesToBackgroundManager()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new FakeJobs(new[]
        {
            new BackgroundJobInfo("bg-1", "bash", "node", BackgroundJobState.Running, now, null, null, null, "s1", "/w"),
        });
        var reg = new FakeRegistry();
        reg.Add("background", jobs);
        var (app, url) = await MakeAsync(reg);
        var d = await PostAsync(url + "/api/activity/processes/background/bg-1/stop", "{}");
        try
        {
            Assert.True(d.GetProperty("ok").GetBoolean());
            Assert.Equal("bg-1", d.GetProperty("id").GetString());
            Assert.Equal(["bg-1"], jobs.Killed);
            // An unknown job id is a clean no-op (ok=false), never an error 500.
            var d2 = await PostAsync(url + "/api/activity/processes/background/nope/stop", "{}");
            Assert.False(d2.GetProperty("ok").GetBoolean());
        }
        finally { await app.StopAsync(CancellationToken.None); }

    }
}
