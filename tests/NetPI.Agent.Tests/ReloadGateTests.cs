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

}
