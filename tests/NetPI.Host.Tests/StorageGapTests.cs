using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50 storage gaps: restart/resume, compaction reconstruction, and WAL
/// concurrency (a live reader observing a writer). The ordering/pagination/model
/// facts live in StorageTests.cs; this file covers the rest.
/// </summary>
public class StorageGapTests
{
    private readonly string _dir;
    public StorageGapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpi-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string Db(string suffix = "") => Path.Combine(_dir, "t" + suffix + ".db");

    private static SessionEntry Msg(string sid, string id, string text, EntryKind kind = EntryKind.Message)
        => new(id, sid, kind,
            new AgentMessage(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, Sequence: 0);

    [Fact]
    public async Task Restart_PersistsAcrossNewInstance()
    {
        var sid = "s";
        using (var a = new SqliteSessionStore(Db()))
        {
            var created = await a.CreateAsync("/ws");
            sid = created.Id;
            await a.AppendAsync(Msg(sid, "e1", "one"), CancellationToken.None);
            await a.AppendAsync(Msg(sid, "e2", "two"), CancellationToken.None);
        }

        // A fresh instance on the same path must see the same data (resume).
        using var b = new SqliteSessionStore(Db());
        var got = await b.ReadAsync(sid, 0, 100, CancellationToken.None);
        Assert.Equal(2, got.Count);
        Assert.Equal("one", (got[0].Message!.Parts[0] as TextPart)!.Text);
        Assert.Equal("two", (got[1].Message!.Parts[0] as TextPart)!.Text);
        var info = await b.GetAsync(sid, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(2, info!.EntryCount);
    }

    [Fact]
    public async Task Compaction_EntryIsPreservedAndBoundariesHistory()
    {
        var st = new SqliteSessionStore(Db("c"));
        var info = await st.CreateAsync("/ws");
        var sid = info.Id;
        await st.AppendAsync(Msg(sid, "h1", "before 1"), CancellationToken.None);
        await st.AppendAsync(Msg(sid, "h2", "before 2"), CancellationToken.None);

        // A compaction checkpoint replaces prior history with a summary.
        var compact = new SessionEntry(
            "c1", sid, EntryKind.Compaction, null,
            JsonSerializer.SerializeToElement(new { summary = "history so far" }),
            DateTimeOffset.UtcNow, Sequence: 0);
        await st.AppendAsync(compact, CancellationToken.None);
        await st.AppendAsync(Msg(sid, "h3", "after"), CancellationToken.None);

        var all = await st.ReadAsync(sid, 0, 100, CancellationToken.None);
        // Reconstruct: the compaction checkpoint sits between before/after and
        // carries its summary payload.
        Assert.Equal(4, all.Count);
        Assert.Equal(EntryKind.Message, all[0].Kind);
        Assert.Equal(EntryKind.Message, all[1].Kind);
        Assert.Equal(EntryKind.Compaction, all[2].Kind);
        Assert.Equal(EntryKind.Message, all[3].Kind);

        var summary = all[2].Payload!.Value;
        Assert.True(summary.TryGetProperty("summary", out _));
        Assert.Equal("history so far", summary.GetProperty("summary").GetString());

        // Monotonic sequences straddle the checkpoint.
        Assert.Equal(1, all[0].Sequence);
        Assert.Equal(2, all[1].Sequence);
        Assert.Equal(3, all[2].Sequence);
        Assert.Equal(4, all[3].Sequence);

        // ReadBefore the compaction checkpoint returns only the earlier history.
        var before = await st.ReadBeforeAsync(sid, all[2].Sequence, 100, CancellationToken.None);
        Assert.All(before, e => Assert.Equal(EntryKind.Message, e.Kind));
        Assert.All(before, e => Assert.NotEqual("after", (e.Message?.Parts[0] as TextPart)?.Text));
        st.Dispose();
    }

    [Fact]
    public async Task Wal_ConcurrentReaderSeesWriterProgress()
    {
        // Two independent connections on the same file: one writes, one reads
        // while the writer runs. WAL mode allows a reader alongside a writer.
        var writer = new SqliteSessionStore(Db("w"));
        var reader = new SqliteSessionStore(Db("w"));
        var info = await writer.CreateAsync("/ws");
        var sid = info.Id;

        const int N = 25;
        var done = new ManualResetEventSlim();
        var writerTask = Task.Run(async () =>
        {
            for (int i = 0; i < N; i++)
            {
                await writer.AppendAsync(Msg(sid, $"w{i}", "data"), CancellationToken.None);
                await Task.Delay(2);
            }
            done.Set();
        });

        // The reader keeps polling until it observes the writer's full progress.
        int seen = 0;
        for (int i = 0; i < 2000 && seen < N; i++)
        {
            var cur = await reader.ReadAsync(sid, 0, N, CancellationToken.None);
            seen = cur.Count;
            await Task.Delay(1);
        }
        await writerTask;

        Assert.Equal(N, seen);
        var final = await reader.ReadAsync(sid, 0, N, CancellationToken.None);
        Assert.Equal(N, final.Count);
        writer.Dispose();
        reader.Dispose();
    }
}
