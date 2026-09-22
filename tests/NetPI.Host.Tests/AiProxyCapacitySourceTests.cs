using System.Net;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

// astra-2 §5.3 acceptance for the Follow-AiProxy capacity source: it reads
// METADATA only (/v1/models, /aiswitcher/status — never an inference
// endpoint), reports a Fresh configured total when one exists, reports
// Unknown (never a guessed number) when none does, and never reads the
// live active-request arrays.

public class AiProxyCapacitySourceTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;
        public List<string> Requested = [];

        public StubHandler(Dictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri.ToString());
            var key = request.RequestUri.AbsolutePath;
            var resp = new HttpResponseMessage
            {
                StatusCode = _responses.TryGetValue(key, out var body) ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                Content = new StringContent(_responses.TryGetValue(key, out var b) ? b : "{}"),
            };
            return Task.FromResult(resp);
        }
    }

    private static (AiProxyCapacitySource, StubHandler) MakeSource(Dictionary<string, string> responses)
    {
        var handler = new StubHandler(responses);
        var http = new HttpClient(handler);
        return (new AiProxyCapacitySource(http, "http://127.0.0.1:8090", new NullLogger()), handler);
    }

    [Fact]
    public async Task ModelsConcurrencyField_IsReportedFresh()
    {
        var (src, handler) = MakeSource(new()
        {
            ["/v1/models"] = """{"data":[{"id":"qwen3.8-27b","concurrency":4}]}""",
        });
        var obs = await src.ObserveAsync("qwen3.8-27b");
        Assert.Equal(ProviderCapacityStatus.Fresh, obs.Status);
        Assert.Equal(4, obs.TotalConcurrency);
        Assert.True(obs.Usable);
        // Metadata only: no inference endpoint touched.
        Assert.All(handler.Requested, r => Assert.False(r.Contains("/chat/completions") || r.Contains("/responses"), r));
    }

    [Fact]
    public async Task StatusSlots_IsReportedFresh_WhenModelsHasNoTotal()
    {
        var (src, handler) = MakeSource(new()
        {
            ["/v1/models"] = """{"data":[{"id":"qwen3.8-27b"}]}""",
            ["/aiswitcher/status"] = """{"backends":[{"name":"ninfer","models":[{"id":"qwen3.8-27b","slots":2,"status":"loaded"}]}]}""",
        });
        var obs = await src.ObserveAsync("qwen3.8-27b");
        Assert.Equal(ProviderCapacityStatus.Fresh, obs.Status);
        Assert.Equal(2, obs.TotalConcurrency);
    }

    [Fact]
    public async Task NoConfiguredTotal_Anywhere_ReportsUnknown_NotAGuess()
    {
        var (src, _) = MakeSource(new()
        {
            ["/v1/models"] = """{"data":[{"id":"qwen3.8-27b"}]}""",
            // ninfer-style backend: model listed, slots null.
            ["/aiswitcher/status"] = """{"backends":[{"name":"ninfer","models":[{"id":"qwen3.8-27b","slots":null,"status":"loaded"}]}]}""",
        });
        var obs = await src.ObserveAsync("qwen3.8-27b");
        Assert.Equal(ProviderCapacityStatus.Unknown, obs.Status);
        Assert.Null(obs.TotalConcurrency);
        Assert.False(obs.Usable);
        Assert.NotNull(obs.Detail); // a visible, explainable reason — not a guessed number
    }

    [Fact]
    public async Task LiveLanesAreNeverRead_OnlyMetadataRoutes()
    {
        var (src, handler) = MakeSource(new()
        {
            ["/v1/models"] = """{"data":[]}""",
            ["/aiswitcher/status"] =
"""{"backends":[{"name":"ninfer","metrics":{"lanes":[{"request_id":1,"phase":"decoding"}],"recent":[{"request_id":2}]},"models":[{"id":"qwen3.8-27b","slots":null}]}]}""",
        });
        await src.ObserveAsync("qwen3.8-27b");
        Assert.All(handler.Requested, r => Assert.False(r.Contains("/chat/completions") || r.Contains("/responses"), r));
        Assert.Contains(handler.Requested, r => r.EndsWith("/aiswitcher/status"));
    }

    [Fact]
    public async Task UnreachableProvider_ReportsUnknown_Bounded()
    {
        var (src, _) = MakeSource(new()); // every route 404
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var obs = await src.ObserveAsync("any-model");
        sw.Stop();
        Assert.Equal(ProviderCapacityStatus.Unknown, obs.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); // bounded, no long hang
    }

    [Fact]
    public async Task UnknownObservation_HoldsNewAdmission_InTheScheduler()
    {
        // End-to-end: an Unknown observation from the source keeps the pool
        // from admitting (astra-2 §5.3: hold-new; never a guessed number).
        var scheduler = new NetPI.Lanes.LaneScheduler("gen", new NullLogger());
        scheduler.RegisterPool("local-big", "dep-local", "local-model", LaneCapacityMode.Provider, null);
        var (src, _) = MakeSource(new());
        var obs = await src.ObserveAsync("local-model");
        scheduler.UpdateProviderObservation(obs);

        var result = await scheduler.AcquireAsync(new LaneQueueEntry(
            "A", "local-big", "dep-local", 1, null, null, null, "task", DateTimeOffset.UtcNow));
        Assert.Null(result.Token);
        Assert.NotNull(result.BlockedReason);
        Assert.Contains("unknown", result.BlockedReason!, StringComparison.OrdinalIgnoreCase);
    }
}
