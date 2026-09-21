using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.BackgroundTasks;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// The background-tasks right-panel surface (docs/web-panels.md). BgWebApp on
/// real Kestrel (port 0) with the real BackgroundJobManager driving a real
/// powershell child process: verifies the list/output/kill endpoints the
/// /panel/background page polls, including kill → Killed state and the
/// output tail.
/// </summary>
public class BackgroundSurfaceTests
{
    private static readonly HttpClient Http = new();

    // ---- fakes ---------------------------------------------------------------

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
        // astra-1 P5: a running background job takes a generation self-lease;
        // in this host-less harness the lease is a no-op.
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeContext(FakeRegistry services) : IPluginContext
    {
        public PluginInfo Info { get; } = new("netpi.backgroundtasks", "Background Tasks Test", "0.2.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => throw new NotSupportedException();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement.Clone();
        public IPluginLogger Log => new NullLogger();
        // astra-1 P5: a running background job takes a generation self-lease;
        // in this host-less harness it is a no-op.
        public IValueLease<object> LeaseSelf() => new NoopLease();
    }

    private sealed class NoopLease : IValueLease<object>
    {
        public object Value => throw new InvalidOperationException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Resolves anything to powershell -Command so the manager can spawn it.</summary>
    private sealed class FakeResolver : IShellCommandResolver
    {
        public string ShellId { get; } = "powershell";
        public ValueTask<ResolvedCommand> ResolveAsync(string command, string workingDirectory, CancellationToken ct)
            => ValueTask.FromResult(new ResolvedCommand(
                "powershell", ["-NoProfile", "-NonInteractive", "-Command", command], workingDirectory, null));
    }

    // ---- e2e ------------------------------------------------------------------

    private static async Task<(BgWebApp app, BackgroundJobManager mgr, string baseUrl, string tempRoot)> MakeAppAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netpi-bg-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        var reg = new FakeRegistry();
        reg.Add("resolver:powershell", new FakeResolver());
        var mgr = new BackgroundJobManager(new FakeContext(reg));
        var app = new BgWebApp(mgr, new NullLogger(), 0, urls: "http://127.0.0.1:0");
        await app.StartAsync(CancellationToken.None);
        return (app, mgr, app.BoundUrl!, tempRoot);
    }

    private static async Task<JsonElement> PostJsonAsync(string url)
    {
        var resp = await Http.PostAsync(url, null);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> GetJsonAsync(string url)
    {
        var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task PanelServesJobsAndKillStopsTheJob()
    {
        var (app, mgr, baseUrl, tempRoot) = await MakeAppAsync();
        BackgroundJobInfo? job = null;
        try
        {
            job = await mgr.StartAsync("powershell", "Start-Sleep -Seconds 60", tempRoot, null);

            // The panel page is served from the plugin assembly.
            var page = await Http.GetStringAsync(baseUrl + "/panel/background");
            Assert.Contains("background", page, StringComparison.OrdinalIgnoreCase);

            // The running job shows up in the list endpoint.
            var d = await GetJsonAsync(baseUrl + "/api/bg/jobs");
            Assert.Equal(1, d.GetProperty("count").GetInt32());
            Assert.Equal("Running", d.GetProperty("jobs")[0].GetProperty("state").GetString());
            Assert.Equal(job.JobId, d.GetProperty("jobs")[0].GetProperty("id").GetString());

            // Kill via the endpoint flips the job to Killed.
            var kill = await PostJsonAsync(baseUrl + $"/api/bg/{job.JobId}/kill");
            Assert.True(kill.GetProperty("ok").GetBoolean());
            var after = await GetJsonAsync(baseUrl + "/api/bg/jobs");
            Assert.Equal("Killed", after.GetProperty("jobs")[0].GetProperty("state").GetString());

            // Killing an unknown job reports ok:false (still a 200).
            var missing = await PostJsonAsync(baseUrl + "/api/bg/does-not-exist/kill");
            Assert.False(missing.GetProperty("ok").GetBoolean());
        }
        finally
        {
            if (job is not null) await mgr.KillAsync(job.JobId);
            await app.StopAsync(CancellationToken.None);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OutputTailReturnsTheLastChars()
    {
        var (app, mgr, baseUrl, tempRoot) = await MakeAppAsync();
        BackgroundJobInfo? job = null;
        try
        {
            job = await mgr.StartAsync("powershell",
                "1..5 | ForEach-Object { Write-Output ('line ' + $_) }; Start-Sleep -Seconds 60",
                tempRoot, null);

            // stdout is captured asynchronously — poll for it (bounded).
            string text = "";
            for (int i = 0; i < 50; i++)
            {
                text = (await GetJsonAsync(baseUrl + $"/api/bg/{job.JobId}/output?chars=8000"))
                    .GetProperty("text").GetString() ?? "";
                if (text.Contains("line 5", StringComparison.Ordinal)) break;
                await Task.Delay(100);
            }
            Assert.Contains("line 5", text, StringComparison.Ordinal);

            // The tail endpoint is bounded to the requested window.
            var tail = await GetJsonAsync(baseUrl + $"/api/bg/{job.JobId}/output?chars=20");
            Assert.True(tail.GetProperty("text").GetString()!.Length <= 20);
            Assert.True(tail.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            if (job is not null) await mgr.KillAsync(job.JobId);
            await app.StopAsync(CancellationToken.None);
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
