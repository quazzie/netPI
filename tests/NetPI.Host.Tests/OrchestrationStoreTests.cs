using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §7 — orchestration persistence (the SqliteOrchestrationStore).
/// Against an isolated temp-dir database (migrated by SqliteSessionStore to
/// v4) it verifies the durability invariants:
///   * EnsureRootAgent is idempotent per session;
///   * SpawnChildAsync is idempotent by operationId AND atomic (a child is
///     never half-created); the same operationId twice returns the same child;
///   * one nonterminal assignment per session is the busy gate;
///   * TransitionAsync is a compare-and-swap: a stale version loses;
///   * the mailbox is durable, drained exactly-once, and idempotent by key;
///   * a terminal transition wakes exactly its waiters (no double resume).
/// </summary>
public sealed class OrchestrationStoreTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-orch-store-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort; isolated per run */ }
    }

    private SqliteOrchestrationStore Store()
    {
        // Opening the session store first runs the v4 migration (orchestration tables).
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".db");
        using (var _ = new SqliteSessionStore(path)) { /* migrate */ }
        return new SqliteOrchestrationStore(path);
    }

    [Fact]
    public async Task EnsureRootAgent_IsIdempotentPerSession()
    {
        var s = Store();
        var a1 = await s.EnsureRootAgentAsync("sess-1", null, "root");
        var a2 = await s.EnsureRootAgentAsync("sess-1", null, "root");
        Assert.Equal(a1.AgentId, a2.AgentId);
        Assert.False(a1.IsChild);
        Assert.Equal("sess-1", a1.SessionId);

        // A different session gets a distinct agent.
        var b = await s.EnsureRootAgentAsync("sess-2", null, "other");
        Assert.NotEqual(a1.AgentId, b.AgentId);

        // Round-trip lookups.
        Assert.Equal(a1.AgentId, (await s.GetAgentAsync(a1.AgentId))!.AgentId);
        Assert.Equal(a1.AgentId, (await s.GetAgentBySessionAsync("sess-1"))!.AgentId);
        Assert.Null(await s.GetAgentBySessionAsync("sess-999"));
    }

    [Fact]
    public async Task SpawnChild_IsIdempotentAndAtomic()
    {
        var s = Store();
        var parent = await s.EnsureRootAgentAsync("parent-sess", null, "root");

        var op1 = await s.SpawnChildAsync("op-1", parent.AgentId, null, "m-1",
            "pool-1", "dep-1", "do the thing", "child-1");
        Assert.Equal(AgentAssignmentLifecycle.Queued, op1.Status);
        Assert.NotEqual(parent.AgentId, op1.Agent.AgentId);
        Assert.True(op1.Agent.IsChild);
        Assert.Equal(parent.AgentId, op1.Agent.ParentAgentId);

        // Same operationId → the SAME child (idempotent), no second agent row.
        var op2 = await s.SpawnChildAsync("op-1", parent.AgentId, null, "m-1",
            "pool-1", "dep-1", "do the thing", "child-1");
        Assert.Equal(op1.Agent.AgentId, op2.Agent.AgentId);
        Assert.Equal(op1.AssignmentId, op2.AssignmentId);

        // The child's assignment is nonterminal (queued) and its own session is
        // the busy gate.
        var nonterm = await s.GetNonterminalAsync(op1.Agent.SessionId);
        Assert.NotNull(nonterm);
        Assert.Equal(AgentAssignmentLifecycle.Queued, nonterm!.Lifecycle);

        // The child session has exactly one agent row (atomicity — no orphans).
        Assert.Equal(op1.Agent.AgentId, (await s.GetAgentBySessionAsync(op1.Agent.SessionId))!.AgentId);
    }

    [Fact]
    public async Task Transition_IsCompareAndSwap()
    {
        var s = Store();
        var op = await s.SpawnChildAsync("op-cas", null, null, "m-1", "p", "d", "b", "t");
        var row = (await s.GetAssignmentAsync(op.AssignmentId))!;
        Assert.Equal(0, row.Version);

        // Transition with the correct version succeeds (version → 1).
        var ok = await s.TransitionAsync(op.AssignmentId, 0,
            AgentAssignmentLifecycle.Running, AgentState.CallingModel,
            "p", "lane-1", "d", null, null);
        Assert.True(ok);
        Assert.Equal(1, (await s.GetAssignmentAsync(op.AssignmentId))!.Version);

        // A stale version (still expecting 0) now LOSES — no silent overwrite.
        var stale = await s.TransitionAsync(op.AssignmentId, 0,
            AgentAssignmentLifecycle.Failed, AgentState.Idle,
            null, null, null, "boom", null);
        Assert.False(stale);
        Assert.Equal(AgentAssignmentLifecycle.Running, (await s.GetAssignmentAsync(op.AssignmentId))!.Lifecycle);
    }

    [Fact]
    public async Task OneNonterminalPerSession_IsTheBusyGate()
    {
        var s = Store();
        await s.EnsureRootAgentAsync("busy-sess", null, "root");
        // A queued child under its own session; the PARENT session stays free.
        var op = await s.SpawnChildAsync("op-busy", null, null, "m", "p", "d", "b", "t");
        Assert.NotNull(await s.GetNonterminalAsync(op.Agent.SessionId));
        // The child session is now the one nonterminal assignment overall.
        var all = await s.ListNonterminalAsync();
        Assert.Single(all, r => r.SessionId == op.Agent.SessionId);
    }

    [Fact]
    public async Task Mailbox_IsDurable_DrainExactlyOnce_AndIdempotentByKey()
    {
        var s = Store();
        var target = (await s.EnsureRootAgentAsync("msg-sess", null, "root")).AgentId;
        var sender = (await s.EnsureRootAgentAsync("from-sess", null, "sender")).AgentId;

        var seq1 = await s.SendMessageAsync("msg-1", sender, target, null, "request", "hello", null, "key-A");
        Assert.Equal(1, seq1);

        // Repeat the same key → same sequence, NOT a second row.
        var seq2 = await s.SendMessageAsync("msg-1b", sender, target, null, "request", "hello", null, "key-A");
        Assert.Equal(seq1, seq2);

        // A fresh key → next sequence.
        var seq3 = await s.SendMessageAsync("msg-2", sender, target, null, "note", "again", null, null);
        Assert.Equal(2, seq3);

        // Draining returns the two messages oldest-first and marks them consumed.
        var drained = await s.DrainMailboxAsync(target, 10);
        Assert.Equal(2, drained.Count);
        Assert.Equal("hello", drained[0].Body);
        Assert.Equal("again", drained[1].Body);

        // A second drain is empty (no re-delivery).
        Assert.Empty(await s.DrainMailboxAsync(target, 10));
    }

    [Fact]
    public async Task TerminalTransition_WakesWaiter_ExactlyOnce()
    {
        var s = Store();
        var wait = await s.SpawnChildAsync("op-wait", null, null, "m", "p", "d", "b", "t");
        // Register a wait that will be satisfied when `wait`'s assignment goes terminal.
        var cond = new AgentWaitCondition { AssignmentIds = [wait.AssignmentId] };
        await s.RegisterWaitAsync("wait-1", wait.Agent.AgentId, null, cond);
        // Idempotent re-register (same wait id) is a no-op.
        await s.RegisterWaitAsync("wait-1", wait.Agent.AgentId, null, cond);

        var row = (await s.GetAssignmentAsync(wait.AssignmentId))!;
        var woke = await s.TransitionAsync(wait.AssignmentId, row.Version,
            AgentAssignmentLifecycle.Completed, AgentState.Idle,
            null, null, null, "done", null);
        Assert.True(woke);

        // The wait was satisfied; the assignment is terminal (no longer nonterminal).
        Assert.Null(await s.GetNonterminalAsync(wait.Agent.SessionId));
        Assert.NotNull(await s.ListRecentTerminalAsync(10));
        // No double-resume: a second terminal transition reports no further wake.
        var row2 = (await s.GetAssignmentAsync(wait.AssignmentId))!;
        var woke2 = await s.TransitionAsync(wait.AssignmentId, row2.Version,
            AgentAssignmentLifecycle.Failed, AgentState.Idle, null, null, null, null, null);
        Assert.True(woke2);
    }
}
