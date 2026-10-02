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

    private static ChatMessage Calls(params string[] ids) => new()
    {
        Role = MessageRole.Assistant, Parts = [.. ids.Select(id => (MessagePart)new ToolCallPart { Id = id, Name = "bash", Arguments = "{}" })],
    };

    private static ChatMessage Result(string id, string content) => new()
    {
        Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = id, Name = "bash", Content = content }],
    };

    /// <summary>The assistant message and each tool result are stored as they happen, so a notice can land between a call and
    /// a result that is not in yet: the real result must survive, and the notice follows it.</summary>
    private static Task NoticeDuringTools()
    {
        var sent = ModelMessages.Normalize(
        [
            ChatMessage.UserText("build it"),
            Calls("c1", "c2"),
            Result("c1", "built"),
            ChatMessage.NoticeText("project changed", "project"),   // appended while c2 still ran
            Result("c2", "tested"),
            ChatMessage.UserText("thanks"),
        ]);
        Check.Equal("User,Assistant,Tool,User,User", string.Join(",", sent.Select(m => m.Role)));
        var results = sent[2].ToolResults.ToList();
        Check.Equal(2, results.Count);
        Check.Equal("built,tested", string.Join(",", results.Select(x => x.Content)), "both real results are kept");
        Check.False(results.Any(x => x.IsError), "nothing is closed as not executed");
        Check.Contains(sent[3].Text, "project changed");
        Check.Equal("thanks", sent[4].Text);

        // a notice before any result, at the end of the history (the tools have finished by the next model call, but the
        // normaliser must not depend on it)
        var tail = ModelMessages.Normalize([ChatMessage.UserText("go"), Calls("c1"), ChatMessage.NoticeText("n", "project")]);
        Check.Equal("User,Assistant,Tool,User", string.Join(",", tail.Select(m => m.Role)));
        Check.True(tail[2].ToolResults.Single().IsError, "a call that never got a result is still closed");
        Check.Contains(tail[2].ToolResults.Single().Content, "not executed");

        // several notices keep their order, all behind the results; a notice with no call open is placed where it is
        var many = ModelMessages.Normalize(
        [
            ChatMessage.NoticeText("first", "a"), ChatMessage.UserText("go"), Calls("c1"),
            ChatMessage.NoticeText("second", "b"), ChatMessage.NoticeText("third", "c"), Result("c1", "ok"), Calls("c2"), Result("c2", "ok2"),
        ]);
        Check.Equal("User,User,Assistant,Tool,User,User,Assistant,Tool", string.Join(",", many.Select(m => m.Role)));
        Check.Contains(many[0].Text, "first");
        Check.Equal("ok", many[3].ToolResults.Single().Content);
        Check.Contains(many[4].Text, "second");
        Check.Contains(many[5].Text, "third");
        Check.Equal("ok2", many[7].ToolResults.Single().Content);
        return Task.CompletedTask;
    }

    public static void Register(TestRunner r)
    {
        r.Add("model messages: a notice stored while tools ran waits behind their results; unanswered calls are still closed", NoticeDuringTools);
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
