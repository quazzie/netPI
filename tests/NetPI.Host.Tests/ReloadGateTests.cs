using NetPI.Host.Plugins;

namespace NetPI.Host.Tests;

/// <summary>The host's reload gate on its own: holds, the apply flag and the waiters.</summary>
public static class ReloadGateTests
{
    public static void Register(TestRunner r)
    {
        r.Add("reload gate: holds keep an apply from beginning; the last release opens it once", () =>
        {
            using var gate = new ReloadGate();
            var opened = 0;
            gate.Opened = () => Interlocked.Increment(ref opened);
            var a = gate.Hold("p", "run a");
            var b = gate.Hold("p", "run b");
            Check.False(gate.TryBeginApply(out var held), "held");
            Check.True(held, "because of the holds");
            Check.Equal(2, gate.Holders().Count);
            Check.Contains(gate.Holders()[0], "p: run a");
            a.Dispose();
            a.Dispose();
            Check.Equal(0, opened, "one hold is left");
            b.Dispose();
            Check.Equal(1, opened, "opened by the last release");
            Check.True(Task.Run(() => gate.TryBeginApply(out _)).Result, "nothing holds it now");
            Check.False(gate.TryBeginApply(out held), "one apply at a time");
            Check.False(held, "not held: another apply is under way");
            gate.EndApply();
            return Task.CompletedTask;
        });

        r.Add("reload gate: entering waits for the apply under way only, then holds; a plain hold never waits", async () =>
        {
            using var gate = new ReloadGate();
            Check.True(await Task.Run(() => gate.TryBeginApply(out _)), "apply begun (in another flow)");
            var entering = gate.EnterAsync("p", "new run").AsTask();
            Check.Equal(1, gate.Waiting, "it waits for the swap");
            var hold = gate.Hold("p", "a run handing over");
            Check.Equal(1, gate.Count, "a hold is taken at once, even now");
            hold.Dispose();
            var cts = new CancellationTokenSource();
            var cancelled = gate.EnterAsync("p", "gives up", ct: cts.Token).AsTask();
            cts.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => cancelled);
            Check.Equal(1, gate.Waiting, "a cancelled waiter leaves");
            gate.EndApply();
            using var entered = await entering.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(1, gate.Count, "it holds the gate now");
            Check.False(gate.TryBeginApply(out var held) || !held, "so the next apply waits for it");
        });

        r.Add("reload gate: shutdown releases waiters and applies nothing; a stopped plugin's leftover holds are dropped", async () =>
        {
            var gate = new ReloadGate();
            var run = gate.Hold("p", "run");
            Check.True(await Task.Run(() => { run.Dispose(); return gate.TryBeginApply(out _); }), "apply begun (in another flow)");
            var late = gate.EnterAsync("p", "a run at shutdown").AsTask();
            Check.Equal(1, gate.Waiting);
            gate.Dispose();
            (await late.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            Check.False(gate.TryBeginApply(out _), "nothing is applied after shutdown");
            var left = gate.Hold("p", "never released");
            Check.Equal(1, gate.ReleaseOwner("P"), "a stopped plugin's leftover holds are dropped");
            left.Dispose();
        });
    }
}
