using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §11a (F) outbound backpressure: one connected non-reading client must
/// not stall updates for every other client. The two pure helpers at the core of
/// the fix are exercised directly: <c>ConcurrentDeliverAsync</c> fans a broadcast
/// out to every client concurrently (a stalled client can't block the others), and
/// <c>CreateDeliveryCancellationToken</c> links the client-lifetime + operation
/// tokens plus a bounded delivery timeout (a dead / non-reading client times out
/// instead of holding the send forever). Per-client ordering is preserved by the
/// FIFO outbox + single writer inside <c>Client</c> (astra-2) — the fan-out only
/// enqueues, so ordering is independent of task scheduling.
/// </summary>
public sealed class OutboundBackpressureTests
{
    private static readonly CancellationToken NoCt = CancellationToken.None;

    // ---- ConcurrentDeliverAsync -------------------------------------------------

    [Fact]
    public async Task EmptyAndSingleClient_Complete()
    {
        await NetPI.Web.WebApp.ConcurrentDeliverAsync(
            new List<Func<CancellationToken, Task>>(), NoCt); // must not throw

        var done = false;
        var single = new List<Func<CancellationToken, Task>>
        {
            _ => Task.Run(() => { done = true; }),
        };
        await NetPI.Web.WebApp.ConcurrentDeliverAsync(single, NoCt);
        Assert.True(done);
    }

    [Fact]
    public async Task FanOut_IsConcurrent_NotSerial()
    {
        // Four clients each enter "in flight" and wait on a release gate; the
        // max simultaneous in-flight count must reach 4 — proving the fan-out runs
        // them concurrently (a serial loop would peak at 1). Deterministic (the
        // gate releases only once all four are in flight), no arbitrary sleeps.
        int running = 0, peak = 0;
        object l = new();
        var allEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var send = new Func<CancellationToken, Task>(_ => Task.Run(async () =>
        {
            lock (l)
            {
                running++;
                if (running > peak) peak = running;
                if (running == 4) allEntered.TrySetResult();
            }
            await release.Task;
            lock (l) running--;
        }));

        var task = NetPI.Web.WebApp.ConcurrentDeliverAsync(
            new Func<CancellationToken, Task>[] { send, send, send, send }, NoCt);

        await allEntered.Task; // all four are in flight together
        // the fan-out must run clients concurrently, not serially
        Assert.True(peak == 4, $"fan-out must run clients concurrently (peak in-flight={peak}, a serial loop peaks at 1)");
        release.SetResult();
        await task;
    }

    // ---- CreateDeliveryCancellationToken -----------------------------------------

    [Fact]
    public void ClientLifetime_CancelsTheLinkedDelivery()
    {
        using var client = new CancellationTokenSource();
        using var delivery = NetPI.Web.WebApp.CreateDeliveryCancellationToken(client.Token, NoCt, 60_000);
        Assert.False(delivery.Token.IsCancellationRequested);
        client.Cancel();
        Assert.True(delivery.Token.IsCancellationRequested,
            "a client disconnect must abort its in-flight delivery (no unbounded stall)");
    }

    [Fact]
    public void OperationCancel_CancelsTheLinkedDelivery()
    {
        using var op = new CancellationTokenSource();
        using var delivery = NetPI.Web.WebApp.CreateDeliveryCancellationToken(NoCt, op.Token, 60_000);
        op.Cancel();
        Assert.True(delivery.Token.IsCancellationRequested,
            "an aborted operation must abort the delivery to that client");
    }

    [Fact]
    public async Task BoundedTimeout_CancelsTheLinkedDelivery()
    {
        // No external cancel — only the bounded delivery timeout should fire.
        using var delivery = NetPI.Web.WebApp.CreateDeliveryCancellationToken(NoCt, NoCt, 120);
        Assert.False(delivery.Token.IsCancellationRequested);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // returns (by cancellation) when the (linked) token cancels — here only the
        // bounded timeout can do that. The cancellation is EXPECTED (it is the
        // signal that the delivery was aborted), so swallow it and just assert the
        // timing: a real stall would make this run far longer than the window.
        try { await Task.Delay(int.MaxValue).WaitAsync(delivery.Token); }
        catch (TaskCanceledException) { /* the timeout fired: expected */ }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"delivery must time out after the bounded window, not stall (took {sw.ElapsedMilliseconds}ms)");
    }
}
