using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Activity;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 H (Package H, slice 3): the Activity plugin surface. Real Kestrel
/// (port 0) with the real ActivityWebApp, faking the three services it CONSUMES
/// through Abstractions (run-query, background-job manager, foreground tracker).
/// Verifies the acceptance criteria:
///   - Activity lists runs from every session (the Agents section);
///   - cancel / stop target only the selected item (a specific run / job);
///   - the Processes section merges background jobs + foreground commands;
///   - MISSING BackgroundTasks / Tools / Agent plugins do not break the surface
///     (each degrades to "unavailable"), so normal chat is never blocked.
/// </summary>
public class ActivitySurfaceTests
{
    private static readonly HttpClient Http = new();

    // ---- fakes ------------------------------------------------------------------

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

    /// <summary>Records which runs are cancelled so a test can assert "only the selected".</summary>
    private sealed class FakeRunner : IAgentRunner
    {
        public List<RunInfo> Runs { get; } = [];
        public List<string> Cancelled { get; } = [];
        public bool IsRunning => false;
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

    /// <summary>Fakes the background-job manager: a couple of jobs + a kill that records.</summary>
    private sealed class FakeJobs : IBackgroundJobManager
    {
        private readonly List<BackgroundJobInfo> _jobs;
        public List<string> Killed { get; } = [];
        public FakeJobs(IEnumerable<BackgroundJobInfo> jobs) => _jobs = jobs.ToList();
        public ValueTask<BackgroundJobInfo> StartAsync(
            string shellId, string command, string workingDirectory, JsonElement? options,
            JobOwnership? ownership = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken ct = default)
            => ValueTask.FromResult(_jobs.FirstOrDefault(j => j.JobId == jobId));
        public ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<BackgroundJobInfo>>(_jobs);
        public ValueTask<bool> KillAsync(string jobId, CancellationToken ct = default)
        {
            var found = _jobs.Any(j => j.JobId == jobId);
            if (found) Killed.Add(jobId);
            return ValueTask.FromResult(found);
        }
        public ValueTask<BackgroundJobOutput> GetOutputAsync(string jobId, int offset, CancellationToken ct = default)
            => ValueTask.FromResult(new BackgroundJobOutput(jobId, BackgroundJobState.Running, null, "output-" + jobId, offset, false));
    }

    /// <summary>Fakes the foreground-process tracker with a running + a recent process.</summary>
    private sealed class FakeForeground : IForegroundProcessTracker
    {
        private readonly List<ForegroundProcessInfo> _running;
        private readonly List<ForegroundProcessInfo> _recent;
        public FakeForeground()
        {
            var now = DateTimeOffset.UtcNow;
            _running = [new ForegroundProcessInfo("bash", "npm run dev", "/ws", "sess-1", 4321, now.AddMinutes(-1), null, null)];
            _recent = [new ForegroundProcessInfo("powershell", "dotnet test", "/ws", "sess-2", 4322, now.AddMinutes(-5), 0, now.AddMinutes(-4))];
        }
        public void Started(string toolShellId, string command, string? workingDirectory, string? sessionId, int processId) { }
        public void Finished(int processId, int? exitCode) { }
        public IReadOnlyList<ForegroundProcessInfo> Running() => _running;
        public IReadOnlyList<ForegroundProcessInfo> Recent(int max = 50) => _recent;
    }

    // ---- harness ----------------------------------------------------------------

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

