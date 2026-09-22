using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §11.2 (package F): the shared cloud budget + atomic reservations and
/// the execution gate. The §16 rows this covers:
///   • "Parallel cloud requests near budget limit → shared reservations prevent
///     double-spending the same remaining allowance" (exactly one of N equal
///     requests is admitted when the remainder covers one estimate).
///   • "Cloud disabled or team disallows cloud → No paid provider call" (a
///     rejected authorization never reaches the wire; the run fails with the
///     actionable reason, never a hidden fallback).
///   • release refunds the drawn estimate; settle reconciles reported usage
///     against the reservation (excess drawn / shortfall re-funded); a host
///     reload cannot lose the convert rate (it is persisted on the row).
/// </summary>
public sealed class CloudBudgetGateTests : IDisposable
{
    private readonly string _dir;

    public CloudBudgetGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpi-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteCloudBudgetStore NewStore() => new(Path.Combine(_dir, "b-" + Guid.NewGuid().ToString("N") + ".db"));

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class FakePolicy : IDeploymentPolicySource
    {
        private readonly Dictionary<string, DeploymentPolicy> _map;
        public FakePolicy(params DeploymentPolicy[] policies) =>
            _map = policies.ToDictionary(p => p.ModelId, StringComparer.Ordinal);
        public DeploymentPolicy? PolicyFor(string modelId) => _map.TryGetValue(modelId, out var p) ? p : null;
    }

    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    // ---- the §16 double-spend row: exactly one of N equal parallel reserves ---
    //      wins when the remaining allowance covers exactly ONE estimate.
    [Fact]
    public async Task ParallelReservations_OnlyOneAdmitted_WhenOnlyOneFits()
    {
        var store = NewStore();
        await store.UpsertAllowanceAsync(new CloudBudgetAllowance("team-a", "USD", 100, CloudBudgetUnit.Currency));

        // 60 × 2 = 120 > 100, but 60 × 1 ≤ 100 → at most one fits.
        const double estimate = 60;
        var tasks = Enumerable.Range(0, 5)
            .Select(i => store.ReserveAsync("team-a", estimate, 1.0, $"run-{i}", CancellationToken.None).AsTask())
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var admitted = results.Count(r => r.Admitted);
        Assert.Equal(1, admitted);                       // exactly one
        var deniedResults = results.Where(r => !r.Admitted).ToList();
        Assert.Equal(4, deniedResults.Count);
        Assert.All(deniedResults, d => Assert.Contains("exhausted", d.Reason!, StringComparison.OrdinalIgnoreCase));

        // The remainder after the single admit is 100 − 60 = 40 (the losing
        // requests drew nothing — no double-spend).
        var usage = (await store.UsageAsync(CancellationToken.None)).Single(u => u.TeamId == "team-a");
        Assert.Equal(60, usage.Spent, 3);
        Assert.Equal(40, usage.Remaining, 3);
    }

    // ---- release refunds the drawn estimate back to the remainder ------------
    [Fact]
    public async Task Release_RefundsTheDrawnEstimate()
    {
        var store = NewStore();
        await store.UpsertAllowanceAsync(new CloudBudgetAllowance("team-a", "USD", 100, CloudBudgetUnit.Currency));

        var r1 = await store.ReserveAsync("team-a", 60, 1.0, "run-1");
        Assert.True(r1.Admitted);
        var afterReserve = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(40, afterReserve.Remaining, 3);

        await store.ReleaseAsync(r1.ReservationId!);
        var afterRelease = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(100, afterRelease.Remaining, 3); // refunded to full

        // Idempotent: a second release is a no-op (the remainder does not grow past the limit).
        await store.ReleaseAsync(r1.ReservationId!);
        var afterSecond = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(100, afterSecond.Remaining, 3);
    }

    // ---- settle reconciles: excess is drawn, shortfall re-funded -------------
    [Fact]
    public async Task Settle_ReconcilesActualAgainstTheEstimate()
    {
        var store = NewStore();
        await store.UpsertAllowanceAsync(new CloudBudgetAllowance("team-a", "USD", 100, CloudBudgetUnit.Currency));

        var r = await store.ReserveAsync("team-a", 60, 1.0, "run-1");
        Assert.True(r.Admitted);

        // Used LESS than reserved → shortfall is re-funded.
        Assert.True(await store.SettleAsync(r.ReservationId!, 25));
        var u1 = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(25, u1.Spent, 3);
        Assert.Equal(75, u1.Remaining, 3);

        // Idempotent: settling the same reservation again is a no-op.
        Assert.False(await store.SettleAsync(r.ReservationId!, 25)); // second settle: already settled → false (no double-draw)
        var u2 = (await store.UsageAsync(CancellationToken.None)).Single();
        Assert.Equal(25, u2.Spent, 3);
    }

