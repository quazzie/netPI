using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Lanes;
using NetPI.Provider.AiProxy;
using NetPI.Storage.Sqlite;
using NetPI.Tools;
using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

public sealed class ImagePipelineTests
{
    private static byte[] Png(params byte[] tail) => [137, 80, 78, 71, 13, 10, 26, 10, .. tail];

    [Fact]
    public void PromptImages_RejectMismatchedMimeAndOversizePayload()
    {
        var mismatch = Payload(Png(1), "image/jpeg");
        var ex = Assert.Throws<ArgumentException>(() => WebApp.ParsePromptImages(mismatch));
        Assert.Contains("matching content", ex.Message, StringComparison.OrdinalIgnoreCase);

        var oversized = Payload(Png(new byte[ImageContent.MaxPromptBytes]));
        var sizeError = Assert.Throws<ArgumentException>(() => WebApp.ParsePromptImages(oversized));
        Assert.Contains("600 KiB", sizeError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadTool_ReturnsImagePart()
    {
        var dir = Directory.CreateTempSubdirectory("netpi-read-image-");
        try
        {
            var bytes = Png(1, 2, 3);
            var path = Path.Combine(dir.FullName, "sample.png");
            await File.WriteAllBytesAsync(path, bytes);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { path = "sample.png" }));

            var result = await new ReadTool().ExecuteAsync(new ToolContext(doc.RootElement, dir.FullName, "s1", null), CancellationToken.None);

            var image = Assert.Single(result.Parts.OfType<ImagePart>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(bytes, image.Data);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task SqliteSessionStore_RoundTripsImageMessage()
    {
        var dir = Directory.CreateTempSubdirectory("netpi-image-store-");
        try
        {
            using var store = new SqliteSessionStore(Path.Combine(dir.FullName, "sessions.db"));
            var session = await store.CreateAsync(null);
            var bytes = Png(4, 5, 6);
            var message = new AgentMessage("image-message", MessageRole.User,
                [new TextPart("look"), new ImagePart("image/png", bytes)], DateTimeOffset.UtcNow);
            await store.AppendAsync(new SessionEntry(message.Id, session.Id, EntryKind.Message, message, null, DateTimeOffset.UtcNow));

            var restored = Assert.Single(await store.ReadRecentAsync(session.Id, 10));
            var image = Assert.Single(restored.Message!.Parts.OfType<ImagePart>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(bytes, image.Data);
            // release the SQLite file before the finally-block directory delete
            SqliteConnection.ClearAllPools(); // release pooled handles before the directory delete
        }
        finally { SqliteConnection.ClearAllPools(); try { dir.Delete(true); } catch { /* pooled SQLite handle may outlive the test; best-effort */ } }
    }

    [Fact]
    public async Task QueuedRun_PersistsAndReplaysImageIntoProviderTranscript()
    {
        var dir = Directory.CreateTempSubdirectory("netpi-run-image-");
        try
        {
            using var store = new SqliteSessionStore(Path.Combine(dir.FullName, "sessions.db"));
            var firstSession = await store.CreateAsync(null);
            var imageSession = await store.CreateAsync(null);
            var scheduler = new LaneScheduler("image-test", new TestLogger());
            scheduler.RegisterPool("images", "deploy", "vision", LaneCapacityMode.Manual, 1, true);
            var provider = new GatedProvider();
            var context = new TestContext();
            context.Add("provider", provider);
            context.Add("sessions", store);
            context.Add("deployments", new VisionPolicy());
            context.Add("lanes", scheduler);
            var runner = new AgentRunner(new AgentRuntime(context), context, maxConcurrentRuns: 8);

            var first = await runner.StartRunAsync(new AgentRunRequest(firstSession.Id, null, "vision", "occupy lane"));
            Assert.Equal(RunDisposition.Admitted, first.Disposition);
            await provider.WaitStarted(first.RunId!);

            var bytes = Png(9, 8, 7);
            var queued = await runner.StartRunAsync(new AgentRunRequest(imageSession.Id, null, "vision", "describe this",
                RunId: "image-queued-run", Images: [new ImagePart("image/png", bytes)]));
            Assert.Equal(RunDisposition.Queued, queued.Disposition);
            Assert.DoesNotContain(provider.Requests, r => r.RunId == queued.RunId);

            var persisted = Assert.Single(await store.ReadRecentAsync(imageSession.Id, 10));
            Assert.Equal(bytes, Assert.Single(persisted.Message!.Parts.OfType<ImagePart>()).Data);

            Assert.True(provider.Release(first.RunId!));
            var imageRequest = await provider.WaitStarted(queued.RunId!);
            var replayedImage = Assert.Single(imageRequest.Messages.Last(m => m.Role == MessageRole.User).Parts.OfType<ImagePart>());
            Assert.Equal(bytes, replayedImage.Data);
            Assert.True(provider.Release(queued.RunId!));
            // release the SQLite file before the finally-block directory delete
            SqliteConnection.ClearAllPools(); // release pooled handles before the directory delete
        }
        finally { SqliteConnection.ClearAllPools(); try { dir.Delete(true); } catch { /* a background run may still touch the store briefly; best-effort */ } }
    }

    private static JsonElement Payload(byte[] bytes, string mime = "image/png")
        => JsonDocument.Parse(JsonSerializer.Serialize(new { images = new[] { new { data = Convert.ToBase64String(bytes), mimeType = mime } } })).RootElement.Clone();

    private sealed class GatedProvider : IModelProvider
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ModelRequest>> _entered = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _release = new(StringComparer.Ordinal);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var runId = request.RunId ?? "missing";
            Requests.Enqueue(request);
            _entered.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(request);
            var gate = _release.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            yield return new ModelStarted(request.ModelId);
            using var registration = cancellationToken.Register(() => gate.TrySetResult());
            await gate.Task;
            yield return new ModelCompleted(new AgentMessage("assistant-" + runId, MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow));
        }

        public async Task<ModelRequest> WaitStarted(string runId)
            => await _entered.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public bool Release(string runId) => _release.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private sealed class VisionPolicy : IDeploymentPolicySource
    {
        public DeploymentPolicy? PolicyFor(string modelId) => modelId == "vision"
            ? new DeploymentPolicy("vision", DeploymentExecutionMode.Pooled, "images", "deploy") : null;
    }

    private sealed class TestContext : IPluginContext
    {
        private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
        public PluginInfo Info => new("test", "test", "1");
        public IServiceRegistry Services => new Registry(_services);
        public IEventBus Events { get; } = new TestBus();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new TestLogger();
        public IValueLease<object> LeaseSelf() => new ObjectLease(this);
        public void Add(string id, object service) => _services[id] = service;

        private sealed class Registry(Dictionary<string, object> services) : IServiceRegistry
        {
            // Honor the IServiceRegistry contract: a missing id throws
            // ServiceUnavailableException (AgentRunner.Resolve catches exactly
            // this and degrades to null), not the raw KeyNotFoundException.
            private object Get(string id)
                => services.TryGetValue(id, out var o) ? o : throw new ServiceUnavailableException(id, "not registered");

            public IDisposable Register<T>(string id, T instance) where T : notnull { services[id] = instance; return new Lease<T>(instance); }
            public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>((T)Get(id));
            public IValueLease<object> Acquire(string id, Type expectedType) => new Lease<object>(Get(id));
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
            public T Resolve<T>(string id) where T : notnull => (T)Get(id);
        }
    }

    private sealed class ObjectLease : IValueLease<object>
    {
        public object Value { get; }
        public ObjectLease(object value) => Value = value;
        public void Dispose() { }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Lease<T>(T value) : IValueLease<T> where T : notnull
    {
        public T Value => value;
        public void Dispose() { }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class TestBus : IEventBus
    {
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null) => new Subscription();
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class TestLogger : IPluginLogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
