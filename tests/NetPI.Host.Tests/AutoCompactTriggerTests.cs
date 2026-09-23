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
    public async Task SecondCompaction_SummarizesOnlyNewerMessages_FoldsPriorSummaryIntoBoundedReSummary()
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
        // docs/plans/compaction-tool-history.md §4.3: the prior summary is FOLDED
        // into the material being summarized (not concatenated onto the output).
        // The model sees the prior summary PLUS only the messages newer than the
        // first checkpoint's retained tail (m12, m13) — not m11 or anything older.
        Assert.Contains("Carry this summary of earlier work forward", convo);
        Assert.Contains("SUMMARY", convo);          // the old summary, folded in as input
        Assert.Contains("MARK-13", convo);          // the new material
        Assert.DoesNotContain("MARK-11", convo);    // older material is not re-sent

        // The checkpoint's summary is the model's single bounded replacement
        // (the fake's output for this call) — NOT old + new concatenated.
        var secondPl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            (await store.LatestCompactionAsync(info.Id))!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(secondPl);
        Assert.Equal("SUMMARY-2", secondPl!.Summary);
        Assert.Equal(14, secondPl.SummarizedThroughSequence);
        Assert.Equal(15, secondPl.RetainedFromSequence);
        Assert.Equal(6_000, secondPl.EstimatedTokensBefore);
    }

    [Fact]
    public async Task RetainedTail_BoundaryIsBatchAware_KeepsTheCompleteToolExchange()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= 8; i++)
            await store.AppendAsync(new SessionEntry("f" + i, info.Id, EntryKind.Message,
                new AgentMessage("filler" + i, MessageRole.User,
                    [new TextPart(new string('a', 400))], DateTimeOffset.UtcNow),
                null, DateTimeOffset.UtcNow, Sequence: 0));
        // The incident shape (docs/plans/compaction-tool-history.md §1): an assistant
        // tool call (seq 9) immediately followed by its result (seq 10).
        await store.AppendAsync(new SessionEntry("call", info.Id, EntryKind.Message,
            new AgentMessage("assistant-call", MessageRole.Assistant,
                [new ToolCallPart("call_incident", "bash",
                    JsonSerializer.SerializeToElement(new { }))],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("result", info.Id, EntryKind.Message,
            new AgentMessage("tool-result", MessageRole.Tool,
                [new ToolResultPart("call_incident", "bash",
                    [new TextPart(new string('b', 400))], false)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));

        var result = await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            LastPromptTokens = 5_000,
            LastUsageMessageCount = 10,
        });

        Assert.True(result.Performed);
        var active = result.ActiveContext!;
        // The COMPLETE exchange is retained: the assistant call AND its result both
        // survive (the per-message boundary used to keep only the result, seq 10,
        // and summarize the call, seq 9 — the provider then rejected the transcript).
        var ids = active.Select(m => m.Id).ToList();
        Assert.Equal(3, active.Count);
        Assert.Contains("assistant-call", ids);
        Assert.Contains("tool-result", ids);
        Assert.True(ids.IndexOf("assistant-call") < ids.IndexOf("tool-result"),
            "the call must precede its result in the retained tail");

        var pl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            (await store.LatestCompactionAsync(info.Id))!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(9, pl.RetainedFromSequence);   // the CALL, not the result (10)
        Assert.Equal(8, pl.SummarizedThroughSequence);
    }

    [Fact]
    public async Task RetainedTail_KeepsAMultiCallBatchWhole()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= 6; i++)
            await store.AppendAsync(new SessionEntry("f" + i, info.Id, EntryKind.Message,
                new AgentMessage("filler" + i, MessageRole.User,
                    [new TextPart(new string('a', 400))], DateTimeOffset.UtcNow),
                null, DateTimeOffset.UtcNow, Sequence: 0));
        // seq 7: assistant issuing TWO calls; seq 8 + 9: the two results (separate messages).
        var emptyArgs = JsonSerializer.SerializeToElement(new { });
        await store.AppendAsync(new SessionEntry("call", info.Id, EntryKind.Message,
            new AgentMessage("assistant-call", MessageRole.Assistant,
                [new ToolCallPart("call_x", "bash", emptyArgs),
                 new ToolCallPart("call_y", "grep", emptyArgs)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("r8", info.Id, EntryKind.Message,
            new AgentMessage("tool-result-x", MessageRole.Tool,
                [new ToolResultPart("call_x", "bash", [new TextPart(new string('b', 400))], false)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("r9", info.Id, EntryKind.Message,
            new AgentMessage("tool-result-y", MessageRole.Tool,
                [new ToolResultPart("call_y", "grep", [new TextPart(new string('c', 400))], false)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));

        var result = await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id,
            ModelId = "test-model",
            LastPromptTokens = 5_000,
            LastUsageMessageCount = 10,
        });

        Assert.True(result.Performed);
        var active = result.ActiveContext!;
        var ids = active.Select(m => m.Id).ToList();
        // The whole batch (call + both results) is retained together.
        Assert.Contains("assistant-call", ids);
        Assert.Contains("tool-result-x", ids);
        Assert.Contains("tool-result-y", ids);
        Assert.True(ids.IndexOf("assistant-call") < ids.IndexOf("tool-result-x")
                 && ids.IndexOf("tool-result-x") < ids.IndexOf("tool-result-y"));

        var pl = JsonSerializer.Deserialize<CompactionEntryPayload>(
            (await store.LatestCompactionAsync(info.Id))!.Payload!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(7, pl.RetainedFromSequence);
    }

    [Fact]
    public async Task ActiveRangeLargerThanReadCap_StillRetainsTheNewestEntries()
    {
        using var store = Store();
        var provider = new FakeProvider();
        // MaxContextMessages=3: a real store session whose active range exceeds the
        // read page. On the old single bounded ReadAfterAsync (ascending LIMIT) the
        // NEWEST entries were silently dropped; the paging read must keep them.
        var svc = new AutoCompactService(new FakeContext(store, provider, null),
            new AutoCompactConfig(true, 4096, 10, 8192, 3));
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= 6; i++)
            await store.AppendAsync(new SessionEntry("s" + i, info.Id, EntryKind.Message,
                Msg(i), null, DateTimeOffset.UtcNow, Sequence: 0));
        Assert.True((await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id, ModelId = "test-model",
            LastPromptTokens = 5_000, LastUsageMessageCount = 10,
        })).Performed);

        // Four more messages push the active range past the 3-entry read cap.
        for (int i = 7; i <= 10; i++)
            await store.AppendAsync(new SessionEntry("s" + i, info.Id, EntryKind.Message,
                Tagged(i), null, DateTimeOffset.UtcNow, Sequence: 0));

        var r2 = await svc.CompactAsync(new CompactionRequest
        {
            SessionId = info.Id, ModelId = "test-model",
            LastPromptTokens = 5_000, LastUsageMessageCount = 10,
        });
        Assert.True(r2.Performed);

        // The newest message survives into the retained tail — the bounded-ascend bug
        // would have truncated the range at m7 and lost m10.
        var ids = r2.ActiveContext!.Select(m => m.Id).ToList();
        Assert.Contains("m10", ids);
        Assert.Equal("m10", ids[^1]);
    }

    [Fact]
    public async Task LegacyCheckpointThatSplitAnExchange_RecoverTheBoundaryCallFromDurableHistory()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await store.CreateAsync(null);
        // Filler (seq 1-6), then the incident exchange: assistant call (seq 7) and
        // its result (seq 8). A LEGACY (pre-§2) boundary set RetainedFromSequence
        // to the RESULT, leaving the CALL in the summarized region.
        for (int i = 1; i <= 6; i++)
            await store.AppendAsync(new SessionEntry("f" + i, info.Id, EntryKind.Message,
                new AgentMessage("filler" + i, MessageRole.User,
                    [new TextPart(new string('a', 400))], DateTimeOffset.UtcNow),
                null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("call", info.Id, EntryKind.Message,
            new AgentMessage("assistant-call", MessageRole.Assistant,
                [new TextPart("Let me run the build."),
                 new ToolCallPart("call_leg", "bash",
                    JsonSerializer.SerializeToElement(new { }))],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("result", info.Id, EntryKind.Message,
            new AgentMessage("tool-result", MessageRole.Tool,
                [new ToolResultPart("call_leg", "bash",
                    [new TextPart(new string('b', 400))], false)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        // The legacy checkpoint: summarize through the CALL (7), retain from the
        // RESULT (8) — the exact split that produced invalid_tool_history.
        var payload = new CompactionEntryPayload
        {
            Summary = "SUMMARY",
            SummarizedThroughSequence = 7,
            RetainedFromSequence = 8,
            EstimatedTokensBefore = 5_000,
            EstimatedTokensAfter = 100,
            ModelId = "test-model",
        };
        var payloadElement = JsonSerializer.SerializeToElement(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await store.AppendAsync(new SessionEntry(
            MessageIdentity.DeterministicId("checkpoint", payloadElement.GetRawText()),
            info.Id, EntryKind.Compaction, null, payloadElement,
            DateTimeOffset.UtcNow, Sequence: 0));

        // Resuming reconstructs the active context. The orphan result leading the
        // tail must have its boundary call recovered from durable history — the
        // exchange is whole again: no recovery note, no validation failure, no
        // synthetic interrupt.
        var active = await svc.BuildActiveContextAsync(info.Id);

        Assert.Equal(3, active.Count);                          // summary + call + result
        Assert.Equal(MessageRole.System, active[0].Role);
        Assert.Contains("SUMMARY", active[0].Parts.OfType<TextPart>().First().Text);

        var call = active[1];
        Assert.Equal(MessageRole.Assistant, call.Role);
        var callPart = Assert.Single(call.Parts.OfType<ToolCallPart>());
        Assert.Equal("call_leg", callPart.Id);

        // The result survives as a real tool message — not degraded to a note.
        var result = Assert.Single(active, m => m.Id == "tool-result");
        Assert.Equal(MessageRole.Tool, result.Role);
        Assert.Single(result.Parts.OfType<ToolResultPart>());
        Assert.DoesNotContain("[historical-recovery]",
            string.Join(" ", active.SelectMany(m => m.Parts.OfType<TextPart>().Select(p => p.Text))));
    }

    [Fact]
    public async Task CleanCheckpointBoundary_IsNotTouchedByBoundaryRecovery()
    {
        using var store = Store();
        var provider = new FakeProvider();
        var svc = Service(new FakeContext(store, provider, null));
        var info = await store.CreateAsync(null);
        for (int i = 1; i <= 6; i++)
            await store.AppendAsync(new SessionEntry("f" + i, info.Id, EntryKind.Message,
                new AgentMessage("filler" + i, MessageRole.User,
                    [new TextPart(new string('a', 400))], DateTimeOffset.UtcNow),
                null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("call", info.Id, EntryKind.Message,
            new AgentMessage("assistant-call", MessageRole.Assistant,
                [new ToolCallPart("call_ok", "bash",
                    JsonSerializer.SerializeToElement(new { }))],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        await store.AppendAsync(new SessionEntry("result", info.Id, EntryKind.Message,
            new AgentMessage("tool-result", MessageRole.Tool,
                [new ToolResultPart("call_ok", "bash",
                    [new TextPart(new string('b', 400))], false)],
                DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow, Sequence: 0));
        // A clean checkpoint: RetainedFromSequence is the CALL (seq 7), so the tail
        // starts with the assistant turn — no leading orphan, recovery is a no-op.
        var payload = new CompactionEntryPayload
        {
            Summary = "SUMMARY",
            SummarizedThroughSequence = 6,
            RetainedFromSequence = 7,
            EstimatedTokensBefore = 5_000,
            EstimatedTokensAfter = 100,
            ModelId = "test-model",
        };
        var payloadElement = JsonSerializer.SerializeToElement(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await store.AppendAsync(new SessionEntry(
            MessageIdentity.DeterministicId("checkpoint", payloadElement.GetRawText()),
            info.Id, EntryKind.Compaction, null, payloadElement,
            DateTimeOffset.UtcNow, Sequence: 0));

        var active = await svc.BuildActiveContextAsync(info.Id);
        var ids = active.Select(m => m.Id).ToList();
        Assert.Equal(3, active.Count);   // summary + call + result — no spurious message
        Assert.Contains("assistant-call", ids);
        Assert.Contains("tool-result", ids);
        Assert.True(ids.IndexOf("assistant-call") < ids.IndexOf("tool-result"));
    }

    [Fact]
    public async Task SummaryFailure_LeavesNoCheckpoint_AndOriginalTranscriptIntact()
    {
        using var store = Store();
        var provider = new FakeProvider { Fail = true };
        var svc = Service(new FakeContext(store, provider, null));
        var info = await SeedAsync(store, 12);

        // A request that WOULD trigger compaction, but the summarization model fails.
        var request = new CompactionRequest
        {
            SessionId = info.Id, ModelId = "test-model",
            LastPromptTokens = 5_000, LastUsageMessageCount = 10,
        };

        // docs/plans/compaction-tool-history.md §4.4: the summarization failure
        // throws BEFORE the checkpoint is appended, so no broken checkpoint is
        // ever persisted — the original transcript stays intact for the next try.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await svc.CompactAsync(request);
        });

        Assert.Null(await store.LatestCompactionAsync(info.Id));
        var recent = await store.ReadRecentAsync(info.Id, 50);
        Assert.Equal(12, recent.Count(e => e.Kind == EntryKind.Message));
    }

    // ---- fakes -----------------------------------------------------------------

    /// <summary>Records every summarization request; replies with a fixed summary.</summary>
    private sealed class FakeProvider : IModelProvider
    {
        private readonly List<ModelRequest> _requests = [];
        public IReadOnlyList<ModelRequest> Requests => _requests;
        public string NextSummary { get; set; } = "SUMMARY";
        /// <summary>When true, the summarization run fails (yields a ModelFailed event).</summary>
        public bool Fail { get; set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
        {
            _requests.Add(request);
            if (Fail)
            {
                yield return new ModelFailed(request.ModelId, "synthetic summarization failure");
                yield break;
            }
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
