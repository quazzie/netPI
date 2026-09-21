using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.BackgroundTasks;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 H (Package H data layer): the background-job contract the Activity
/// plugin will consume. Verifies the three behaviors that slice must get right:
///   1. <c>Slice</c> no longer copies the whole retained buffer (PLAN §10: "the
///      current Slice copies the entire retained string before slicing — remove
///      that unnecessary allocation"); slices are exact and cursor-based.
///   2. ownership + the ORIGINAL working directory are FROZEN at start (a later
///      project switch must not relabel an existing process).
///   3. completed jobs are retained only within a bounded window (running jobs
///      are always kept; the oldest exited jobs are pruned first).
/// </summary>
public class BackgroundActivityDataTests
{
    // ---- fakes (mirror BackgroundSurfaceTests; each test file carries its own) ----

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
        public IValueLease<object> LeaseSelf() => new NoopLease();
    }

    private sealed class NoopLease : IValueLease<object>
    {
        public object Value => throw new InvalidOperationException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Resolves to a powershell -Command (the manager spawns it) in the given workdir.</summary>
    private sealed class FakeResolver : IShellCommandResolver
    {
        public string ShellId { get; } = "powershell";
        public ValueTask<ResolvedCommand> ResolveAsync(string command, string workingDirectory, CancellationToken ct)
            => ValueTask.FromResult(new ResolvedCommand(
                "powershell", ["-NoProfile", "-NonInteractive", "-Command", command], workingDirectory, null));
    }

    private static FakeContext MakeContext()
    {
        var reg = new FakeRegistry();
        reg.Add("resolver:powershell", new FakeResolver());
        return new FakeContext(reg);
    }

    private static string MakeTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "netpi-bg-act-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ---- 1. Slice: exact, cursor-based, no full-buffer copy -----------------------

    [Fact]
    public void Slice_ReturnsExactRangesAndCursors()
    {
        var job = new BackgroundJob { JobId = "j", ShellId = "bash", Command = "x" };
        job.AppendOutput("hello world"); // 11 chars, nothing dropped

        var (full, next, truncated) = job.Slice(0);
        Assert.Equal("hello world", full);
        Assert.Equal(11, next);
        Assert.False(truncated);

        // a mid-buffer slice returns only the tail from the cursor.
        var (tail, tailNext, tailTrunc) = job.Slice(6);
        Assert.Equal("world", tail);
        Assert.Equal(11, tailNext);
        Assert.False(tailTrunc);

        // a cursor at the end yields an empty slice, not an error.
        var (atEnd, atEndNext, atEndTrunc) = job.Slice(11);
        Assert.Equal(string.Empty, atEnd);
        Assert.Equal(11, atEndNext);
        Assert.False(atEndTrunc);

        // a cursor past the end yields an empty slice, not a throw.
        var (past, pastNext, pastTrunc) = job.Slice(1000);
        Assert.Equal(string.Empty, past);
        Assert.Equal(11, pastNext);
        Assert.False(pastTrunc);
    }

    [Fact]
    public void Slice_RespectsTheChunkCapAndAdvancesTheCursor()
    {
        var job = new BackgroundJob { JobId = "j", ShellId = "bash", Command = "x" };
        const int total = 100_000; // under the 256_000 ring cap, so nothing is dropped
        job.AppendOutput(new string('a', total));
        const int chunkCap = 64_000;

        // A from-front read (offset <= dropped, here both 0) serves the WHOLE retained
        // buffer — the path GetTailAsync relies on to learn the total.
        var (front, frontNext, frontTrunc) = job.Slice(0);
        Assert.Equal(total, front.Length);
        Assert.Equal(total, frontNext);
        Assert.False(frontTrunc);

        // A MID-buffer read (past the dropped region) is capped at the chunk size —
        // the allocation-free path — and the cursor advances by what was served.
        const int mid = 10_000;
        var (slice, sliceNext, sliceTrunc) = job.Slice(mid);
        Assert.Equal(chunkCap, slice.Length);     // one mid-read returns at most the cap
        Assert.Equal(mid + chunkCap, sliceNext);  // cursor advances by what was served
        Assert.False(sliceTrunc);

        // A final read past the served window returns the remainder, then nothing.
        var (rest, restNext, restTrunc) = job.Slice(mid + chunkCap);
        Assert.Equal(total - mid - chunkCap, rest.Length);
        Assert.Equal(total, restNext);
        Assert.False(restTrunc);
    }

    [Fact]
    public void Slice_ReportsTruncationWhenTheRingDroppedOldest()
    {
        var job = new BackgroundJob { JobId = "j", ShellId = "bash", Command = "x" };
        // 44_000 chars drop off the front of the 256_000 ring; 256_000 remain.
        const int dropped = 44_000;
        const int kept = BackgroundJob.MaxOutputChars;
        job.AppendOutput(new string('b', dropped + kept));

        // A from-front read while the ring has dropped the earliest chars serves the
        // retained buffer and reports the start as lost (truncated) — the cursor is
        // the ABSOLUTE total so a continuation read is still well-defined.
        var (text, next, truncated) = job.Slice(0);
        Assert.True(truncated);        // the requested start (0) was lost
        Assert.Equal(kept, text.Length); // the whole retained buffer is served
        Assert.Equal(dropped + kept, next); // cursor = absolute total (dropped + kept)
    }

    // ---- 2. ownership + original working directory, frozen at start --------------

    [Fact]
    public async Task StartAsync_CapturesOwnershipAndOriginalWorkingDirectory()
    {
        var tempRoot = MakeTempRoot();
        var mgr = new BackgroundJobManager(MakeContext());
        string? jobId = null;
        try
        {
            var ownership = new JobOwnership("sess-1", "run-1", "proj-1");
            var info = await mgr.StartAsync(
                "powershell", "Write-Output ok", tempRoot, null, ownership, CancellationToken.None);
            jobId = info.JobId;

            // The ownership is what the caller passed, frozen at start.
            Assert.Equal("sess-1", info.SessionId);
            Assert.Equal("run-1", info.RunId);
            Assert.Equal("proj-1", info.ProjectId);
            // The ORIGINAL working directory is the resolved one (the caller's workdir
            // here), captured once and never relabeled by a later project switch.
            Assert.Equal(tempRoot, info.WorkingDirectory);

            // A re-read agrees with the frozen ownership (no drift).
            var reloaded = await mgr.GetAsync(info.JobId, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal("sess-1", reloaded!.SessionId);
            Assert.Equal(tempRoot, reloaded.WorkingDirectory);
        }
        finally
        {
            if (jobId is not null)
                await mgr.KillAsync(jobId, CancellationToken.None);
        }
    }

    // ---- 3. bounded recent-completions -------------------------------------------

    [Fact]
    public async Task CompletedJobs_AreRetainedOnlyWithinTheBoundedWindow()
    {
        var tempRoot = MakeTempRoot();
        // A small cap makes the prune observable with a handful of processes.
        var mgr = new BackgroundJobManager(MakeContext(), maxRetainedJobs: 2);
        var ids = new List<string>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var info = await mgr.StartAsync(
                    "powershell", "Write-Output ok", tempRoot, null, null, CancellationToken.None);
                ids.Add(info.JobId);
            }

            // Poll until the table settles: at most the cap, and no job still running.
            // Pruning happens on each process exit (OnExited), so the table settles
            // shortly after the last job exits.
            var list = await PollUntilAsync(mgr);
            Assert.True(list.Count <= 2, $"expected at most 2 retained, got {list.Count}");
            Assert.All(list, j => Assert.NotEqual(BackgroundJobState.Running, j.State));
        }
        finally
        {
            foreach (var id in ids)
                await mgr.KillAsync(id, CancellationToken.None);
        }
    }

    /// <summary>Poll ListAsync until at most <c>cap</c> jobs remain and none is Running,
    /// or the deadline lapses (in which case the raw list is returned for the
    /// caller's asserts to explain). Pruning runs on each process exit, so the
    /// table settles shortly after the last job finishes.</summary>
    private static async Task<List<BackgroundJobInfo>> PollUntilAsync(BackgroundJobManager mgr)
    {
        const int cap = 2;
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            var list = (await mgr.ListAsync(CancellationToken.None)).ToList();
            if (list.Count <= cap && list.All(j => j.State != BackgroundJobState.Running))
                return list;
            if (Environment.TickCount64 > deadline)
                return list; // let the caller's asserts explain the state
            await Task.Delay(100);
        }
    }
}
