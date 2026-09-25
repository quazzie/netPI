using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;
using NetPI.Host.Models;
using NetPI.Host.Registries;
using NetPI.Host.Settings;

namespace NetPI.Host.Tests;

public static class ModelCatalogTests
{
    private sealed class FakeProvider(string id, params ModelInfo[] models) : IModelProvider
    {
        public bool Fail { get; set; }
        public List<string> SeenRoles { get; } = [];
        public string Id => id;
        public string DisplayName => id;
        public bool IsLocal => true;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct) =>
            Fail ? throw new HttpRequestException("offline") : Task.FromResult<IReadOnlyList<ModelInfo>>(models);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            SeenRoles.AddRange(request.Messages.Select(m => m.Role.ToString()));
            await Task.Yield();
            yield return new TextDelta("hi");
            yield return new StreamCompleted(new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = $"{id}:{request.Model.Id}" }] });
        }
    }

    private sealed class RecordingMiddleware(string name, int order, List<string> log) : IModelMiddleware
    {
        public int Order => order;

        public async IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next, [EnumeratorCancellation] CancellationToken ct)
        {
            log.Add(name + ">");
            await foreach (var e in next(request, ct)) yield return e;
            log.Add("<" + name);
        }
    }

    private static ModelInfo Model(string provider, string id, string? status = null) =>
        new() { Provider = provider, Id = id, Status = status, IsLocal = true };

    public static void Register(TestRunner r)
    {
        r.Add("models: aggregate, find, default model, models.changed on provider registration", async () =>
        {
            var dir = T.TempDir("models");
            await using var bus = new EventBus(NullLogger.Instance);
            using var settings = new SettingsStore(Path.Combine(dir, "settings.json"), NullLogger.Instance);
            var services = new ServiceRegistry();
            using var catalog = new ModelCatalog(services, settings, bus, NullLogger.Instance);
            services.Changed += catalog.OnServiceChanged;
            var changed = 0;
            using var sub = bus.Subscribe(EventTypes.ModelsChanged, e => { if (e.Source == ModelCatalog.Source) Interlocked.Increment(ref changed); });

            Check.Equal(0, (await catalog.ListAsync()).Count);
            var p1 = new FakeProvider("fake", Model("fake", "a", "unloaded"), Model("fake", "b", "loaded"));
            using var reg1 = services.Register<IModelProvider>(p1);
            await Wait.UntilAsync(() => Volatile.Read(ref changed) > 0, "models.changed after provider registration");
            Check.Equal("fake/a,fake/b", string.Join(",", catalog.Cached.Select(m => m.Ref)));
            var p2 = new FakeProvider("other", Model("other", "org/c"));
            var reg2 = services.Register<IModelProvider>(p2);
            var all = await catalog.ListAsync();
            Check.Equal(3, all.Count);

            Check.Equal("b", (await catalog.FindAsync("fake/b"))!.Id);
            Check.Equal("fake", (await catalog.FindAsync("a"))!.Provider, "bare id");
            Check.Equal("org/c", (await catalog.FindAsync("other/org/c"))!.Id, "split at the first slash");
            Check.Equal("unknown", (await catalog.FindAsync("fake/unlisted"))!.Status, "unlisted id of a known provider");
            Check.True(await catalog.FindAsync("nope/x") is null);

            settings.Set("defaultModel", "other/org/c");
            Check.Equal("other/org/c", catalog.DefaultModelRef, "settings.defaultModel");
            settings.Set("defaultModel", null);
            Check.Equal("fake/b", catalog.DefaultModelRef, "first loaded local model");

            // A failing provider keeps its last known models.
            p1.Fail = true;
            Check.Equal(3, (await catalog.ListAsync(refresh: true)).Count);
            reg2.Dispose();
            await Wait.UntilAsync(async () => (await catalog.ListAsync()).Count == 2, "provider removal invalidates");
        });

        r.Add("models: stream normalizes messages and runs middleware lowest Order outermost", async () =>
        {
            var dir = T.TempDir("models");
            await using var bus = new EventBus(NullLogger.Instance);
            using var settings = new SettingsStore(Path.Combine(dir, "settings.json"), NullLogger.Instance);
            var services = new ServiceRegistry();
            using var catalog = new ModelCatalog(services, settings, bus, NullLogger.Instance);
            var provider = new FakeProvider("fake", Model("fake", "a"));
            using var reg = services.Register<IModelProvider>(provider);
            var log = new List<string>();
            using var m1 = services.Register<IModelMiddleware>(new RecordingMiddleware("inner", 10, log));
            using var m2 = services.Register<IModelMiddleware>(new RecordingMiddleware("outer", 1, log));

            var request = new ModelRequest
            {
                Model = Model("fake", "a"),
                Messages =
                [
                    ChatMessage.UserText("hello"),
                    ChatMessage.NoticeText("project changed", "project"),
                    new ChatMessage { Role = MessageRole.Assistant, Parts = [new ToolCallPart { Id = "c1", Name = "read" }] },
                ],
            };
            var message = await catalog.CompleteAsync(request, CancellationToken.None);
            Check.Equal("fake:a", message.Text);
            Check.Equal("outer>,inner>,<inner,<outer", string.Join(",", log));
            // Notice → User, and the unanswered tool call gets a synthetic Tool result.
            Check.Equal("User,User,Assistant,Tool", string.Join(",", provider.SeenRoles));

            var missing = new ModelRequest { Model = Model("gone", "x"), Messages = [ChatMessage.UserText("x")] };
            await Check.ThrowsAsync<ModelException>(() => catalog.CompleteAsync(missing, CancellationToken.None));
        });
    }
}
