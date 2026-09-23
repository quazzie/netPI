using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Activity;
using NetPI.Host.Events;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §15 F2 — "budgets/usage endpoint exists, no frontend". The endpoint IS
/// the Activity Work panel: the Work panel is a cross-origin iframe served by the
/// Activity plugin's own Kestrel (see AGENTS.md §Plugin architecture), and its
/// activity.html already renders a BUDGET section (renderBudgets). The residual
/// is that the budgets section of the combined /api/activity/work payload has
/// NO test coverage. These tests pin the read-only budget/usage contract:
///   - a present ICloudBudgetStore ("cloud-budgets") surfaces in the payload
///     under budgets with explicit Available=true and per-team rows, tokens vs
///     currency units mapped correctly, and team order deterministic (id ASC);
///   - a missing/failing store degrades ONLY the budgets section (Available=false)
///     — the rest of the panel still renders.
/// </summary>
public class WorkPanelBudgetsTests
    {
    private static readonly HttpClient Http = new();

    // ---- fake cloud budget store -------------------------------------------

    private sealed class FakeBudgetStore : ICloudBudgetStore
    {
        private readonly IReadOnlyList<CloudBudgetUsage> _usage;
        public FakeBudgetStore(params CloudBudgetUsage[] usage) => _usage = usage;

        public ValueTask UpsertAllowanceAsync(CloudBudgetAllowance allowance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<CloudReservationResult> ReserveAsync(string teamId, double estimate, double convertRate, string? runId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<CloudReservationInfo?> ReserveInfoAsync(string reservationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask ReleaseAsync(string reservationId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<bool> SettleAsync(string reservationId, double actual, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CloudBudgetUsage>> UsageAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_usage);
    }

    private sealed class ThrowingBudgetStore : ICloudBudgetStore
    {
        public ValueTask UpsertAllowanceAsync(CloudBudgetAllowance a, CancellationToken c = default) => throw new NotSupportedException();
        public ValueTask<CloudReservationResult> ReserveAsync(string t, double e, double r, string? id = null, CancellationToken c = default) => throw new NotSupportedException();
        public ValueTask<CloudReservationInfo?> ReserveInfoAsync(string i, CancellationToken c = default) => throw new NotSupportedException();
        public ValueTask ReleaseAsync(string i, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<bool> SettleAsync(string i, double a, CancellationToken c = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CloudBudgetUsage>> UsageAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("budget store failed");
    }

    // ---- harness (mirrors WorkPanelDataTests) --------------------------------

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
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeContext(FakeRegistry services, IEventBus? events = null) : IPluginContext
    {
        private readonly IEventBus _events = events ?? new EventBus();
        public PluginInfo Info { get; } = new("netPI.Activity", "Activity Test", "0.1.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => _events;
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement.Clone();
        public IPluginLogger Log => new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    private static async Task<(ActivityWebApp app, string baseUrl)> MakeAsync(FakeRegistry reg)
    {
        var app = new ActivityWebApp(new FakeContext(reg), 0);
        await app.StartAsync(CancellationToken.None);
        return (app, app.BoundUrl!);
    }

    private static async Task<JsonElement> GetJsonAsync(string url)
    {
        var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---- tests ---------------------------------------------------------------

    [Fact]
    public async Task WorkPanel_BudgetsEmbedded_WhenCloudBudgetStorePresent()
    {
        var store = new FakeBudgetStore(
            // out of order on purpose — the payload must sort by team id ASC.
            new CloudBudgetUsage("team-b", "USD", CloudBudgetUnit.Currency, 100, 5, 30, 70),
            new CloudBudgetUsage("team-a", "tokens", CloudBudgetUnit.Tokens, 1_000_000, 250, 100_000, 900_000));
        var reg = new FakeRegistry();
        reg.Add("cloud-budgets", store);
        var (app, url) = await MakeAsync(reg);
        try
        {
            var work = await GetJsonAsync(url + "/api/activity/work");
            var budgets = work.GetProperty("budgets");
            Assert.True(budgets.GetProperty("available").GetBoolean(),
                "a present cloud-budgets store must surface the budget section as available");
            var teams = budgets.GetProperty("teams").EnumerateArray().ToArray();
            Assert.True(teams.Length == 2, $"expected 2 team rows, saw {teams.Length}");
            // sorted by team id ASC (deterministic display order).
            Assert.Equal("team-a", teams[0].GetProperty("teamId").GetString());
            Assert.Equal("team-b", teams[1].GetProperty("teamId").GetString());
            // token unit is mapped to "tokens", currency to "currency".
            Assert.Equal("tokens", teams[0].GetProperty("unit").GetString());
            Assert.Equal("1000000", teams[0].GetProperty("limit").GetRawText());
            Assert.Equal("900000", teams[0].GetProperty("remaining").GetRawText());
            Assert.Equal("currency", teams[1].GetProperty("unit").GetString());
            Assert.Equal("100", teams[1].GetProperty("limit").GetRawText());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task WorkPanel_BudgetsDegrade_NotPanel_WhenStoreAbsent()
    {
        var reg = new FakeRegistry(); // no "cloud-budgets" service at all
        var (app, url) = await MakeAsync(reg);
        try
        {
            var work = await GetJsonAsync(url + "/api/activity/work");
            var budgets = work.GetProperty("budgets");
            Assert.False(budgets.GetProperty("available").GetBoolean(),
                "a missing budget store must degrade the budgets section, not the panel");
            Assert.Empty(budgets.GetProperty("teams").EnumerateArray());
            // the rest of the panel is still there (the section degrades, not the view).
            _ = work.GetProperty("agents");
            _ = work.GetProperty("lanes");
            _ = work.GetProperty("processes");
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task WorkPanel_BudgetsDegrade_WhenUsageFails()
    {
        var reg = new FakeRegistry();
        reg.Add("cloud-budgets", new ThrowingBudgetStore());
        var (app, url) = await MakeAsync(reg);
        try
        {
            var work = await GetJsonAsync(url + "/api/activity/work");
            var budgets = work.GetProperty("budgets");
            Assert.False(budgets.GetProperty("available").GetBoolean(),
                "a failing UsageAsync must surface as an unavailable budget section");
            Assert.Empty(budgets.GetProperty("teams").EnumerateArray());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
