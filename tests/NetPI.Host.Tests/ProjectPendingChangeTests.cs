using System.IO;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 D2 — the pending-project-change store: one pending selection per
/// session (a later enqueue REPLACES the earlier unapplied one), the full
/// request (operation id + authoritative snapshot) round-trips through the
/// DB, the pending row survives a store re-open (simulated host restart), and
/// ClearAsync removes only the row whose operation id still matches.
/// </summary>
public class ProjectPendingChangeTests : IDisposable
{
    private readonly string _root;
    private readonly string _db;

    public ProjectPendingChangeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "netpi-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "netpi.db");
        // The session store owns the DDL (migration v3) — open it first, as the
        // plugin does, so the pending_project_changes table exists.
        using var sessionStore = new SqliteSessionStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private ProjectContextSnapshot Snap(string id, string name, string ws, string instructions) =>
        new(id, name, ws, ["/AGENTS.md"], instructions, "hash-" + id, DateTimeOffset.UtcNow);

    private ProjectChangeRequest Req(string op, string sid, string projectId, string name, string ws, string instructions) =>
        new(op, sid, "proj-" + projectId, -1, Snap(projectId, name, ws, instructions));

    private static int RowCount(string dbPath)
    {
        var conn = new SqliteConnection("Data Source=" + dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_project_changes;";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        conn.Close();
        return n;
    }

    [Fact]
    public async Task Enqueue_LaterSelection_ReplacesEarlier_OnlyOneRowPerSession()
    {
        var store = new PendingProjectChangeStore(_db);

        await store.EnqueueAsync(Req("op-1", "s1", "x", "Project X", "/ws/x", "X rules"), CancellationToken.None);
        var afterFirst = await store.PendingAsync("s1", CancellationToken.None);
        Assert.NotNull(afterFirst);
        Assert.Equal("op-1", afterFirst!.OperationId);
        Assert.Equal("Project X", afterFirst.ProjectName);
        Assert.Equal(1, RowCount(_db));

        // A LATER selection supersedes the earlier unapplied one.
        await store.EnqueueAsync(Req("op-2", "s1", "y", "Project Y", "/ws/y", "Y rules"), CancellationToken.None);
        var afterSecond = await store.PendingAsync("s1", CancellationToken.None);
        Assert.NotNull(afterSecond);
        Assert.Equal("op-2", afterSecond!.OperationId);
        Assert.Equal("Project Y", afterSecond.ProjectName);
        Assert.Equal("Y rules", afterSecond.Request.Snapshot.EffectiveInstructions);
        Assert.Equal("/ws/y", afterSecond.Request.Snapshot.WorkspacePath);

        // Exactly ONE pending row — the replace did not accumulate.
        Assert.Equal(1, RowCount(_db));
    }

    [Fact]
    public async Task Pending_SurvivesStoreReopen_RoundTripsSnapshot()
    {
        var store = new PendingProjectChangeStore(_db);
        await store.EnqueueAsync(
            Req("op-9", "s2", "z", "Project Z", "/ws/z", "Z effective instructions"), CancellationToken.None);
        store = null!; // instance goes away — the row must stay in the DB

        var reopened = new PendingProjectChangeStore(_db);
        var pending = await reopened.PendingAsync("s2", CancellationToken.None);

        Assert.NotNull(pending);
        Assert.Equal("op-9", pending!.OperationId);
        Assert.Equal("proj-z", pending.Request.ProjectId);
        // The FULL request round-tripped — operation id, session, project and
        // the authoritative snapshot (instructions verbatim, sources, hash).
        Assert.Equal("s2", pending.Request.SessionId);
        Assert.Equal("Project Z", pending.Request.Snapshot.ProjectName);
        Assert.Equal("/ws/z", pending.Request.Snapshot.WorkspacePath);
        Assert.Equal(["/AGENTS.md"], pending.Request.Snapshot.InstructionSources);
        Assert.Equal("Z effective instructions", pending.Request.Snapshot.EffectiveInstructions);
        Assert.Equal("hash-z", pending.Request.Snapshot.ContentHash);
        Assert.Equal(-1, pending.Request.ExpectedContextRevision);

        Assert.Null(await reopened.PendingAsync("no-such-session", CancellationToken.None));
    }

    [Fact]
    public async Task ClearAsync_OnlyClearsTheRowWhoseOperationIdMatches()
    {
        var store = new PendingProjectChangeStore(_db);
        await store.EnqueueAsync(Req("op-1", "s3", "a", "Project A", "/ws/a", "A"), CancellationToken.None);
        // op-2 supersedes op-1 (op-1's row values are overwritten).
        await store.EnqueueAsync(Req("op-2", "s3", "b", "Project B", "/ws/b", "B"), CancellationToken.None);

        // A stale clear (the already-superseded op-1) must be a NO-OP — the
        // current selection (op-2) survives.
        Assert.False(await store.ClearAsync("s3", "op-1", CancellationToken.None));
        Assert.NotNull(await store.PendingAsync("s3", CancellationToken.None));

        // The current selection clears.
        Assert.True(await store.ClearAsync("s3", "op-2", CancellationToken.None));
        Assert.Null(await store.PendingAsync("s3", CancellationToken.None));
        Assert.Equal(0, RowCount(_db));
    }

    [Fact]
    public async Task DuplicateOperationId_DoesNotDuplicateRows()
    {
        var store = new PendingProjectChangeStore(_db);
        var change = Req("op-same", "s4", "c", "Project C", "/ws/c", "C rules");

        var first = await store.EnqueueAsync(change, CancellationToken.None);
        // The same operation retried (e.g. a client redelivery) — no second row.
        var second = await store.EnqueueAsync(change, CancellationToken.None);

        Assert.Equal("op-same", first.OperationId);
        Assert.Equal("op-same", second.OperationId);
        Assert.Equal(1, RowCount(_db));
        var pending = await store.PendingAsync("s4", CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("op-same", pending!.OperationId);
        Assert.Equal("C rules", pending.Request.Snapshot.EffectiveInstructions);
    }

    [Fact]
    public async Task DistinctSessions_KeepIndependentPendingRows()
    {
        var store = new PendingProjectChangeStore(_db);
        await store.EnqueueAsync(Req("op-1", "sA", "1", "P1", "/1", "one"), CancellationToken.None);
        await store.EnqueueAsync(Req("op-2", "sB", "2", "P2", "/2", "two"), CancellationToken.None);

        var a = await store.PendingAsync("sA", CancellationToken.None);
        var b = await store.PendingAsync("sB", CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("op-1", a!.OperationId);
        Assert.Equal("op-2", b!.OperationId);
        Assert.Equal(2, RowCount(_db));

        Assert.True(await store.ClearAsync("sA", "op-1", CancellationToken.None));
        Assert.Null(await store.PendingAsync("sA", CancellationToken.None));
        Assert.NotNull(await store.PendingAsync("sB", CancellationToken.None));
    }
}
