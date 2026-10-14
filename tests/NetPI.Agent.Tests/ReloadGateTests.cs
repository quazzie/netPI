using System.Runtime.CompilerServices;
using NetPI.Host.Plugins;

namespace NetPI.Agent.Tests;

/// <summary>
/// The runtime's side of the host's reload gate: every run holds it from its start to its end (so the host never reloads
/// a plugin - this one included - under a run), and a new run waits for a reload being applied.
/// </summary>
public static class ReloadGateTests
{
    public static void Register(TestRunner t)
    {
        t.Add("reload gate: a run holds the gate from start to end, a queued follow-up included; the gate opens once, when the last run ends", HeldForTheRun);
        t.Add("reload gate: a new run waits for a reload being applied, a steer does not", NewRunWaitsForApply);
        t.Add("reload gate: after the drain limit a user's new chat turn yields to the deferred reload; the running agent finishes untouched", DrainDoesNotTouchRuns);
    }

    private static async Task<(TestHost Host, ReloadGate Gate)> StartAsync()
    {
        var gate = new ReloadGate();
        var h = await TestHost.StartAsync(h => h.Services.Register<IReloadGate>(gate));
        return (h, gate);
    }

    /// <summary>Waits for <paramref name="until"/> on its first call, then answers at once.</summary>
    private static ModelHandler Blocked(Task until)
    {
        var calls = 0;
        return (r, ct) => Interlocked.Increment(ref calls) == 1 ? After(until, ct) : Reply.Text("ok");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> After(Task until, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await until.WaitAsync(ct);
        await foreach (var e in Reply.Text("done").WithCancellation(ct)) yield return e;
    }

    private static async Task HeldForTheRun()
    {
        var (h, gate) = await StartAsync();
        await using var host = h;
        var opened = 0;
        gate.Opened = () => Interlocked.Increment(ref opened);
        var release = new TaskCompletionSource();
        h.Catalog.Handler = Blocked(release.Task);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "first");
        Check.Equal(1, gate.Count, "the run holds the gate");
        Check.Contains(gate.Holders()[0], "netpi.runtime: agent ");
        Check.False(gate.TryBeginApply(out var held), "no reload while it runs");
        Check.True(held);
        await h.SendAsync(s.Id, "second", DeliveryMode.Queue);
        release.SetResult();
        var a = await h.IdleAsync(s.Id);
        Check.Equal(AgentStatus.Idle, a.Status);
        await Wait.Until(() => gate.Count == 0, "the last run released its hold");
        Check.Equal(1, Volatile.Read(ref opened), "the gate did not open between the two runs");
    }

    private static async Task NewRunWaitsForApply()
    {
        var (h, gate) = await StartAsync();
        await using var host = h;
        var release = new TaskCompletionSource();
        h.Catalog.Handler = Blocked(release.Task);
        var busy = h.NewSession();
        await h.SendAsync(busy.Id, "running");
        Check.Equal(1, gate.Count);
        var idle = h.NewSession();
        // the busy run ends; then a reload is being applied when the next message comes
        release.SetResult();
        await h.IdleAsync(busy.Id);
        await Wait.Until(() => gate.Count == 0, "gate free");
        Check.True(await Task.Run(() => gate.TryBeginApply(out _)), "apply begun");
        var send = h.SendAsync(idle.Id, "while a reload is applied");
        await Wait.Until(() => gate.Waiting == 1, "the new run waits for the swap");
        Check.False(send.IsCompleted, "not started on the version being retired");
        Check.Equal(AgentStatus.Idle, h.Runtime.GetBySession(busy.Id)!.Status);
        gate.EndApply();
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        var a = await h.IdleAsync(idle.Id);
        Check.Equal(AgentStatus.Idle, a.Status);
        Check.Equal(1, a.Runs);
    }

    private static async Task DrainDoesNotTouchRuns()
    {
        var (h, gate) = await StartAsync();
        await using var host = h;
        var now = DateTimeOffset.UtcNow;
        gate.Now = () => now;
        gate.DrainAfter = TimeSpan.FromMinutes(30);
        var applied = 0;
        var pending = 0;   // what the plugin manager knows: a reload is deferred
        gate.Opened = () => Task.Run(() =>
        {
            if (Volatile.Read(ref pending) == 0 || !gate.TryBeginApply(out _)) return;
            Volatile.Write(ref pending, 0);
            Interlocked.Increment(ref applied);
            gate.EndApply();
        });
        var release = new TaskCompletionSource();
        h.Catalog.Handler = Blocked(release.Task);
        var running = h.NewSession();
        await h.SendAsync(running.Id, "long task");
        Volatile.Write(ref pending, 1);
        gate.Deferred();   // a publish arrived: its reload waits for the run

        now += TimeSpan.FromMinutes(31);
        var other = h.NewSession();
        var send = h.SendAsync(other.Id, "a new chat turn");
        await Wait.Until(() => gate.Waiting == 1, "the user's new run yields to the overdue reload");
        await h.SendAsync(running.Id, "a steer into the running chat");   // never waits: no run starts
        Check.Equal(AgentStatus.Running, h.Runtime.GetBySession(running.Id)!.Status, "the running agent is untouched");
        Check.Equal(0, Volatile.Read(ref applied), "and nothing is forced");

        release.SetResult();
        var a = await h.IdleAsync(running.Id);
        Check.Equal(AgentStatus.Idle, a.Status, "finished normally, not failed");
        Check.True(a.Error is null, a.Error ?? "");
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        Check.Equal(1, Volatile.Read(ref applied), "the reload went in between");
        Check.Equal(AgentStatus.Idle, (await h.IdleAsync(other.Id)).Status);
    }
}
