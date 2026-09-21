using System;
using System.IO;
using System.Threading;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P0.5: one effective runtime home. An explicit <c>NETPI_HOME</c>
/// drives both the config location and the default SQLite database path —
/// two runtime homes must keep separate sessions (no shared netpi.db, no
/// silent migration between them).
/// </summary>
public class RuntimeHomeTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly string? _savedEnv;

    public RuntimeHomeTests()
    {
        _rootA = Path.Combine(Path.GetTempPath(), "netpi-home-A-" + Guid.NewGuid().ToString("N"));
        _rootB = Path.Combine(Path.GetTempPath(), "netpi-home-B-" + Guid.NewGuid().ToString("N"));
        _savedEnv = Environment.GetEnvironmentVariable("NETPI_HOME");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETPI_HOME", _savedEnv);
        foreach (var d in new[] { _rootA, _rootB })
            try { Directory.Delete(d, true); } catch { }
    }

    private static SessionEntry Msg(string sid, string id, string text)
        => new(id, sid, EntryKind.Message,
            new AgentMessage(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, Sequence: 0);

    [Fact]
    public void RuntimeHome_HonorsNetpiHomeEnv()
    {
        Environment.SetEnvironmentVariable("NETPI_HOME", _rootA);
        Assert.Equal(Path.GetFullPath(_rootA), RuntimeHome.Dir);
    }

    [Fact]
    public void DefaultDbPath_FollowsNetpiHomeEnv()
    {
        Environment.SetEnvironmentVariable("NETPI_HOME", _rootA);
        Assert.Equal(Path.Combine(Path.GetFullPath(_rootA), "netpi.db"), DefaultDbPath());
    }

    /// <summary>Two runtime homes keep fully separate session databases.</summary>
    [Fact]
    public async Task TwoRuntimeHomes_KeepSeparateSessions()
    {
        var ct = CancellationToken.None;

        Environment.SetEnvironmentVariable("NETPI_HOME", _rootA);
        using var storeA = new SqliteSessionStore(Path.Combine(RuntimeHome.Dir, "netpi.db"));
        var sessA = await storeA.CreateAsync("/workspace-a");
        await storeA.AppendAsync(Msg(sessA.Id, "a1", "in home A"), ct);

        Environment.SetEnvironmentVariable("NETPI_HOME", _rootB);
        using var storeB = new SqliteSessionStore(Path.Combine(RuntimeHome.Dir, "netpi.db"));
        var sessB = await storeB.CreateAsync("/workspace-b");
        await storeB.AppendAsync(Msg(sessB.Id, "b1", "in home B"), ct);

        // Each home sees exactly its own session — nothing leaked across.
        var listA = await storeA.ListAsync(50, 0, ct);
        var listB = await storeB.ListAsync(50, 0, ct);
        Assert.Single(listA);
        Assert.Single(listB);
        Assert.Equal(sessA.Id, listA[0].Id);
        Assert.Equal(sessB.Id, listB[0].Id);

        // The databases physically live in their respective homes.
        Assert.True(File.Exists(Path.Combine(_rootA, "netpi.db")));
        Assert.True(File.Exists(Path.Combine(_rootB, "netpi.db")));
    }

    /// <summary>DefaultDbPath is private to the plugin — verify through the
    /// store the plugin would build (same expression: RuntimeHome + netpi.db).</summary>
    private static string DefaultDbPath() => Path.Combine(RuntimeHome.Dir, "netpi.db");
}
