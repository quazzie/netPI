using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Tests;

/// <summary>
/// The restart by itself (RestartWhenIdle): a publish's marker, the reload gate, the quiet time after a client's change,
/// the setting, a failed restart; the start that clears the marker; the successor's arguments.
/// </summary>
public static class RestartTests
{
    public static void Register(TestRunner r)
    {
        r.Add("restart: a publish's marker and nothing going on: app.restarting, the gate closed to new runs, the shell's restart once", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("restart-plugins"));
            var app = T.TempDir("restart-app");
            var restarts = 0;
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<JsonElement>();
            using var sub = server.Events.Subscribe("app.restarting", e => { lock (events) events.Add(JsonSerializer.SerializeToElement(e.Data)); });
            using var w = new RestartWhenIdle(server.Kernel, () => { Interlocked.Increment(ref restarts); restarted.TrySetResult(); return Task.CompletedTask; },
                tick: Timeout.InfiniteTimeSpan, announce: TimeSpan.Zero, quiet: TimeSpan.Zero, appDir: app);

            w.Check();
            Check.Equal(null, w.Status, "no marker: nothing waits");
            File.WriteAllText(Path.Combine(app, RestartWhenIdle.MarkerName), """{ "at": "2026-10-23T15:00:00Z", "reasons": ["new host files", "plugins built against changed contracts"] }""");
            w.Check();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            w.Check();
            Check.Equal(1, restarts, "once");
            await Wait.Until(() => { lock (events) return events.Count == 1; }, "app.restarting published", 5000);
            Check.Equal("new host files", events[0].GetProperty("reasons")[0].GetString());
            Check.False(server.Kernel.Plugins.Gate.TryBeginApply(out _), "the gate stays closed to new runs until the process ends");
            Check.Contains(JsonSerializer.Serialize(w.Status), "restarting");
            server.Kernel.Plugins.Gate.EndApply();
        });

        r.Add("restart: disposed while the restart is announced, no successor starts", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("restart-plugins"));
            var app = T.TempDir("restart-app");
            var restarts = 0;
            var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = server.Events.Subscribe("app.restarting", _ => announced.TrySetResult());
            var w = new RestartWhenIdle(server.Kernel, () => { Interlocked.Increment(ref restarts); return Task.CompletedTask; },
                tick: Timeout.InfiniteTimeSpan, announce: TimeSpan.FromMilliseconds(300), quiet: TimeSpan.Zero, appDir: app);
            File.WriteAllText(Path.Combine(app, RestartWhenIdle.MarkerName), "not json");
            w.Check();
            await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
            w.Dispose();
            await Task.Delay(600); // past the 300 ms announce, with room
            Check.Equal(0, restarts, "the restart did not run after Dispose");
            server.Kernel.Plugins.Gate.EndApply();
        });

        r.Add("restart: a run (a hold on the reload gate) and then a client's change keep it waiting; a plugin's own call does not", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("restart-plugins"));
            var k = server.Kernel;
            var app = T.TempDir("restart-app");
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var w = new RestartWhenIdle(k, () => { restarted.TrySetResult(); return Task.CompletedTask; },
                tick: Timeout.InfiniteTimeSpan, announce: TimeSpan.Zero, quiet: TimeSpan.FromMilliseconds(400), appDir: app);
            File.WriteAllText(Path.Combine(app, RestartWhenIdle.MarkerName), "not json");

            var run = k.Plugins.Gate.Hold("netpi.runtime", "agent a running");
            w.Check();
            Check.Contains(JsonSerializer.Serialize(w.Status), "agent a running", "it waits for the run");
            Check.Contains(JsonSerializer.Serialize(w.Status), "an update", "a marker it cannot read still asks for a restart");
            run.Dispose();
            w.Check();
            Check.Contains(JsonSerializer.Serialize(w.Status), "without a change", "and then for the quiet time");
            await Task.Delay(500);
            var parameters = JsonSerializer.SerializeToElement(new { path = "agent.maxTurns", value = 9 });
            await k.Rpc.InvokeAsync("settings.set", parameters, null, fromClient: true, CancellationToken.None);
            w.Check();
            Check.False(restarted.Task.IsCompleted, "a change from a client starts the quiet time again");
            await Task.Delay(500);
            await k.Rpc.InvokeAsync("settings.set", JsonSerializer.SerializeToElement(new { path = "agent.maxTurns", value = (int?)null }), null, CancellationToken.None);
            await k.Rpc.InvokeAsync("app.info", JsonSerializer.SerializeToElement(new { }), null, fromClient: true, CancellationToken.None);
            w.Check();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // a plugin's call and a client's read are no change from the user
            k.Plugins.Gate.EndApply();
        });

        r.Add("restart: host.autoRestart off waits; a failed restart opens the gate again and drops the marker", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("restart-plugins"));
            var k = server.Kernel;
            var app = T.TempDir("restart-app");
            var marker = Path.Combine(app, RestartWhenIdle.MarkerName);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = server.Events.Subscribe("app.restartFailed", _ => failed.TrySetResult());
            using var w = new RestartWhenIdle(k, () => throw new InvalidOperationException("no successor"),
                tick: Timeout.InfiniteTimeSpan, announce: TimeSpan.Zero, quiet: TimeSpan.Zero, appDir: app);
            File.WriteAllText(marker, """{ "reasons": ["-NextStart"] }""");
            k.Settings.Set("host.autoRestart", JsonValue.Create(false));
            w.Check();
            Check.Contains(JsonSerializer.Serialize(w.Status), "host.autoRestart is off");
            k.Settings.Set("host.autoRestart", null);
            w.Check();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Wait.Until(() => !File.Exists(marker), "the marker is dropped: no retry loop until the next publish", 5000);
            Check.True(Task.Run(() => k.Plugins.Gate.TryBeginApply(out _)).Result, "the gate is open again: NetPI goes on");
            k.Plugins.Gate.EndApply();
        });

        r.Add("restart: the start that installs a publish clears its marker; the successor's arguments", () =>
        {
            var app = T.TempDir("restart-marker");
            File.WriteAllText(Path.Combine(app, RestartWhenIdle.MarkerName), "{}");
            PendingBuild.Install(app, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Check.False(File.Exists(Path.Combine(app, RestartWhenIdle.MarkerName)), "answered by this start, even with nothing pending");

            Check.Equal("--home|h|--port|1", string.Join("|", Successor.Without(["--home", "h", "--after", "123", "--port", "1"], out var pid)));
            Check.Equal(123, pid);
            Check.Equal("--multi", string.Join("|", Successor.Without(["--multi"], out pid)));
            Check.Equal(null, pid);
            var rest = Successor.WaitForPredecessor(["--after", "2147483000", "--quiet"], out var successor);
            Check.True(successor, "named a predecessor (one that is gone: no wait)");
            Check.Equal("--quiet", string.Join("|", rest));
            return Task.CompletedTask;
        });
    }
}
