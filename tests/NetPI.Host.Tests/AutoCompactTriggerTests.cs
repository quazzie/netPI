using System.Text.Json;
using NetPI.Abstractions;
using NetPI.AutoCompact;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §31/§32/§33: the compaction TRIGGER flow — <c>CompactAsync</c> fires only
/// when estimated context &gt; contextWindow − reserveTokens, summarizes the older
/// messages (tools disabled, T=0.3), persists an <c>EntryKind.Compaction</c> entry,
/// and returns the reconstructed active context (summary + retained recent tail).
/// A session below the threshold is a no-op: no provider call, no entry.
/// </summary>
public sealed class AutoCompactTriggerTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-compact-trig-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort; tests are isolated per run */ }
    }

    private SqliteSessionStore Store() =>
        new(Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".db"));

    private static AgentMessage Tagged(int n)
        => new("m" + n, MessageRole.User,
            [new TextPart("MARK-" + n + new string('a', 395))], DateTimeOffset.UtcNow);

    /// <summary>101 tokens each (400 chars / 4 + 1) — the estimator is internal,
    /// so the numbers below are derived from that coarse chars/4 rule.</summary>
    private static AgentMessage Msg(int n)
        => new("m" + n, MessageRole.User, [new TextPart(new string('a', 400))], DateTimeOffset.UtcNow);

    private static async ValueTask<SessionInfo> SeedAsync(ISessionStore store, int messages)
    {
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= messages; i++)
            await store.AppendAsync(new SessionEntry(
                "e" + i, info.Id, EntryKind.Message, Msg(i), null, DateTimeOffset.UtcNow, Sequence: 0));
        return info;
    }

    /// <summary>Config: window 8192, reserve 4096 → trigger threshold 4096 tokens.
    /// keepRecent 10 tokens retains the single newest message; everything older is
    /// summarized.</summary>
    private static AutoCompactService Service(FakeContext ctx)
        => new(ctx, new AutoCompactConfig(true, 4096, 10, 8192, 10_000));

    [Fact]
    public async Task Triggers_WhenEstimateExceedsWindowMinusReserve_PersistsEntry_ReturnsSummaryPlusTail()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var ctx = new FakeContext(store, provider, null);
        var svc = Service(ctx);
        var info = await SeedAsync(store, 12);

        // 12 × 101 = 1212… below 4096 → the full estimate alone would NOT fire;
        // the provider-reported usage base (PLAN §32) pushes it over the line.
        var request = new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            KeepRecentTokens = 0, // fall back to the config keep budget
            LastPromptTokens = 5_000,
            LastUsageMessageCount = 10,
        };

        var result = await svc.CompactAsync(request);

        // ---- triggered --------------------------------------------------------
        Assert.True(result.Performed, "estimate (5000 + 2×101) should exceed 4096");
        Assert.NotNull(result.Payload);
        Assert.NotNull(result.ActiveContext);

        // ---- the summarization call -------------------------------------------
        var req = Assert.Single(provider.Requests);
        Assert.Equal("test-model", req.ModelId);
        Assert.Empty(req.Tools);                              // tools disabled (PLAN §33)
        Assert.Equal(0.3f, req.Temperature!.Value);              // T=0.3
        Assert.Equal(2, req.Messages.Count);                   // system instruction + conversation
        Assert.Equal(MessageRole.System, req.Messages[0].Role);
        Assert.Contains("user: ", req.Messages[1].Parts.OfType<TextPart>().First().Text);

        // ---- reconstructed context --------------------------------------------
        var active = result.ActiveContext!;
        Assert.Equal(2, active.Count);                          // summary + one retained message
        Assert.Equal(MessageRole.System, active[0].Role);
        Assert.Contains("SUMMARY", active[0].Parts.OfType<TextPart>().First().Text);
        Assert.Equal("m12", active[1].Id);                     // the newest message is the tail

        // ---- persisted entry ---------------------------------------------------
        var latest = await store.LatestCompactionAsync(info.Id);
        Assert.NotNull(latest);
        var pl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            latest!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(pl);
        Assert.Equal("SUMMARY", pl!.Summary);
        Assert.Equal(11, pl.SummarizedThroughSequence);
        Assert.Equal(12, pl.RetainedFromSequence);
        Assert.Equal(5_202, pl.EstimatedTokensBefore);

        // And a fresh-context rebuild serves the SAME reconstruction (PLAN §31).
        var rebuilt = await svc.BuildActiveContextAsync(info.Id);
        Assert.Equal(2, rebuilt.Count);
        Assert.Contains("SUMMARY", rebuilt[0].Parts.OfType<TextPart>().First().Text);
        Assert.Equal("m12", rebuilt[1].Id);
    }

    [Fact]
    public async Task BelowThreshold_IsNoOp_NoProviderCall_NoEntry()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await SeedAsync(store, 3);   // 3 × 101 = 303 ≤ 4096

        var result = await svc.CompactAsync(new CompactionRequest
        { SessionId = info.Id, ModelId = "test-model" });

        Assert.False(result.Performed);
        Assert.Null(result.Payload);
        Assert.Null(result.ActiveContext);
        Assert.Empty(provider.Requests);
        Assert.Null(await store.LatestCompactionAsync(info.Id));
    }

    [Fact]
    public async Task DisabledConfig_IsNoOp_EvenWhenThresholdWouldFire()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = new AutoCompactService(
            new FakeContext(store, provider, null),
            new AutoCompactConfig(false, 4096, 10, 8192, 10_000));
        var info = await SeedAsync(store, 12);

        var result = await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            LastPromptTokens = 5_000,
            LastUsageMessageCount = 10,
        });

        Assert.False(result.Performed);
        Assert.Empty(provider.Requests);
        Assert.Null(await store.LatestCompactionAsync(info.Id));
    }

    [Fact]
    public async Task SecondCompaction_SummarizesOnlyNewerMessages_CarriesForwardOldSummary()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await SeedAsync(store, 12);

        var request = new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            LastPromptTokens = 5_000,
            LastUsageMessageCount = 10,
        };
        Assert.True((await svc.CompactAsync(request)).Performed);

        // Two more messages arrive (m13 carries a marker so we can verify which
        // messages the second pass actually sent to the model); the usage base
        // now covers all 14.
        await store.AppendAsync(new SessionEntry(
            "e13", info.Id, EntryKind.Message, Tagged(13), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry(
            "e14", info.Id, EntryKind.Message, Msg(14), null, DateTimeOffset.UtcNow, Sequence: 0));

        provider.NextSummary = "SUMMARY-2";
        var second = await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            LastPromptTokens = 6_000,
            LastUsageMessageCount = 14,
        });
        Assert.True(second.Performed);

        var secondRequest = provider.Requests[1];
        var convo = secondRequest.Messages[1].Parts.OfType<TextPart>().First().Text;
        // Only the messages NEWER than the first checkpoint's retained tail went
        // to the model: m12 (inside no earlier summary) and m13 — not m11 or
        // anything older, which the first summary already folded.
        Assert.Equal(2, convo.Split('\n').Count(l => l.Length > 0));
        Assert.Contains("MARK-13", convo);
        Assert.DoesNotContain("MARK-11", convo);

        // The old summary text is carried forward into the new checkpoint.
        var secondPl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            (await store.LatestCompactionAsync(info.Id))!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(secondPl);
        Assert.Contains("SUMMARY", secondPl!.Summary);
        Assert.Contains("SUMMARY-2", secondPl.Summary);
        Assert.Equal(14, secondPl.SummarizedThroughSequence);
        Assert.Equal(15, secondPl.RetainedFromSequence);
        Assert.Equal(6_000, secondPl.EstimatedTokensBefore);
    }

    // ---- fakes -----------------------------------------------------------------

    /// <summary>Records every summarization request; replies with a fixed summary.</summary>
    private sealed class FakeProvider : IModelProvider
    {
        private readonly List<ModelRequest> _requests = [];
        public IReadOnlyList<ModelRequest> Requests => _requests;
        public string NextSummary { get; set; } = "SUMMARY";

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
        {
            _requests.Add(request);
            yield return new ModelCompleted(new AgentMessage(
                Guid.NewGuid().ToString("n"), MessageRole.Assistant,
                [new TextPart(NextSummary)], DateTimeOffset.UtcNow));
            await Task.CompletedTask;
        }
    }


    private sealed class FakeContext : IPluginContext
    {
        private sealed class Scoped(ServiceRegistry host) : IServiceRegistry
        {
            public IDisposable Register<T>(string id, T instance) where T : notnull
                => host.Register(id, instance, Owner);
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
        public PluginInfo Info { get; } = new("netPI.AutoCompact", "AutoCompact", "1.0.0");
        public IServiceRegistry Services => _services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => throw new NotSupportedException();
        public JsonElement OwnConfig => default;
        public IPluginLogger Log { get; } = new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();


        public FakeContext(
            ISessionStore store, IModelProvider provider, IModelCatalog? catalog)
        {
            _services = BuildServices(store, provider, catalog);
        }

        private static Scoped BuildServices(
            ISessionStore store, IModelProvider provider, IModelCatalog? catalog)
        {
            var host = new ServiceRegistry();
            host.Register("sessions", store, Scoped.Owner);
            host.Register("provider", provider, Scoped.Owner);
            if (catalog is not null) host.Register("catalog", catalog, Scoped.Owner);
            return new Scoped(host);
        }
    }
}