    private static async Task<JsonElement> PostJsonAsync(string url)
    {
        var resp = await Http.PostAsync(url, null);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---- tests ------------------------------------------------------------------

    [Fact]
    public async Task PanelServesPage_AndListsRunsFromEverySession()
    {
        var reg = new FakeRegistry();
        var runner = new FakeRunner();
        runner.Runs.Add(new RunInfo("run-1", "sess-A", "m1", AgentState.CallingModel, DateTimeOffset.UtcNow.AddSeconds(-10), null, RunState.Running));
        runner.Runs.Add(new RunInfo("run-2", "sess-B", "m2", AgentState.Idle, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-59), RunState.Completed));
        reg.Add("runner", runner);
        var (app, baseUrl) = await MakeAsync(reg);
        try
        {
            // The panel page is served from the plugin assembly.
            var page = await Http.GetStringAsync(baseUrl + "/panel/activity");
            Assert.Contains("Activity", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/api/activity/agents", page);

            // The Agents section lists runs from EVERY session (two distinct sessions).
            var d = await GetJsonAsync(baseUrl + "/api/activity/agents");
            Assert.True(d.GetProperty("available").GetBoolean());
            var runs = d.GetProperty("runs");
            Assert.Equal(2, runs.GetArrayLength());
            Assert.Equal("sess-A", runs[0].GetProperty("sessionId").GetString());
            Assert.Equal("sess-B", runs[1].GetProperty("sessionId").GetString());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Processes_MergesBackgroundAndForeground_AndStopTargetsOnlySelected()
    {
        var reg = new FakeRegistry();
        var jobs = new FakeJobs([
            new BackgroundJobInfo("bg-1", "bash", "node server", BackgroundJobState.Running, DateTimeOffset.UtcNow.AddSeconds(-30), null, null, null,
                SessionId: "sess-1", WorkingDirectory: "/ws"),
            new BackgroundJobInfo("bg-2", "bash", "build", BackgroundJobState.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), 0, null,
                SessionId: "sess-1", WorkingDirectory: "/ws"),
        ]);
        reg.Add("background", jobs);
        reg.Add("foreground-processes", new FakeForeground());
        var (app, baseUrl) = await MakeAsync(reg);
        try
        {
            var d = await GetJsonAsync(baseUrl + "/api/activity/processes");
            // Background section: both jobs, with the ownership captured at start.
            Assert.True(d.GetProperty("background").GetProperty("available").GetBoolean());
            var bg = d.GetProperty("background").GetProperty("jobs");
            Assert.Equal(2, bg.GetArrayLength());
            Assert.Equal("bg-1", bg[0].GetProperty("id").GetString());
            Assert.Equal("sess-1", bg[0].GetProperty("sessionId").GetString());
            Assert.Equal("/ws", bg[0].GetProperty("workingDirectory").GetString());
            // Foreground section: the running command + a recent one.
            Assert.True(d.GetProperty("foreground").GetProperty("available").GetBoolean());
            var fgRun = d.GetProperty("foreground").GetProperty("running");
            var fgRec = d.GetProperty("foreground").GetProperty("recent");
            Assert.Equal(1, fgRun.GetArrayLength());
            Assert.Equal("npm run dev", fgRun[0].GetProperty("command").GetString());
            Assert.Equal(1, fgRec.GetArrayLength());
            Assert.Equal(4322, fgRec[0].GetProperty("processId").GetInt32());
            Assert.True(fgRun[0].GetProperty("isRunning").GetBoolean());

            // Stop targets ONLY the selected job (not the other, not foreground).
            var ok = await PostJsonAsync(baseUrl + "/api/activity/processes/background/bg-2/stop");
            Assert.True(ok.GetProperty("ok").GetBoolean());
            Assert.Equal(["bg-2"], jobs.Killed);
            // A background job's output is served on demand (bounded).
            var outp = await GetJsonAsync(baseUrl + "/api/activity/processes/background/bg-1/output?chars=500");
            Assert.True(outp.GetProperty("available").GetBoolean());
            Assert.Equal("output-bg-1", outp.GetProperty("text").GetString());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancel_TargetsOnlyTheSelectedRun()
    {
        var reg = new FakeRegistry();
        var runner = new FakeRunner();
        runner.Runs.Add(new RunInfo("run-1", "sess-A", "m1", AgentState.CallingModel, DateTimeOffset.UtcNow, null, RunState.Running));
        runner.Runs.Add(new RunInfo("run-2", "sess-B", "m2", AgentState.Idle, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, RunState.Completed));
        reg.Add("runner", runner);
        var (app, baseUrl) = await MakeAsync(reg);
        try
        {
            var ok = await PostJsonAsync(baseUrl + "/api/activity/agents/run-2/cancel");
            Assert.True(ok.GetProperty("ok").GetBoolean());
            // Only the selected run was signalled — the other was not touched.
            Assert.Equal(["run-2"], runner.Cancelled);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MissingPlugins_DegradeToUnavailable_NeverAnError()
    {
        // NO services registered at all — BackgroundTasks, Tools and the agent
        // plugin are all absent. The Activity surface must still start and serve
        // "unavailable" sections, so normal chat is never blocked.
        var reg = new FakeRegistry();
        var (app, baseUrl) = await MakeAsync(reg);
        try
        {
            // The panel still serves.
            var page = await Http.GetStringAsync(baseUrl + "/panel/activity");
            Assert.Contains("Activity", page, StringComparison.OrdinalIgnoreCase);

            var agents = await GetJsonAsync(baseUrl + "/api/activity/agents");
            Assert.False(agents.GetProperty("available").GetBoolean());
            Assert.Equal(0, agents.GetProperty("runs").GetArrayLength());

            var procs = await GetJsonAsync(baseUrl + "/api/activity/processes");
            Assert.False(procs.GetProperty("background").GetProperty("available").GetBoolean());
            Assert.False(procs.GetProperty("foreground").GetProperty("available").GetBoolean());

            // Cancelling with no runner is a graceful no-op (ok:false), not a 500.
            var cancel = await PostJsonAsync(baseUrl + "/api/activity/agents/run-1/cancel");
            Assert.False(cancel.GetProperty("ok").GetBoolean());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// Acceptance: "Activity unload/reload leaves work running." The plugin is
    /// purely presentational — its Stop/Unload close ITS OWN Kestrel and
    /// unregister ITS OWN panel, but never touch the runner or jobs it merely
    /// shows. After a reload a fresh generation re-serves and sees the same work.
    /// </summary>
    [Fact]
    public async Task Unload_Reload_LeavesWorkRunning()
    {
        var reg = new FakeRegistry();
        var panels = new FakePanels();
        var runner = new FakeRunner();
        var now = DateTimeOffset.UtcNow;
        runner.Runs.Add(new RunInfo("run-1", "sess-A", "m1", AgentState.CallingModel, now.AddSeconds(-20), null, RunState.Running));
        var jobs = new FakeJobs([new BackgroundJobInfo("bg-1", "bash", "node server", BackgroundJobState.Running, now.AddSeconds(-30), null, null, null, SessionId: "sess-A", WorkingDirectory: "/ws")]);
        reg.Add("runner", runner);
        reg.Add("background", jobs);

        var cfg = JsonDocument.Parse("{\"port\":0}").RootElement.Clone();
        var plugin = new ActivityPlugin();

        // Generation 1: load + start; the panel re-registers with the bound URL.
        await plugin.LoadAsync(new FakeContext(reg, panels, cfg), CancellationToken.None);
        Assert.Equal(1, panels.Registered.Count);
        await plugin.StartAsync(CancellationToken.None);

        // Unload: Kestrel closes, panel unregisters, but the WORK is untouched.
        await plugin.StopAsync(CancellationToken.None);
        await plugin.UnloadAsync(CancellationToken.None);
        Assert.Equal(0, panels.Registered.Count);
        Assert.Empty(jobs.Killed);
        Assert.Empty(runner.Cancelled);
        Assert.Single(runner.Runs);
        Assert.Equal(BackgroundJobState.Running, (await jobs.GetAsync("bg-1")).State);

        // Reload: a new generation re-registers and re-serves, seeing the same work.
        await plugin.LoadAsync(new FakeContext(reg, panels, cfg), CancellationToken.None);
        await plugin.StartAsync(CancellationToken.None);
        try
        {
            var gen2 = panels.Registered.Single();
            var origin = gen2.EntryUrl[..gen2.EntryUrl.LastIndexOf("/panel/activity")];
            var agents = await GetJsonAsync(origin + "/api/activity/agents");
            Assert.True(agents.GetProperty("available").GetBoolean());
            Assert.Equal(1, agents.GetProperty("runs").GetArrayLength());
            Assert.Equal("sess-A", agents.GetProperty("runs")[0].GetProperty("sessionId").GetString());
            Assert.Empty(runner.Cancelled);
            Assert.Empty(jobs.Killed);
        }
        finally { await plugin.StopAsync(CancellationToken.None); await plugin.UnloadAsync(CancellationToken.None); }
    }
}