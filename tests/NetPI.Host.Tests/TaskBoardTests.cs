using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Orchestration;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §9 — task-board persistence + the per-agent outstanding-message bound.
/// Against an isolated temp-dir database (migrated by SqliteSessionStore) it
/// verifies:
///   * task CRUD round-trips through the real SqliteOrchestrationStore
///     (create → list → status → delete; team-scoped; status filter);
///   * claim is idempotent for the SAME agent and a competing claim by a
///     DIFFERENT agent FAILS without clobbering the first owner;
///   * a create with a self-dependency is rejected (DAG invariant);
///   * the orchestrator's outstanding-message bound rejects a send once the
///     recipient has `bound` UNCONSUMED messages (a typed rejection, not an
///     unhandled exception); below the bound it is accepted; a bound of 0 =
///     unbounded.
/// </summary>
public sealed class TaskBoardTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-task-board-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* best effort; isolated per run */ }
    }

    private SqliteOrchestrationStore Store()
    {
        // Opening the session store first runs the v4 migration (orchestration tables,
        // incl. agent_tasks). The orchestration store shares the same database file.
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".db");
        using (var _ = new SqliteSessionStore(path)) { /* migrate */ }
        return new SqliteOrchestrationStore(path);
    }

    [Fact]
    public async Task TaskCrud_RoundTripsThroughStore()
    {
        var s = Store();
        var team = "team-1";

        // create
        var created = await s.CreateTaskAsync("t1", team, "Write the tests", []);
        Assert.Equal("t1", created.TaskId);
        Assert.Equal("open", created.Status);
        Assert.Null(created.OwnerAgentId);

        // list (all) — one row
        var all = await s.ListTasksAsync(team, null);
        Assert.Single(all);
        Assert.Equal("Write the tests", all[0].Title);

        // list (other team) — scoped, empty
        var other = await s.ListTasksAsync("team-2", null);
        Assert.Empty(other);

        // list (status filter) — matches open
        var open = await s.ListTasksAsync(team, "open");
        Assert.Single(open);
        var done = await s.ListTasksAsync(team, "done");
        Assert.Empty(done);

        // update status → done
        await s.UpdateTaskStatusAsync("t1", "done", "agent-x");
        var after = await s.ListTasksAsync(team, "done");
        Assert.Single(after);
        Assert.Equal("agent-x", after[0].OwnerAgentId);

        // delete → gone
        var deleted = await s.DeleteTaskAsync("t1");
        Assert.True(deleted);
        Assert.Empty(await s.ListTasksAsync(team, null));

        // delete again → false (no row)
        var again = await s.DeleteTaskAsync("t1");
        Assert.False(again);
    }

    [Fact]
    public async Task Claim_IsIdempotentForSameAgent_AndFailsForCompetingAgent()
    {
        var s = Store();
        var t = await s.CreateTaskAsync("c1", "team-1", "Task", []);
        Assert.Equal("open", t.Status);

        // first claim by agent-A succeeds (open -> claimed)
        var a = await s.ClaimTaskAsync("c1", "agent-A");
        Assert.True(a);
        var ownedA = (await s.ListTasksAsync("team-1", null))[0];
        Assert.Equal("agent-A", ownedA.OwnerAgentId);
        Assert.Equal("claimed", ownedA.Status);

        // idempotent re-claim by the SAME agent succeeds
        var again = await s.ClaimTaskAsync("c1", "agent-A");
        Assert.True(again);

        // competing claim by agent-B FAILS and does NOT clobber agent-A
        var b = await s.ClaimTaskAsync("c1", "agent-B");
        Assert.False(b);
        var still = (await s.ListTasksAsync("team-1", null))[0];
        Assert.Equal("agent-A", still.OwnerAgentId);

        // claim on a nonexistent task fails
        var missing = await s.ClaimTaskAsync("nope", "agent-A");
        Assert.False(missing);
    }

    [Fact]
    public async Task Create_RejectsSelfDependency()
    {
        var s = Store();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => { await s.CreateTaskAsync("self", "team-1", "cyclic", ["self"], default); });
        // nothing was persisted
        Assert.Empty(await s.ListTasksAsync("team-1", null));
    }

    [Fact]
    public async Task Create_UnknownDependencyRejected()
    {
        var s = Store();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => { await s.CreateTaskAsync("t2", "team-1", "needs ghost", ["ghost"], default); });
    }

    [Fact]
    public async Task OutstandingMessageBound_RejectsSendAtTheBound()
    {
        // Wire the real store through a bound-aware orchestrator (bound = 2).
        var s = Store();
        var orch = new AgentOrchestrator(new NoopPluginContext(), s,
            maxDelegationDepth: 3, maxOutstandingMessages: 2);

        const string to = "agent-recipient";

        // Two sends below the bound are accepted (0 -> 1 -> 2 outstanding).
        var s1 = await orch.SendMessageAsync("agent-sender", to, "note", "m1");
        var s2 = await orch.SendMessageAsync("agent-sender", to, "note", "m2");
        Assert.True(s1 >= 1 && s2 >= 1);

        // A third send would make 3 > bound 2 — rejected as a typed,
        // actionable exception the tool boundary converts to a bounded Err.
        var ex = await Assert.ThrowsAsync<MessageBoundExceededException>(
            async () => { await orch.SendMessageAsync("agent-sender", to, "note", "m3"); });
        Assert.Equal("agent-recipient", ex.ToAgentId);
        Assert.Equal(2, ex.CurrentCount);
        Assert.Equal(2, ex.Bound);

        // The rejected send did NOT enqueue: still exactly 2 outstanding.
        Assert.Equal(2, await s.CountOutstandingAsync(to));
    }

    [Fact]
    public async Task OutstandingMessageBound_ZeroMeansUnbounded()
    {
        // A bound of 0 (unset) means UNBOUNDED — the orchestrator never checks,
        // so N sends are all accepted. Proves the bound is the configured value,
        // not a hardcoded constant.
        var s = Store();
        var unbounded = new AgentOrchestrator(new NoopPluginContext(), s,
            maxDelegationDepth: 3, maxOutstandingMessages: 0);
        for (var i = 0; i < 10; i++)
            await unbounded.SendMessageAsync("a", "b", "note", $"m{i}");
        Assert.Equal(10, await s.CountOutstandingAsync("b"));
    }
}
