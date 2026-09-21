using System;
using System.Linq;
using NetPI.Abstractions;
using NetPI.Tools;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 H (Package H, slice 2): the foreground-process lifecycle the Tools
/// plugin exposes (<c>IForegroundProcessTracker</c>, service id
/// "foreground-processes") that the Activity plugin consumes. Verifies the
/// lifecycle transitions — started → running → finished, bounded recent tail,
/// and no enumeration of unrelated OS processes (only what was Started).
/// </summary>
public class ForegroundProcessTrackerTests
{
    [Fact]
    public void Started_IsRunning_ThenFinished_MovesToRecent()
    {
        var t = new ForegroundProcessTracker();

        t.Started("bash", "npm run build", "/ws", "sess-1", 1001);
        t.Started("powershell", "dotnet test", "/ws", "sess-2", 1002);

        // Both are running; Running() is oldest-first.
        var running = t.Running();
        Assert.Equal(2, running.Count);
        Assert.All(running, r => Assert.True(r.IsRunning));
        Assert.Equal(1001, running[0].ProcessId);
        Assert.Equal(1002, running[1].ProcessId);
        // Command/workdir/session are captured for the Activity "Processes" row.
        Assert.Equal("npm run build", running[0].Command);
        Assert.Equal("/ws", running[0].WorkingDirectory);
        Assert.Equal("sess-1", running[0].SessionId);
        Assert.Equal(1001, running[0].ProcessId);
        Assert.True(running[0].ExitCode is null);

        // Finishing the first removes it from running, records its exit code,
        // and lands it at the head of the recent list.
        t.Finished(1001, 0);
        running = t.Running();
        Assert.Single(running);
        Assert.Equal(1002, running[0].ProcessId);

        var recent = t.Recent();
        Assert.Single(recent);
        var done = recent[0];
        Assert.False(done.IsRunning);
        Assert.Equal(1001, done.ProcessId);
        Assert.Equal(0, done.ExitCode);
        Assert.NotNull(done.ExitedAt);

        // Finishing the second: the recent list is now [1002, 1001] (newest first).
        t.Finished(1002, 2);
        recent = t.Recent();
        Assert.Equal(2, recent.Count);
        Assert.Equal(1002, recent[0].ProcessId); // newest first
        Assert.Equal(1001, recent[1].ProcessId);
        Assert.Equal(2, recent[0].ExitCode);
    }

    [Fact]
    public void Finished_IsANoOp_WhenNotRunning()
    {
        var t = new ForegroundProcessTracker();
        t.Started("bash", "x", null, null, 7);
        t.Finished(7, 0);
        // A duplicate finish must not throw or create a phantom recent entry.
        t.Finished(7, 0);
        Assert.Empty(t.Running());
        Assert.Single(t.Recent());
    }

    [Fact]
    public void Recent_IsBounded_WhenProcessesOutrunTheWindow()
    {
        // The tracker caps its recent tail. Drive it well past the bound; the
        // tail must stay bounded and newest-first.
        var t = new ForegroundProcessTracker();
        const int n = 1000;
        for (int pid = 1; pid <= n; pid++)
        {
            t.Started("bash", $"cmd{pid}", null, null, pid);
            t.Finished(pid, 0);
        }
        var recent = t.Recent(256);
        Assert.True(recent.Count <= 256);
        // The NEWEST pids are at the head of the recent tail.
        Assert.Equal(n, recent[0].ProcessId);
        Assert.Equal(n - 1, recent[1].ProcessId);
        Assert.Empty(t.Running());
    }

    [Fact]
    public void OnlyProcessesStartedByTheTracker_AreExposed()
    {
        // The Activity plugin must never see OS processes the Tools plugin did
        // not start. A fresh tracker with no Started calls exposes nothing —
        // there is no OS-enumeration path to pull in unrelated processes.
        var t = new ForegroundProcessTracker();
        Assert.Empty(t.Running());
        Assert.Empty(t.Recent());
    }
}
