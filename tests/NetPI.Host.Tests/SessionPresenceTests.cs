using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Sessions;

namespace NetPI.Host.Tests;

/// <summary>
/// What a window has open: a heartbeat keeps a session open for 90 s, a close drops it at once, and a session
/// that waits on the user (a question, a plan) is open no matter what. The idle compaction sweeps exactly these.
/// </summary>
public static class SessionPresenceTests
{
    /// <summary>The smallest service registry the presence service asks of (the host's is internal to the host project).</summary>
    private sealed class Registry : IServiceRegistry
    {
        private readonly List<(Type Type, object Instance)> _items = [];
        public IDisposable Register<T>(T instance, int priority = 0) where T : class
        {
            lock (_items) _items.Add((typeof(T), (object)instance));
            return new Disposed(() => { lock (_items) _items.RemoveAll(i => ReferenceEquals(i.Instance, instance)); });
        }
        public T? Get<T>() where T : class => _items.FirstOrDefault(i => i.Type == typeof(T)).Instance as T;
        public IReadOnlyList<T> GetAll<T>() where T : class => _items.Where(i => i.Type == typeof(T)).Select(i => (T)i.Instance).ToList();
        private sealed class Disposed(Action a) : IDisposable
        {
            private Action? _action = a;
            public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }

    /// <summary>A runtime whose waits a test sets by hand.</summary>
    private sealed class Runtime : IAgentRuntime
    {
        public List<WaitingCall> All = [];
        public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => [];
        public AgentInfo? Get(string agentId) => null;
        public AgentInfo? GetBySession(string sessionId) => null;
        public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentWaitResult> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default, bool any = false) => throw new NotSupportedException();
        public IReadOnlyList<WaitingCall> Waits(string? sessionId = null, string? kind = null) =>
            All.Where(w => (sessionId is null || w.SessionId == sessionId) && (kind is null || w.Kind == kind)).ToList();
        public Task<bool> AbortAsync(string agentOrSessionId) => Task.FromResult(false);
        public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => [];
        public bool RemoveQueued(string sessionId, string inputId) => false;

        public void Wait(string sessionId, string kind = "ask") => All.Add(new WaitingCall
        {
            Id = "w" + All.Count, Kind = kind, SessionId = sessionId, AgentId = "agt", CallId = "c", Tool = "ask_user",
        });
    }

    private static WaitingCall W(string sessionId, string kind) => new()
    {
        Id = "w", Kind = kind, SessionId = sessionId, AgentId = "agt", CallId = "c", Tool = "ask_user",
    };

    public static void Register(TestRunner r)
    {
        r.Add("presence: a heartbeat keeps a session open until it expires, and a close drops it at once", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var presence = new SessionPresence(new Registry(), () => now);
            presence.MarkOpen("s1");
            Check.True(presence.IsOpen("s1"), "a fresh heartbeat is open");
            Check.True(presence.OpenSessions().Contains("s1"), "in the open set");
            now += TimeSpan.FromSeconds(89);
            Check.True(presence.IsOpen("s1"), "89 s is inside the 90 s expiry");
            now += TimeSpan.FromSeconds(2);
            Check.False(presence.IsOpen("s1"), "the heartbeat expired: a window that died is not counted");
            Check.Equal(0, presence.OpenSessions().Count, "the open set is empty");

            presence.MarkOpen("s2");
            presence.MarkClosed("s2");
            Check.False(presence.IsOpen("s2"), "a close drops the session at once");
        });

        r.Add("presence: a session that waits on the user is open without a heartbeat, and stops when the wait ends", () =>
        {
            var runtime = new Runtime();
            var registry = new Registry();
            registry.Register<IAgentRuntime>(runtime);
            var presence = new SessionPresence(registry, () => DateTimeOffset.UtcNow);

            Check.False(presence.IsOpen("s1"), "nothing is open yet");
            runtime.Wait("s1", "ask");
            Check.True(presence.IsOpen("s1"), "a question keeps the session open");
            Check.True(presence.OpenSessions().Contains("s1"), "in the open set through its wait");

            runtime.All.Clear();
            Check.False(presence.IsOpen("s1"), "the answered question stops counting");

            foreach (var kind in new[] { "ask", "guard", "plan", "plan-enter", "schedule" })
            {
                runtime.All.Add(W("s1", kind));
                Check.True(presence.IsOpen("s1"), $"a {kind} wait keeps the session open");
                runtime.All.Clear();
            }
        });

        r.Add("presence: expired heartbeats are pruned, so the map cannot grow with every session that ever opened", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var presence = new SessionPresence(new Registry(), () => now);
            for (var i = 0; i < 100; i++) presence.MarkOpen("s" + i);
            Check.Equal(100, presence.OpenSessions().Count);
            now += TimeSpan.FromMinutes(2);
            Check.Equal(0, presence.OpenSessions().Count, "all expired");
            presence.MarkOpen("live");
            Check.Equal(1, presence.OpenSessions().Count, "the live one stays");
        });

        r.Add("presence: the session.presence RPC marks a session open and closed", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"));
            var res = await server.Rpc.InvokeAsync("session.presence", new { sessionId = "s-x", open = true });
            var json = NetPiJson.ToNode(res) as JsonObject;
            Check.Equal(true, json?["open"]?.GetValue<bool>(), "the heartbeat marks it open");
            Check.True(json?["openSessions"]?.AsArray()?.Any(n => n?.GetValue<string>() == "s-x") == true, "the open set names it");

            res = await server.Rpc.InvokeAsync("session.presence", new { sessionId = "s-x", open = false });
            json = NetPiJson.ToNode(res) as JsonObject;
            Check.Equal(false, json?["open"]?.GetValue<bool>(), "the close drops it");
        });
    }
}
