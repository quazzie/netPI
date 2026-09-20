using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>PLAN §30/§38/§50: append-only store, pagination, and model persistence.</summary>
public class StorageTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteSessionStore _store;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpi-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new SqliteSessionStore(Path.Combine(_dir, "t.db"));
    }

    public void Dispose()
    {
        try { _store.Dispose(); Directory.Delete(_dir, true); } catch { }
    }

    private static SessionEntry Msg(string sid, string id, string text)
        => new(id, sid, EntryKind.Message,
            new AgentMessage(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, Sequence: 0);

    [Fact]
    public async Task Append_AssignsAscendingSequences()
    {
        var info = await _store.CreateAsync("/ws");
        await _store.AppendAsync(Msg(info.Id, "e1", "one"), CancellationToken.None);
        await _store.AppendAsync(Msg(info.Id, "e2", "two"), CancellationToken.None);

        var all = await _store.ReadAsync(info.Id, 0, 100, CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all[0].Sequence);
        Assert.Equal(2, all[1].Sequence);
        Assert.Equal("one", (all[0].Message!.Parts[0] as TextPart)!.Text);
    }

    [Fact]
    public async Task Read_PaginatesByRowOffset()
    {
        var info = await _store.CreateAsync("/ws");
        for (var i = 0; i < 10; i++)
            await _store.AppendAsync(Msg(info.Id, "e" + i, "m" + i), CancellationToken.None);

        // rows 5..7 (0-based offset 5, count 3)
        var page = await _store.ReadAsync(info.Id, 5, 3, CancellationToken.None);
        Assert.Equal(3, page.Count);
        Assert.Equal("m5", (page[0].Message!.Parts[0] as TextPart)!.Text);
        Assert.Equal("m7", (page[2].Message!.Parts[0] as TextPart)!.Text);
    }

    [Fact]
    public async Task ReadBefore_ReturnsOlderInAppendOrder()
    {
        var info = await _store.CreateAsync("/ws");
        for (var i = 1; i <= 10; i++)
            await _store.AppendAsync(Msg(info.Id, "e" + i, "m" + i), CancellationToken.None);

        // entries older than sequence 8 -> seq 1..7, oldest first
        var older = await _store.ReadBeforeAsync(info.Id, 8, 100, CancellationToken.None);
        Assert.Equal(7, older.Count);
        Assert.Equal(1, older[0].Sequence);
        Assert.Equal(7, older[6].Sequence);
        Assert.Equal("m1", (older[0].Message!.Parts[0] as TextPart)!.Text);

        // bounded: only 3 most-recent of the older ones, but still ascending
        var top = await _store.ReadBeforeAsync(info.Id, 8, 3, CancellationToken.None);
        Assert.Equal(3, top.Count);
        Assert.Equal(5, top[0].Sequence);
        Assert.Equal(7, top[2].Sequence);
    }

    [Fact]
    public async Task SetModel_PersistsAndSurfacesOnGet()
    {
        var info = await _store.CreateAsync("/ws");
        await _store.SetModelAsync(info.Id, "model-x", "high", CancellationToken.None);

        var reloaded = await _store.GetAsync(info.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal("model-x", reloaded!.ModelId);
        Assert.Equal("high", reloaded.ReasoningLevel);
    }

    [Fact]
    public async Task Rename_TitlesSession()
    {
        var info = await _store.CreateAsync("/ws");
        await _store.RenameAsync(info.Id, "My Title", CancellationToken.None);
        var reloaded = await _store.GetAsync(info.Id, CancellationToken.None);
        Assert.Equal("My Title", reloaded!.Title);
    }
}
