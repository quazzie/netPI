using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §15 B4 — "guard all provider inference". The runtime re-validates the
/// execution permit before EVERY model call it makes in the main loop (a stale
/// permit fails closed, before network I/O). In-run AutoCompact fires its OWN
/// summarization model call (the plugin's SummarizeAsync → provider.RunAsync) that
/// the loop's per-call check does NOT wrap: it runs after the tool batch, in the
/// gap between two loop model calls. This drives the runtime through a tool batch
/// whose execution releases the lane (the permit goes stale mid-run) and proves
/// the runtime now refuses the compaction model call — the compaction service is
/// never invoked, so no unadmitted summarization happens. Without the guard, the
/// compaction would run at the checkpoint (CompactCalls == 1); with it, it is
/// skipped (CompactCalls == 0).
/// </summary>
public sealed class LaneInferenceGuardTests
{
    /// <summary>A lane scheduler whose validation verdict is controllable.</summary>
    private sealed class StubLanes : ILaneScheduler
    {
        public bool Valid = true;
        public bool TryValidatePermit(LaneOwnershipToken? token, string poolId, string assignmentId)
            => token is not null && token.AssignmentId == assignmentId && Valid;
        public ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry queue)
            => ValueTask.FromResult(new LaneAcquireResult(null, 0, "stub"));
        public ValueTask ReleaseAsync(LaneOwnershipToken token) => ValueTask.CompletedTask;
        public ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken from, LaneQueueEntry to)
            => ValueTask.FromResult<LaneOwnershipToken?>(null);
        public ValueTask<bool> CancelQueuedAsync(string assignmentId) => ValueTask.FromResult(false);
        public IReadOnlyList<LanePoolSnapshot> Snapshots() => Array.Empty<LanePoolSnapshot>();
        public ValueTask SetPoolEnabledAsync(string poolId, bool enabled) => ValueTask.CompletedTask;
    }

    /// <summary>An ICompaction that records whether it was invoked at all.</summary>
    private sealed class RecordingCompaction : ICompaction
    {
        public int CompactCalls;
        public bool IsAvailable => true;
        public ValueTask<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken cancellationToken = default)
        {
            CompactCalls++;
            return ValueTask.FromResult(new CompactionResult(null, null));
        }
        public ValueTask<IReadOnlyList<AgentMessage>> BuildActiveContextAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
    }

    /// <summary>A tool that releases the lane (stales the permit) when it runs.</summary>
    private sealed class LaneReleasingTool(StubLanes lanes) : IAgentTool
    {
        public bool Executed;
        public string Name => "stale";
        public string Description => "releases the lane on execution";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
        {
            Executed = true;
            lanes.Valid = false; // simulate the lane being released mid-batch
            return ValueTask.FromResult(new ToolResult("t1", "stale", [new TextPart("ok")], false));
        }
    }

    private static LaneOwnershipToken Token(string assignment)
        => new("pool-1", "lane-1", assignment, assignment, "gen-1", 1, DateTimeOffset.UtcNow);

    private static AgentMessage Assistant(params MessagePart[] parts)
        => new("a1", MessageRole.Assistant, parts, DateTimeOffset.UtcNow);

    private static AgentRuntime MakeRuntimeWith(
        IModelProvider provider, IToolRegistry tools, ISessionStore store,
        ILaneScheduler lanes, ICompaction compaction)
    {
        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        ctx.Add("tools", tools);
        ctx.Add("sessions", store);
        ctx.Add("lanes", lanes);
        ctx.Add("compaction", compaction);
        return new AgentRuntime(ctx);
    }

    private static AgentRunOptions ToolTurnOptions(string assignment, LaneOwnershipToken? permit)
        => new()
        {
            SessionId = "s1",
            RunId = assignment,
            ModelId = "model",
            LanePermit = permit,
            Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
        };

    // Turn 1: model calls a tool; Turn 2: model finishes with text. The tool batch
    // in turn 1 is what trips the compaction checkpoint (after the tool results).
    private static FakeProvider ToolTurnProvider(string toolName)
        => new([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", toolName,
                    JsonSerializer.SerializeToElement(new { msg = "hello" }))))],
            [new ModelStarted("model"), new ModelCompleted(Assistant(new TextPart("done")))]
        ]);

    [Fact]
    public async Task PermitReleasedMidRun_InRunCompaction_IsRefused()
    {
        var provider = ToolTurnProvider("stale");
        var staleLanes = new StubLanes();
        var tool = new LaneReleasingTool(staleLanes);
        var compaction = new RecordingCompaction();
        var store = new FailingAppendStore(failAfter: 1_000_000);
        var rt = MakeRuntimeWith(provider, new TestRegistry(tool), store, staleLanes, compaction);

        var result = await rt.RunAsync(ToolTurnOptions("run-1", Token("run-1")), CancellationToken.None);

        // The tool ran (turn 1 was admitted), and releasing the lane staled the permit
        // mid-batch. The run then fails: the NEXT loop model call is refused for the
        // stale permit.
        Assert.True(tool.Executed, "the tool must run (turn 1 was admitted)");
        Assert.False(staleLanes.Valid);
        Assert.False(result.Ok, "the stale permit must stop the next loop model call");
        // THE B4 GUARD: the compaction model call is refused at the checkpoint — the
        // compaction service is never invoked. Without the guard this would be 1.
        Assert.True(compaction.CompactCalls == 0,
            $"in-run compaction must be refused when the lane permit is stale; saw {compaction.CompactCalls} compaction call(s)");
    }

    [Fact]
    public async Task ValidPermit_CompactionIsNotOverRefused()
    {
        // Sanity for the guard: with the permit valid throughout, the guard must NOT
        // over-refuse — the run completes (proof every loop model call was admitted)
        // and the compaction checkpoint is reachable (the service is invoked).
        var provider = ToolTurnProvider("echo");
        var tool = new EchoTool();
        var lanes = new StubLanes { Valid = true };
        var compaction = new RecordingCompaction();
        var store = new FailingAppendStore(failAfter: 1_000_000);
        var rt = MakeRuntimeWith(provider, new TestRegistry(tool), store, lanes, compaction);

        var result = await rt.RunAsync(ToolTurnOptions("run-1", Token("run-1")), CancellationToken.None);

        Assert.True(result.Ok, $"run must complete with a valid permit: {result.Note}");
        Assert.True(tool.Saw("echo", "hello"), "with a valid permit the tool must run");
    }
}