    // ---- the convert rate is persisted on the row (survives a reload) --------
    [Fact]
    public async Task ReserveInfo_PersistsTheConvertRate()
    {
        var path = Path.Combine(_dir, "info.db");
        var store = new SqliteCloudBudgetStore(path);
        await store.UpsertAllowanceAsync(new CloudBudgetAllowance("team-a", "USD", 1000, CloudBudgetUnit.Currency));
        var r = await store.ReserveAsync("team-a", 50, 0.025, "run-1"); // rate 0.025 USD/…

        var info = await store.ReserveInfoAsync(r.ReservationId!);
        Assert.NotNull(info);
        Assert.Equal("team-a", info!.TeamId);
        Assert.Equal(0.025, info.ConvertRate, 6);
        // ToDenominated converts reported tokens into the team's denomination.
        Assert.Equal(2.5, info.ToDenominated(100), 6);

        // A fresh store generation on the SAME file still sees the row.
        var store2 = new SqliteCloudBudgetStore(path);
        var info2 = await store2.ReserveInfoAsync(r.ReservationId!);
        Assert.Equal(0.025, info2!.ConvertRate, 6);
    }

    // ---- the gate: a covered direct-cloud model is authorized + reserved -----
    [Fact]
    public async Task Gate_CoversADirectCloudModel_AndDrawsTheCeiling()
    {
        var store = NewStore();
        var policy = new FakePolicy(
            new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud"));
        var cfg = Config("""
            {"teams":[{"teamId":"team-a","currency":"USD","limit":100,
                        "pricePer1kTokens":0.001,"models":["m-cloud"]}]}
            """);
        var gate = new ConfigCloudExecutionGate(store, cfg, new NullLogger(), () => policy);

        var res = await gate.AuthorizeAsync("m-cloud", "dep-cloud", 1000, "run-1");
        Assert.True(res.Admitted, $"denied: {res.Reason}");
        Assert.NotNull(res.ReservationId);
        // estimate = 1000 tokens × (0.001 / 1000) = 0.001 USD → spent tracks it.
        var usage = (await store.UsageAsync(CancellationToken.None)).Single(u => u.TeamId == "team-a");
        Assert.Equal(0.001, usage.Spent, 6);
    }

    // ---- the gate: a model NO team covers is denied (never paid) ------------
    [Fact]
    public async Task Gate_DeniesAModelNoTeamCovers()
    {
        var store = NewStore();
        var policy = new FakePolicy(
            new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud"));
        var cfg = Config("""{"teams":[{"teamId":"team-a","limit":100,"models":["other-model"]}]}""");
        var gate = new ConfigCloudExecutionGate(store, cfg, new NullLogger(), () => policy);

        var res = await gate.AuthorizeAsync("m-cloud", "dep-cloud", 1000, "run-1");
        Assert.False(res.Admitted);
        Assert.NotNull(res.Reason);
        Assert.Contains("no cloud budget policy", res.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the gate: a currency team with no price is denied (no invented $) ---
    [Fact]
    public async Task Gate_DeniesACurrencyTeamWithNoPrice()
    {
        var store = NewStore();
        var policy = new FakePolicy(
            new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud"));
        var cfg = Config("""{"teams":[{"teamId":"team-a","currency":"USD","limit":100,"models":["m-cloud"]}]}""");
        var gate = new ConfigCloudExecutionGate(store, cfg, new NullLogger(), () => policy);

        var res = await gate.AuthorizeAsync("m-cloud", "dep-cloud", 1000, "run-1");
        Assert.False(res.Admitted);
        Assert.NotNull(res.Reason);
        Assert.Contains("no pricePer1kTokens", res.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the §16 denied-inference row at the PROVIDER: no paid call ----------
    //      A rejected authorization yields a ModelFailed and NEVER opens the
    //      wire (no HTTP request is sent); the run is not silently redirected.
    [Fact]
    public async Task Provider_DeniedGate_NoPaidProviderCall_ModelFailed()
    {
        // A gate that denies everything (no team covers the model).
        var handler = new CountingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var provider = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat", cloudGate: new DeniedGate());

        var events = new List<ModelEvent>();
        await foreach (var ev in provider.RunAsync(
            new ModelRequest { ModelId = "m-cloud", DeploymentId = "dep-cloud", MaxTokens = 1000, RunId = "run-1" },
            CancellationToken.None))
            events.Add(ev);

        Assert.Single(events);
        Assert.IsType<ModelFailed>(events[0]);
        var failed = (ModelFailed)events[0];
        Assert.Contains("denied", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestsSent); // never reached the wire
    }

    // ---- the §10/§11 orchestrate-mode persistence row -----------------------
    //      SetModeAsync("orchestrate") persists; the store round-trips it on
    //      GetAsync, and clearing it (null) restores the chat default.
    [Fact]
    public async Task SessionMode_Orchestrate_PersistsAndRoundTrips()
    {
        using var store = new SqliteSessionStore(Path.Combine(_dir, "sessions.db"));
        var s = await store.CreateAsync(null);
        var id = s.Id;

        Assert.Null((await store.GetAsync(id))?.Mode); // default = chat (null)

        await store.SetModeAsync(id, "orchestrate");
        Assert.Equal("orchestrate", (await store.GetAsync(id))?.Mode);

        // Switching back to chat is an explicit null (not a magic "chat" value).
        await store.SetModeAsync(id, null);
        Assert.Null((await store.GetAsync(id))?.Mode);
    }


    // ---- regression: legacy mode (NO policy source) must not deny ------------
    //      The "deployments" service is registered ONLY by the lanes plugin in
    //      enabled mode. With lanes disabled the policy source is absent, so NO
    //      model is a known DirectCloud deployment — the gate's step-(1)
    //      posture ("no policy known -> legacy direct -> not a paid request")
    //      must return the None sentinel (proceed WITHOUT accounting), not a
    //      denial. Before the fix, the residency check was skipped and every
    //      model fell through to the team lookup, where a no-cloudBudgets host
    //      denied EVERY model with "no cloud budget policy".
    [Fact]
    public async Task Gate_NoPolicySource_LegacyDirect_ProceedsWithNoAccounting()
    {
        var store = NewStore();
        var cfg = Config("{}"); // no teams at all (a lanes-disabled host)
        var gate = new ConfigCloudExecutionGate(store, cfg, new NullLogger(), () => null);

        var res = await gate.AuthorizeAsync("qwen3.8-27b", null, 1000, "run-legacy");
        // The None sentinel: not admitted-as-reserved, but NO reason —
        // the request executes without a reservation (not a failure).
        Assert.False(res.Admitted);
        Assert.Null(res.Reason);
        Assert.Equal(CloudReservationResult.None, res);

        // ...and the provider must treat it as "execute without accounting":
        // the wire IS reached, the run succeeds, and nothing is concluded
        // (no reservation id exists to settle).
        var handler = new CountingHandler();
        handler.SetBody("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var provider = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat", cloudGate: gate);
        var events = new List<ModelEvent>();
        await foreach (var ev in provider.RunAsync(
            new ModelRequest { ModelId = "qwen3.8-27b", MaxTokens = 100, RunId = "run-legacy" },
            CancellationToken.None))
            events.Add(ev);
        Assert.True(handler.RequestsSent == 1, $"expected the wire to be reached, sent {handler.RequestsSent}");
        Assert.Contains(events, e => e is ModelCompleted);
        Assert.DoesNotContain(events, e => e is ModelFailed);
        Assert.Empty(await store.UsageAsync(CancellationToken.None)); // no accounting at all
    }

    // ---- regression: WITH a policy source, the direct-cloud deny stays ------
    //      (the astra-2 parity behavior that must NOT be regressed by the
    //      legacy-mode fix above: a DirectCloud model no team covers is still
    //      denied before any paid call; a pooled model stays None.)
    [Fact]
    public async Task Gate_PolicyPresent_DirectCloudNoTeamCovers_StillDenied()
    {
        var store = NewStore();
        var policy = new FakePolicy(
            new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud"));
        var cfg = Config("""{"teams":[{"teamId":"team-a","limit":100,"models":["other-model"]}]}""");
        var gate = new ConfigCloudExecutionGate(store, cfg, new NullLogger(), () => policy);

        var res = await gate.AuthorizeAsync("m-cloud", "dep-cloud", 1000, "run-1");
        Assert.False(res.Admitted);
        Assert.NotNull(res.Reason);
        Assert.Contains("no cloud budget policy", res.Reason!, StringComparison.OrdinalIgnoreCase);

        // ...and the pooled case WITH a policy source stays None (lane-governed,
        // not a paid cloud request) — the provider proceeds without a reservation.
        var pooledPolicy = new FakePolicy(
            new DeploymentPolicy("m-pooled", DeploymentExecutionMode.Pooled, "pool-a", "dep-pooled"));
        var gate2 = new ConfigCloudExecutionGate(NewStore(), Config("{}"), new NullLogger(), () => pooledPolicy);
        var res2 = await gate2.AuthorizeAsync("m-pooled", "dep-pooled", 1000, "run-p");
        Assert.False(res2.Admitted);
        Assert.Null(res2.Reason);
        Assert.Equal(CloudReservationResult.None, res2);
    }

    // ---- fakes -----------------------------------------------------------------
    private sealed class DeniedGate : ICloudExecutionGate
    {
        public ValueTask<CloudReservationResult> AuthorizeAsync(
            string? modelId, string? deploymentId, int? maxOutputTokens, string? runId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CloudReservationResult.Denied("Cloud budget authorization was denied.", 0));
        public ValueTask ConcludeAsync(string reservationId, bool succeeded, double? actual,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestsSent { get; private set; }
        private string? _body;
        public void SetBody(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestsSent++;
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            if (_body is not null)
                resp.Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }
}
