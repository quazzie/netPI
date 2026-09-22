using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §9: the agent runtime drains the durable mailbox at model-turn
/// boundaries (top of each loop iteration — after a complete tool batch, and
/// once at segment start) and injects the messages as provenance-carrying
/// User entries. Delivery is bounded per turn (rest stays queued), consume-on-
/// read, and a persistence failure of a delivered message fails the run
/// (a consumed-but-unpersisted message would be lost).
/// </summary>
public class MailboxDrainTests
{
    private sealed class FakeOrchStore : IOrchestrationStore
    {
        public readonly List<AgentMailboxMessage> Queue = [];
        public readonly List<int> DrainCounts = [];
        public bool DrainCalled;

        public ValueTask<int> SendMessageAsync(string messageId, string fromAgentId, string toAgentId,
            string? teamId, string kind, string body, IReadOnlyList<string>? artifacts,
            string? idempotencyKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string agentId, int count,
            CancellationToken ct = default)
        {
            DrainCalled = true;
            DrainCounts.Add(count);
            var take = Queue.Take(count).ToList();
            Queue.RemoveRange(0, take.Count);
            return ValueTask.FromResult<IReadOnlyList<AgentMailboxMessage>>(take);
        }

        public ValueTask<bool> NoteMessageForWaitsAsync(string toAgentId, string fromAgentId, string kind,
            CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<AgentIdentity> EnsureRootAgentAsync(string sessionId, string? teamId, string title,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentSpawnOutcome> SpawnChildAsync(string operationId, string? parentAgentId,
            string? teamId, string modelId, string? poolId, string? deploymentId, string brief, string title,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<AgentIdentity?> GetAgentAsync(string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<AgentIdentity?> GetAgentBySessionAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TransitionAsync(string assignmentId, int expectedVersion,
            AgentAssignmentLifecycle lifecycle, AgentState phase, string? poolId, string? laneId,
            string? deploymentId, string? reason, string? checkpointRef, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListNonterminalAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask MarkToolBatchInFlightAsync(string assignmentId, string agentId, string sessionId,
            int transcriptCursor, string? toolCallIdsJson, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask ClearToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> HasToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask WriteLaneJournalAsync(string assignmentId, string? poolId, string? laneId, string state,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<QueuedAdoptionInfo>> ListQueuedForAdoptionAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow?> GetByRunIdAsync(string runId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<string?> GetRunIdAsync(string assignmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<AgentAssignmentRow> CreateAssignmentAsync(string operationId, string agentId,
            string sessionId, string? teamId, string? parentAgentId, string? modelId, string? poolId,
            string? deploymentId, string title, string? briefRef, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask SetSessionWorkspaceAsync(string sessionId, string? workspace, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListRecentTerminalAsync(int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask RegisterWaitAsync(string waitId, string agentId, string assignmentId,
            AgentWaitCondition condition, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<bool> NoteTerminalForWaitsAsync(string assignmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AgentSatisfiedWait>> ListSatisfiedWaitsAsync(string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask MarkWaitConsumedAsync(string waitId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static (AgentRuntime Rt, FakeOrchStore Orch, FakeProvider Provider, FailingAppendStore Store) Make(
        IReadOnlyList<AgentMailboxMessage> seeded, int failAfter = int.MaxValue)
    {
        var ctx = new NoopPluginContext();
        var tool = new EchoTool();
        ctx.Add("tools", new TestRegistry(tool));
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
                [new ToolCallPart("t1", "echo",
                    JsonSerializer.SerializeToElement(new { msg = "ping" }))], DateTimeOffset.UtcNow))],
            [new ModelCompleted(new AgentMessage("a2", MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow))],
        ]);
        ctx.Add("provider", provider);
        var store = new FailingAppendStore(failAfter);
        ctx.Add("sessions", store);
        var orch = new FakeOrchStore();
        orch.Queue.AddRange(seeded);
        ctx.Add("orchestration-store", orch);
        return (new AgentRuntime(ctx), orch, provider, store);
    }

    private static AgentMailboxMessage Msg(string from, string kind, string body, int seq)
        => new($"m-{seq}", from, "agent-1", null, kind, body, seq, DateTimeOffset.UtcNow, null);

    private static AgentRunOptions Options() => new()
    {
        SessionId = "s1",
        ModelId = "model",
        AgentId = "agent-1",
        Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart("echo")], DateTimeOffset.UtcNow)],
    };

    [Fact]
    public async Task DrainedMessages_ReachModel_WithProvenance_BeforeNextCall()
    {
        var (rt, orch, provider, store) = Make([
            Msg("peer-1", "finding", "the file is at /tmp/x", 1),
            Msg("peer-2", "question", "which branch is current?", 2),
        ]);

        var result = await rt.RunAsync(Options(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Turns);
        // Drained at the boundary: delivered to the model BEFORE the next call.
        var transcript = provider.LastMessages!;
        Assert.Contains(transcript, m =>
            m.Role == MessageRole.User &&
            (m.Parts[0] as TextPart)!.Text ==
            "[agent message from peer-1 (finding)] the file is at /tmp/x");
        Assert.Contains(transcript, m =>
            m.Role == MessageRole.User &&
            (m.Parts[0] as TextPart)!.Text ==
            "[agent message from peer-2 (question)] which branch is current?");
        // The messages precede the assistant turn that consumed them.
        var firstDrain = transcript.ToList().FindIndex(m => (m.Parts[0] as TextPart)?.Text?.StartsWith("[agent message from") == true);
        var assistant = transcript.ToList().FindIndex(m => m.Role == MessageRole.Assistant);
        Assert.True(firstDrain < assistant);
        // Consume-on-read: the queue is empty and persisted to the session store.
        Assert.Empty(orch.Queue);
        Assert.True(orch.DrainCalled);
    }

    [Fact]
    public async Task Drain_IsBoundedPerTurn_KeepingRemainderForLater()
    {
        var seeded = Enumerable.Range(1, 15)
            .Select(i => Msg("peer", "finding", $"body {i}", i)).ToList();
        var (rt, orch, _, _) = Make(seeded);

        var result = await rt.RunAsync(Options(), CancellationToken.None);

        Assert.True(result.Ok);
        // Bounded by the per-turn cap on the first boundary; the tail was
        // delivered at the NEXT boundary (still queued, then drained).
        Assert.Equal(10, orch.DrainCounts[0]);
        Assert.Empty(orch.Queue);
    }

    [Fact]
    public async Task DrainPersistenceFailure_FailsTheRun()
    {
        // failAfter: 0 → the drained mailbox message (append #1 — drain fires at
        // iteration-1 top, before any model output) fails to persist → the run
        // must stop rather than proceed on a transcript the store lacks.
        var (rt, orch, _, _) = Make([Msg("peer-1", "finding", "body", 1)], failAfter: 0);

        var result = await rt.RunAsync(Options(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("failed to persist a mailbox message", result.Note);
    }

    [Fact]
    public async Task NoOrchestrationStore_DrainIsANoop()
    {
        var ctx = new NoopPluginContext();
        var tool = new EchoTool();
        ctx.Add("tools", new TestRegistry(tool));
        ctx.Add("provider", new FakeProvider([
            [new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant, [new TextPart("hi")], DateTimeOffset.UtcNow))],
        ]));
        ctx.Add("sessions", new FailingAppendStore(int.MaxValue));
        // No "orchestration-store" registered — AgentId is set but there is
        // nothing to drain.
        var rt = new AgentRuntime(ctx);

        var result = await rt.RunAsync(Options(), CancellationToken.None);

        Assert.True(result.Ok);
    }
}
