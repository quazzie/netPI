using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Diagnostics;
using NetPI.Host.Events;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §47: first-class diagnostics surface.
/// 1) DiagApp end-to-end: real Kestrel on a free port + real EventBus, fake
///    host services — every /api/diag endpoint verified.
/// 2) AiProxyProvider publishes ModelRequestDiagnostics for the wire decision.
/// </summary>
public class DiagnosticsSurfaceTests
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
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull
            => throw new NotSupportedException();
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeContext(
        FakeRegistry services, IEventBus events, IWebPanelRegistry panels,
        JsonElement? config = null) : IPluginContext
    {
        public PluginInfo Info { get; } = new("netpi.diagnostics", "Diagnostics Test", "0.1.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => panels;
        public IEventBus Events => events;
        public JsonElement OwnConfig => config ?? JsonDocument.Parse("{}").RootElement.Clone();
        public IPluginLogger Log => new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    private sealed class FakeFacade : IPluginManagerFacade
    {
        public IReadOnlyList<PluginStatusSnapshot> GetStatus() => new List<PluginStatusSnapshot>
        {
            new("netpi.test", "Test Plugin", "0.1.0", "Active", 2, null, "PluginIdle", 0, null),
        };
        public ValueTask<bool> ReloadAsync(string pluginId, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<int> ReloadAllAsync(CancellationToken ct = default) => ValueTask.FromResult(1);
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public IReadOnlyList<ModelInfo> Models { get; } = [new ModelInfo("qwen3.8-27b", "aiProxy", "Qwen", true, true)];
        public bool IsStale { get; } = true;
        public DateTimeOffset? LastRefreshedAt { get; } = null;
        public ValueTask<IReadOnlyList<ModelInfo>> RefreshAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Models);
    }

    private sealed class FakeStore : ISessionStore
    {
        private readonly SessionInfo _info;
        private readonly List<SessionEntry> _entries;

        public FakeStore(SessionInfo info, List<SessionEntry> entries)
        {
            _info = info;
            _entries = entries;
        }

        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(_info);
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<SessionInfo?>(id == _info.Id ? _info : null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(new List<SessionInfo> { _info }.Skip(offset).Take(count).ToList());
        public ValueTask<int> CountAsync(CancellationToken ct = default)
            => ValueTask.FromResult(1);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(_entries.Skip(offset).Take(count).ToList());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<SessionEntry?> LatestCompactionAsync(string sessionId, CancellationToken ct = default)
            => ValueTask.FromResult(_entries.Where(e => e.Kind == EntryKind.Compaction)
                .OrderByDescending(e => e.Sequence).FirstOrDefault());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(
                _entries.Where(e => e.Sequence > afterSequence).OrderBy(e => e.Sequence).Take(count).ToList());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(
                _entries.OrderByDescending(e => e.Sequence).Take(count).OrderBy(e => e.Sequence).ToList());
    }

    private sealed class FakeAgent : IAgentRuntime
    {
        private sealed class FakeState : IAgentState
        {
            public AgentState State => AgentState.Idle;
            public bool IsRunning => false;
            public string? ActiveSessionId => "sess-1";
            public bool IsIdle => true;
        }
        public IAgentState State { get; } = new FakeState();
    }

    private sealed class FakeJobs : IBackgroundJobManager
    {
        public ValueTask<BackgroundJobInfo> StartAsync(string shellId, string command, string workingDirectory, JsonElement? options, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken ct = default)
            => ValueTask.FromResult<BackgroundJobInfo?>(null);
        public ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<BackgroundJobInfo>>([]);
        public ValueTask<BackgroundJobOutput> GetOutputAsync(string jobId, int offset, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> KillAsync(string jobId, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    // ---- e2e ------------------------------------------------------------------

    private static (DiagApp app, EventBus bus, FakeRegistry reg) MakeApp(string tempRoot)
    {
        var logDir = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllLines(Path.Combine(logDir, $"netpi-{DateTime.Now:yyyy-MM-dd}.log"),
        [
            "2026-09-20T00:00:00+02:00 | Information | netPI.Agent | - | host started",
            "2026-09-20T00:01:00+02:00 | Warning | netPI.Web | - | something warny",
            "2026-09-20T00:02:00+02:00 | Error | netPI.Storage.Sqlite | - | disk error",
        ]);

        var bus = new EventBus();
        var reg = new FakeRegistry();
        reg.Add("plugins", new FakeFacade());
        reg.Add("catalog", new FakeCatalog());
        var store = new FakeStore(
            new SessionInfo("sess-1", "C:\\w", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, 2, "Test"),
            [
                new SessionEntry("e1", "sess-1", EntryKind.Message,
                    new AgentMessage("m1", MessageRole.User, [new TextPart("hello")], DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, 1),
                new SessionEntry("e2", "sess-1", EntryKind.Message,
                    new AgentMessage("m2", MessageRole.Assistant, [new TextPart("hi there")], DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, 2),
            ]);
        reg.Add("sessions", store);
        reg.Add("agent", new FakeAgent());
        reg.Add("background", new FakeJobs());
        var ctx = new FakeContext(reg, bus, new NetPI.Host.Services.WebPanelRegistry());

        var app = new DiagApp(ctx, new NullLogger(), 0, tempRoot, urls: "http://127.0.0.1:0");
        return (app, bus, reg);
    }

    private static async Task<JsonElement> GetJsonAsync(string baseUrl, string path)
    {
        var resp = await Http.GetAsync(baseUrl + path);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task AllEndpointsServed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netpi-diag-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (app, bus, _) = MakeApp(tempRoot);
            await app.StartAsync(CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(app.BoundUrl));

            // Feed the bus: one lifecycle event + one provider diagnostic.
            await bus.PublishAsync(new AgentEvent("ev1", AgentEventType.ModelStreamEvent,
                DateTimeOffset.UtcNow, "sess-1",
                JsonDocument.Parse("{\"kind\":\"text-delta\",\"text\":\"hello\"}").RootElement.Clone()), CancellationToken.None);
            await bus.PublishAsync(new ModelRequestDiagnostics("sess-1", "m", "auto", "responses", "responses", true, false, null),
                CancellationToken.None);
            await Task.Delay(50); // bus dispatch is async; give it a tick

            var baseUrl = app.BoundUrl!.TrimEnd('/');

            // /bootstrap
            var boot = await GetJsonAsync(baseUrl, "/bootstrap");
            Assert.True(boot.GetProperty("ok").GetBoolean());

            // /panel/diagnostics — self-hosted live page
            var html = await Http.GetStringAsync(baseUrl + "/panel/diagnostics");
            Assert.Contains("/api/diag/overview", html);
            Assert.Contains("<!DOCTYPE html>", html);

            // /api/diag/overview — every subsystem present
            var ov = await GetJsonAsync(baseUrl, "/api/diag/overview");
            Assert.Equal(Environment.ProcessId, ov.GetProperty("host").GetProperty("pid").GetInt32());
            var plugins = ov.GetProperty("plugins").EnumerateArray().ToList();
            Assert.Single(plugins);
            Assert.Equal("netpi.test", plugins[0].GetProperty("id").GetString());
            Assert.Equal("qwen3.8-27b", ov.GetProperty("models").GetProperty("models")[0].GetProperty("id").GetString());
            Assert.Equal("sess-1", ov.GetProperty("sessions")[0].GetProperty("id").GetString());
            Assert.Equal("Idle", ov.GetProperty("agent").GetProperty("state").GetString());

            // /api/diag/logs — newest file tail, level filter
            var logs = await GetJsonAsync(baseUrl, "/api/diag/logs?lines=10");
            Assert.Equal(3, logs.GetProperty("totalLines").GetInt32());
            var warns = await GetJsonAsync(baseUrl, "/api/diag/logs?lines=10&level=Warning");
            Assert.Equal(1, warns.GetProperty("totalLines").GetInt32());
            Assert.Equal(1, warns.GetProperty("lines").GetArrayLength());

            // /api/diag/events — bus ring
            var evs = await GetJsonAsync(baseUrl, "/api/diag/events?limit=10");
            var evList = evs.GetProperty("events").EnumerateArray().ToList();
            Assert.Single(evList);
            Assert.Equal("ModelStreamEvent", evList[0].GetProperty("type").GetString());
            Assert.Equal("stream text-delta +5", evList[0].GetProperty("summary").GetString());

            // /api/diag/model — provider wire-decision ring
            var mdl = await GetJsonAsync(baseUrl, "/api/diag/model?limit=10");
            var reqs = mdl.GetProperty("requests").EnumerateArray().ToList();
            Assert.Single(reqs);
            Assert.True(reqs[0].GetProperty("chained").GetBoolean());
            Assert.False(reqs[0].GetProperty("fallback").GetBoolean());
            Assert.Equal("responses", reqs[0].GetProperty("wireServed").GetString());

            // /api/diag/sessions + /api/diag/sessions/{id} — history
            var sess = await GetJsonAsync(baseUrl, "/api/diag/sessions");
            Assert.Single(sess.GetProperty("sessions").EnumerateArray());
            var one = await GetJsonAsync(baseUrl, "/api/diag/sessions/sess-1?count=10");
            Assert.Equal("Test", one.GetProperty("session").GetProperty("title").GetString());
            var entries = one.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(2, entries.Count);
            Assert.Equal("hello", entries[0].GetProperty("text").GetString());
            Assert.Equal("hi there", entries[1].GetProperty("text").GetString());

            // unknown session → 404
            var missing = await Http.GetAsync(baseUrl + "/api/diag/sessions/nope");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            await app.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            var after = await Http.GetStringAsync(baseUrl + "/");
            Assert.Fail("stopped server should not answer: " + after);
        }
        catch (HttpRequestException)
        {
            // expected: server stopped, connection refused
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // ---- provider diagnostics ---------------------------------------------------

    private sealed class CapBus : IEventBus
    {
        public List<ModelRequestDiagnostics> ModelEvents { get; } = [];
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
            => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            if (@event is ModelRequestDiagnostics d) ModelEvents.Add(d);
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Routes /v1/models + /v1/responses + /v1/chat/completions.</summary>
    private sealed class WireHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _route;
        public WireHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync().Result);
            var (status, resp) = _route(request, body);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(resp, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private const string Catalog = "{\"data\":[{\"id\":\"m\"}]}";
    private const string RespOk =
        "event: response.created\ndata: {\"response\":{\"id\":\"r1\"},\"type\":\"response.created\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\"hi\",\"item_id\":\"msg_1\"}\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"r1\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}},\"type\":\"response.completed\"}\n";
    private const string ChatOk =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n" +
        "data: [DONE]\n";

    private static Func<HttpRequestMessage, string, (HttpStatusCode, string)> OkRoute(string respBody,
        HttpStatusCode respStatus = HttpStatusCode.OK)
    {
        return (req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/v1/models", StringComparison.Ordinal)) return (HttpStatusCode.OK, Catalog);
            if (url.EndsWith("/v1/responses", StringComparison.Ordinal))
                return (respStatus, body.Contains("\"stream\":false", StringComparison.Ordinal) ? "{\"response\":{\"id\":\"probe\"}}" : respBody);
            if (url.EndsWith("/v1/chat/completions", StringComparison.Ordinal)) return (HttpStatusCode.OK, ChatOk);
            return (HttpStatusCode.NotFound, "{}");
        };
    }

    [Fact]
    public async Task ResponsesWireCleans_PublishesNonFallbackDiagnostic()
    {
        var http = new HttpClient(new WireHandler(OkRoute(RespOk))) { BaseAddress = new Uri("http://test") };
        var bus = new CapBus();
        var p = new AiProxyProvider(http, "http://test", new NullLogger(), "responses", bus);
        await p.RefreshAsync(CancellationToken.None);
        await p.WaitForProbeAsync();

        await foreach (var _ in p.RunAsync(new ModelRequest
        {
            ModelId = "m",
            SessionId = "s1",
            Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        }, CancellationToken.None)) { }

        Assert.Single(bus.ModelEvents);
        var d = bus.ModelEvents[0];
        Assert.Equal("responses", d.WireServed);
        Assert.False(d.Fallback);
        Assert.False(d.Chained);
        Assert.Null(d.FailureReason);
    }

    [Fact]
    public async Task ResponsesWireFailsBeforeContent_PublishesFallbackDiagnostic()
    {
        // Probe succeeds (model supports responses) but the RUN stream fails
        // Probe succeeds (model supports responses) but the RUN stream ends
        // in response.failed before any content — the exact nInfer shape
        // ("model name=… failed to load") → transparent chat fallback.
        const string RespFailed =
            "event: response.created\ndata: {\"response\":{\"id\":\"rf1\"},\"type\":\"response.created\"}\n" +
            "event: response.failed\ndata: {\"response\":{\"id\":\"rf1\",\"error\":{\"message\":\"model name=m failed to load\"}},\"type\":\"response.failed\"}\n";
        var http = new HttpClient(new WireHandler((req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/v1/models", StringComparison.Ordinal)) return (HttpStatusCode.OK, Catalog);
            if (url.EndsWith("/v1/responses", StringComparison.Ordinal))
                return (HttpStatusCode.OK, body.Contains("\"stream\":false", StringComparison.Ordinal)
                    ? "{\"response\":{\"id\":\"probe\"}}"
                    : RespFailed);
            return (HttpStatusCode.OK, ChatOk);
        })) { BaseAddress = new Uri("http://test") };
        var bus = new CapBus();
        var p = new AiProxyProvider(http, "http://test", new NullLogger(), "responses", bus);
        await p.RefreshAsync(CancellationToken.None);
        await p.WaitForProbeAsync();

        await foreach (var _ in p.RunAsync(new ModelRequest
        {
            ModelId = "m",
            SessionId = "s1",
            Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        }, CancellationToken.None)) { }

        Assert.Single(bus.ModelEvents);
        var d = bus.ModelEvents[0];
        Assert.Equal("chat", d.WireServed);
        Assert.True(d.Fallback);
        Assert.False(d.Chained);
        Assert.NotNull(d.FailureReason);
    }
}
