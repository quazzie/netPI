using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Abstractions;
using NetPI.AutoCompact;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package A (context reads): context is read from a checkpoint or a
/// RECENT tail — never from an oldest-first window. A session longer than the
/// old read limit must include its newest messages, and a compaction
/// checkpoint beyond any history window must still be found.
/// </summary>
public sealed class ContextReadsTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-ctxreads-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort; tests are isolated per run */ }
    }

    private SqliteSessionStore Store() => new(Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".db"));

    private static AgentMessage Msg(int n)
        => new("m" + n, MessageRole.User, [new TextPart("message " + n)], DateTimeOffset.UtcNow);

    private static async ValueTask<SessionInfo> SeedAsync(ISessionStore store, int messages)
    {
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= messages; i++)
            await store.AppendAsync(new SessionEntry(
                "e" + i, info.Id, EntryKind.Message, Msg(i), null, DateTimeOffset.UtcNow, Sequence: 0));
        return info;
    }

    private static async Task AppendCompactionAsync(ISessionStore store, string sessionId,
        string summary, int retainedFrom)
    {
        var payload = new CompactionEntryPayload
        {
            Summary = summary,
            SummarizedThroughSequence = retainedFrom - 1,
            RetainedFromSequence = retainedFrom,
        };
        await store.AppendAsync(new SessionEntry(
            Guid.NewGuid().ToString("n"), sessionId, EntryKind.Compaction, null,
            JsonSerializer.SerializeToElement(payload,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            DateTimeOffset.UtcNow, Sequence: 0));
    }

    [Fact]
    public async Task LatestCompaction_FoundBeyondAnyHistoryWindow()
    {
        using var store = Store();
        var info = await SeedAsync(store, 400);

        await AppendCompactionAsync(store, info.Id, "old checkpoint", 100);
        // The LATEST checkpoint sits after entry 390 — far beyond any old
        // oldest-first window (the pre-A code read 200 entries and would have
        // found only the first checkpoint).
        await AppendCompactionAsync(store, info.Id, "new checkpoint", 300);

        var latest = await store.LatestCompactionAsync(info.Id);

        Assert.NotNull(latest);
        var pl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            latest!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("new checkpoint", pl!.Summary);
        Assert.Equal(300, pl.RetainedFromSequence);
    }

    [Fact]
    public async Task ReadAfter_ReturnsOnlyEntriesAfterTheSequenceInOrder()
    {
        using var store = Store();
        var info = await SeedAsync(store, 50);

        var page = await store.ReadAfterAsync(info.Id, afterSequence: 40, count: 10);

        Assert.Equal(10, page.Count);
        Assert.Equal(41, page[0].Sequence);
        Assert.Equal(50, page[^1].Sequence);
        for (int i = 1; i < page.Count; i++)
            Assert.True(page[i].Sequence > page[i - 1].Sequence);
    }

    [Fact]
    public async Task ReadRecent_ReturnsNewestEntriesInChronologicalOrder()
    {
        using var store = Store();
        var info = await SeedAsync(store, 50);

        var recent = await store.ReadRecentAsync(info.Id, 10);

        Assert.Equal(10, recent.Count);
        Assert.Equal(41, recent[0].Sequence);   // newest 10, oldest first
        Assert.Equal(50, recent[^1].Sequence);
    }

    [Fact]
    public async Task LongSession_RecentReadIncludesNewestMessages_AndReadAsyncWindowDoesNot()
    {
        using var store = Store();
        var info = await SeedAsync(store, 300);

        // The old fallback (oldest-first ReadAsync with offset 0) silently
        // dropped everything after the window on a long session.
        var window = await store.ReadAsync(info.Id, 0, 200);
        Assert.DoesNotContain(window, e => e.Sequence == 300);

        var recent = await store.ReadRecentAsync(info.Id, 200);
        Assert.Equal(200, recent.Count);
        Assert.Contains(recent, e => e.Sequence == 300);   // newest included
        Assert.Equal(101, recent[0].Sequence);              // chronological
    }

    // ---- AutoCompactService: checkpoint reconstruction over the new queries ----

    [Fact]
    public async Task BuildActiveContext_CheckpointBeyondOldWindow_ServesSummaryPlusNewTail()
    {
        using var store = Store();
        var info = await SeedAsync(store, 400);
        // checkpoint after the old 200-message window: the pre-A reconstruction
        // never saw it (it scanned the oldest 200 entries).
        await AppendCompactionAsync(store, info.Id, "SUMMARY-XYZ", 300);
        // …and 20 more messages arrived since the checkpoint.
        for (int i = 401; i <= 420; i++)
            await store.AppendAsync(new SessionEntry(
                "x" + i, info.Id, EntryKind.Message, Msg(i), null, DateTimeOffset.UtcNow, Sequence: 0));

        var svc = new AutoCompactService(
            new FakeContext(store),
            new AutoCompactConfig(true, 1, 1, 1_000_000, 10_000));

        var context = await svc.BuildActiveContextAsync(info.Id);

        // first element is the summary message; verify it carries the text
        Assert.True(context[0].Parts.OfType<TextPart>().Any(p => p.Text.Contains("SUMMARY-XYZ")),
            "summary message missing");
        // retained tail is exactly messages 300..420
        var tail = context.Skip(1).Select(m => m.Id).ToList();
        Assert.Equal(Enumerable.Range(300, 121).Select(n => "m" + n), tail);
    }

    [Fact]
    public async Task BuildActiveContext_NoCheckpoint_ReturnsBoundedRecentHistory_NotOldest()
    {
        using var store = Store();
        var info = await SeedAsync(store, 300);

        var svc = new AutoCompactService(
            new FakeContext(store),
            new AutoCompactConfig(true, 1, 1, 1_000_000, 10_000));

        var context = await svc.BuildActiveContextAsync(info.Id);

        Assert.Equal(300, context.Count);      // window larger than the session
        Assert.Equal("m1", context[0].Id);
        Assert.Equal("m300", context[^1].Id);
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeContext : IPluginContext
    {
        private sealed class Scoped(ServiceRegistry host) : IServiceRegistry
        {
            public IDisposable Register<T>(string id, T instance) where T : notnull
            { host.Register(id, instance, Owner); return new Noop(); }
            private sealed class Noop : IDisposable { public void Dispose() { } }
            public IValueLease<T> Acquire<T>(string id) where T : notnull => host.Acquire<T>(id);
            public IValueLease<object> Acquire(string id, Type expectedType) => host.Acquire(id, expectedType);
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull => host.AcquireSelfLease<T>();
            public T Resolve<T>(string id) where T : notnull => host.Resolve<T>(id);
            public IDisposable WatchServiceReplacement(string id, Action<string> onReplaced)
                => host.WatchServiceReplacement(id, onReplaced);
            public static readonly PluginInstance Owner = new("netPI.AutoCompact", 1) { State = PluginState.Active };
        }

        private sealed class NullLogger : IPluginLogger
        {
            public void Debug(string m) { }
            public void Information(string m) { }
            public void Warning(string m) { }
            public void Error(string m, Exception? e = null) { }
        }

        private readonly Scoped _services;

        public FakeContext(ISessionStore store)
        {
            var host = new ServiceRegistry();
            _services = new Scoped(host);
            host.Register("sessions", store, Scoped.Owner);
        }

        public PluginInfo Info { get; } = new("netPI.AutoCompact", "AutoCompact", "1.0.0");
        public IServiceRegistry Services => _services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => throw new NotSupportedException();
        public JsonElement OwnConfig => default;
        public IPluginLogger Log { get; } = new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }
}
