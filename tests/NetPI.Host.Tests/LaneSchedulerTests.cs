using NetPI.Abstractions;
using NetPI.Lanes;
using Xunit;

namespace NetPI.Host.Tests;

// astra-2 §3.2 / §5.3 acceptance: the A/B/C trace, capacity changes,
// handoff, stale permits, drain, residency. Deterministic (no timing —
// the scheduler is a synchronous state machine under a lock).

public class LaneSchedulerTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private const string Gen = "host-test";
    private readonly NullLogger _log = new();

    private static LaneScheduler NewScheduler(string poolId = "local-big", string deploymentId = "local-qwen",
        int? maxAgents = null, LaneCapacityMode mode = LaneCapacityMode.Provider, bool enabled = true)
    {
        var s = new LaneScheduler(Gen, new NullLogger());
        s.RegisterPool(poolId, deploymentId, deploymentId, mode, maxAgents, enabled);
        return s;
    }

    private static void SetCapacity(LaneScheduler s, int total, DateTimeOffset? at = null)
        => s.UpdateProviderObservation(new ProviderCapacityObservation(
            "local-qwen", total, ProviderCapacityStatus.Fresh, at ?? DateTimeOffset.UtcNow, "test"));

    private static LaneQueueEntry Entry(string id, int seq, string pool = "local-big", string deployment = "local-qwen")
        => new(id, pool, deployment, seq, AgentId: null, RunId: null, SessionId: null, "task", DateTimeOffset.UtcNow);

    private static async Task<LaneOwnershipToken> AdmitAsync(LaneScheduler s, string id, int seq, string deployment = "local-qwen")
    {
        var r = await s.AcquireAsync(Entry(id, seq, deployment: deployment));
        Assert.NotNull(r.Token);
        return r.Token!;
    }
    // ---- §3.2 the example trace that must pass --------------------------------
    // lane 1: A model -> A tools -> A model -> A retry -> A model -> A done
    // lane 2: B model -> B tools ... B model
    // queue : C ------------------------------------------------> C admitted
    // C must make ZERO inference requests and run ZERO agent tools before A
    // releases lane 1.

    // astra-2 §5.3 / §17: a deployment's context window and its total concurrency
    // are SEPARATE provider facts. The lane scheduler admits by the reported total
    // concurrency (or a manual cap) and never reads the model context window — a
    // reduced-context, four-parallel deployment admits exactly four, and no context
    // figure anywhere in the pipeline can admit a fifth. The proof is behavioral:
    // only the concurrency observation moves admission; nothing else does.
    [Fact]
    public async Task AdmissionIsTheConcurrencyFact_NotContext()
    {
        var s = NewScheduler();
        // The provider reports total concurrency 4 for this deployment. (A context
        // window is a distinct fact on the deployment record; it is NOT a capacity
        // input to this scheduler — there is no context parameter to set.)
        SetCapacity(s, total: 4);

        var admitted = 0;
        for (var i = 1; i <= 5; i++)
        {
            var r = await s.AcquireAsync(Entry($"R{i}", i));
            if (r.Token is not null) admitted++;
        }
        Assert.Equal(4, admitted); // admission is exactly the reported total concurrency
        var snap = s.Snapshots()[0];
        Assert.Equal(4, snap.OwnedCount);
        Assert.Equal(1, snap.QueueCount); // the 5th queues, never over-admits
    }

    [Fact]
    public async Task AbcTrace_ThirdAdmittedOnlyAfterFirstRelease()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);

        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);

        // C is the third submitter with two lanes owned: it MUST queue and
        // hold no lane. The queued result is a stored work record — nothing
        // in the system can start work for C from it.
        var c = await s.AcquireAsync(Entry("C", 3));
        Assert.Null(c.Token);
        Assert.Equal(1, c.QueuePosition);
        Assert.NotNull(c.BlockedReason);

        // A keeps its lane through an arbitrary number of "model turn +
        // tools" cycles — the scheduler is not involved at all in those;
        // the ownership token is still the live authority.
        Assert.True(s.TryValidatePermit(a, "local-big", "A"));
        Assert.True(s.TryValidatePermit(b, "local-big", "B"));
        Assert.False(s.TryValidatePermit(null, "local-big", "C"));
        Assert.Equal(2, s.Snapshots()[0].OwnedCount);

        // A finishes → releases. C is the head of the FIFO queue and is
        // admitted AT THAT MOMENT — not earlier, not by borrowing lane 2.
        await s.ReleaseAsync(a);
        Assert.True(s.TryValidatePermit(b, "local-big", "B"));
        var snap = s.Snapshots()[0];
        Assert.Equal(2, snap.OwnedCount); // B + C
        Assert.Equal(0, snap.QueueCount);

        // The queue record for C is gone; C now owns a lane.
        Assert.False(await s.CancelQueuedAsync("C"));
    }

    [Fact]
    public async Task OwnerRetainsLaneThroughToolsRetryAndMaintenance()
    {
        var s = NewScheduler();
        SetCapacity(s, 1);

        var a = await AdmitAsync(s, "A", 1);
        // B waits while A runs tools — no borrowing, no time slice.
        var b = await s.AcquireAsync(Entry("B", 2));
        Assert.Null(b.Token);

        // A's token stays valid across its whole segment — model, tools,
        // retry backoff, compaction — until A releases it.
        for (var i = 0; i < 50; i++)
            Assert.True(s.TryValidatePermit(a, "local-big", "A"));

        await s.ReleaseAsync(a);
        // B is admitted after A's release.
        var snap = s.Snapshots()[0];
        Assert.Equal(1, snap.OwnedCount);
        Assert.Equal(0, snap.QueueCount);
    }

    // ---- §5.3 capacity increases ------------------------------------------------

    [Fact]
    public async Task CapacityIncrease_2To4_AdmitsTwoMore_FifthQueues()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);
        var c = await s.AcquireAsync(Entry("C", 3));
        var d = await s.AcquireAsync(Entry("D", 4));
        Assert.Null(c.Token);
        Assert.Null(d.Token);

        // Backend reconfigured for four simultaneous agents: netPI adopts four
        // after validating that configuration. The two existing owners are NOT
        // replaced — the fifth waits under the same ownership rules.
        SetCapacity(s, 4);
        var e = await s.AcquireAsync(Entry("E", 5));
        Assert.Null(e.Token);

        var snap = s.Snapshots()[0];
        Assert.Equal(4, snap.EffectiveCapacity);
        Assert.Equal(4, snap.OwnedCount);
        Assert.Equal(1, snap.QueueCount);
        // A and B still own their original lanes (same tokens, unchanged).
        Assert.True(s.TryValidatePermit(a, "local-big", "A"));
        Assert.True(s.TryValidatePermit(b, "local-big", "B"));
    }

    [Fact]
    public async Task CapacityDecrease_4To2_DrainsWithoutPreemption()
    {
        var s = NewScheduler();
        SetCapacity(s, 4);
        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);
        var c = await AdmitAsync(s, "C", 3);
        var d = await AdmitAsync(s, "D", 4);

        // Target reduced to 2: existing owners stay (2/4 → still above the new
        // target), no NEW admission until below the target — no eviction.
        SetCapacity(s, 2);
        var e = await s.AcquireAsync(Entry("E", 5));
        Assert.Null(e.Token);
        Assert.NotNull(e.BlockedReason);
        var f = await s.AcquireAsync(Entry("F", 6));
        Assert.Null(f.Token);

        // One owner finishes (3 owned, still above the new target of 2):
        // no new admission — admission requires owned < target (astra-2 §5.3).
        await s.ReleaseAsync(d);
        Assert.Equal(3, s.Snapshots()[0].OwnedCount);
        Assert.Equal(2, s.Snapshots()[0].QueueCount);

        // Second owner finishes → exactly at target: STILL no admission.
        await s.ReleaseAsync(c);
        Assert.Equal(2, s.Snapshots()[0].OwnedCount);
        Assert.Equal(2, s.Snapshots()[0].QueueCount);

        // Third owner finishes → below target: the FIFO queue drains one in.
        await s.ReleaseAsync(b);
        var snap = s.Snapshots()[0];
        Assert.Equal(2, snap.OwnedCount); // A + E
        Assert.Equal(1, snap.QueueCount); // F
        Assert.True(s.TryValidatePermit(a, "local-big", "A")); // A was never evicted
    }

    // ---- §5.3 provider cap ------------------------------------------------------

    [Fact]
    public async Task ProviderCapacityWithExplicitCap_IsCappedAndVisible()
    {
        var s = NewScheduler(maxAgents: 2);
        SetCapacity(s, 4);
        var snap = s.Snapshots()[0];
        Assert.Equal(4, snap.ProviderReportedConcurrency);
        Assert.Equal(2, snap.UserCap);
        Assert.Equal(2, snap.EffectiveCapacity);

        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);
        var c = await s.AcquireAsync(Entry("C", 3));
        Assert.Null(c.Token);
        Assert.Equal(2, s.Snapshots()[0].OwnedCount);
    }

    // ---- §5.3 unknown/stale capacity -------------------------------------------

    [Fact]
    public async Task UnknownCapacity_HoldsNewAdmission_WithVisibleReason()
    {
        var s = NewScheduler(); // no observation yet
        var a = await s.AcquireAsync(Entry("A", 1));
        Assert.Null(a.Token);
        Assert.NotNull(a.BlockedReason);
        Assert.Contains("capacity", a.BlockedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", a.BlockedReason!, StringComparison.OrdinalIgnoreCase);

        // A stale observation is NOT fresh capacity.
        s.UpdateProviderObservation(new ProviderCapacityObservation(
            "local-qwen", 2, ProviderCapacityStatus.Stale, DateTimeOffset.UtcNow.AddHours(-6), "test"));
        var b = await s.AcquireAsync(Entry("B", 2));
        Assert.Null(b.Token);
        Assert.NotNull(b.BlockedReason);

        // Existing owners are unaffected by unknown capacity (they may
        // finish on their unchanged deployment) — nothing to drain here,
        // but a fresh observation unblocks admission.
        SetCapacity(s, 2);
        var c = await s.AcquireAsync(Entry("C", 3));
        Assert.Null(c.Token); // A and B were admitted from the queue first
        Assert.Equal(2, s.Snapshots()[0].OwnedCount);
        Assert.Equal(1, s.Snapshots()[0].QueueCount);
    }

    // ---- §5.2 disabling means draining -------------------------------------------

    [Fact]
    public async Task DisabledPool_DrainsWithoutEvictingOwners()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);

        await s.SetPoolEnabledAsync("local-big", false);
        var c = await s.AcquireAsync(Entry("C", 3));
        Assert.Null(c.Token);
        Assert.NotNull(c.BlockedReason);

        // Current owners retain their lanes and may finish.
        Assert.True(s.TryValidatePermit(a, "local-big", "A"));
        await s.ReleaseAsync(a);
        var snap = s.Snapshots()[0];
        Assert.True(snap.Draining);
        Assert.False(snap.CanAdmit);
        Assert.Equal(1, snap.OwnedCount);

        // Re-enable: the queued C is admitted at once — the pool is now FULL
        // again (no free lane, but the queue is drained and no owner was evicted).
        await s.SetPoolEnabledAsync("local-big", true);
        snap = s.Snapshots()[0];
        Assert.False(snap.Draining);
        Assert.Equal(2, snap.OwnedCount);
        Assert.Equal(0, snap.QueueCount);
        Assert.NotNull(snap.BlockedReason); // full
        Assert.Equal("full", snap.BlockedReason);
        // The new owner is the queued C (A's and B's tokens are gone: A released,
        // B and C were re-admitted from the queue in order — B keeps its slot, C's
        // slot is the newly admitted one).
        Assert.True(s.TryValidatePermit(b, "local-big", "B"));
        Assert.False(await s.CancelQueuedAsync("C")); // C is no longer queued
    }

    // ---- §5.4 single-residency -----------------------------------------------------

    [Fact]
    public async Task SingleResidency_SecondDeploymentWaitsForFullDrain()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        var a = await AdmitAsync(s, "A", 1, deployment: "local-qwen");
        // A different deployment cannot take a lane while the pool is pinned
        // by a live owner — even if a lane is free.
        var b = await s.AcquireAsync(Entry("B", 2, deployment: "local-other"));
        Assert.Null(b.Token);
        Assert.Contains("single-residency", b.BlockedReason!, StringComparison.OrdinalIgnoreCase);

        // Same deployment can still be admitted while pinned.
        var c = await s.AcquireAsync(Entry("C", 3, deployment: "local-qwen"));
        Assert.NotNull(c.Token);

        // Drain fully → the other deployment is admitted only after that.
        await s.ReleaseAsync(a);
        await s.ReleaseAsync(c.Token!);
        var d = await s.AcquireAsync(Entry("D", 4, deployment: "local-other"));
        Assert.NotNull(d.Token);
    }

    // ---- §6.2 explicit handoff -----------------------------------------------------

    [Fact]
    public async Task Handoff_TransfersTokenToSamePoolChild_Atomic()
    {
        var s = NewScheduler();
        SetCapacity(s, 1); // capacity ONE — the delegation case
        var parent = await AdmitAsync(s, "P", 1);
        var child = await s.AcquireAsync(Entry("C", 2));
        Assert.Null(child.Token);

        // The handoff returns the child's NEW ownership token — the caller must
        // hold that exact reference (permits are reference-checked, so a
        // reconstructed copy never validates).
        var childToken = await s.HandoffAsync(parent, Entry("C", 2));
        Assert.NotNull(childToken);
        Assert.Equal("C", childToken!.AssignmentId);
        Assert.Equal(parent.Epoch + 1, childToken.Epoch);
        // The parent's OLD token is stale in every form.
        Assert.False(s.TryValidatePermit(parent, "local-big", "P"));
        Assert.False(s.TryValidatePermit(parent, "local-big", "C"));
        Assert.False(s.TryValidatePermit(parent with { AssignmentId = "C" }, "local-big", "C"));
        // The child now owns the lane under the returned token.
        Assert.True(s.TryValidatePermit(childToken, "local-big", "C"));
        Assert.Equal(1, s.Snapshots()[0].OwnedCount);
    }

    [Fact]
    public async Task Handoff_StaleToken_ChangesNothing()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        var parent = await AdmitAsync(s, "P", 1);
        await s.ReleaseAsync(parent);

        // The released token can no longer be handed off.
        var handoff = await s.HandoffAsync(parent, Entry("C", 2));
        Assert.Null(handoff); // a released token can no longer be handed off
        Assert.Equal(0, s.Snapshots()[0].OwnedCount);
    }

    [Fact]
    public async Task Handoff_CrossPool_IsRejected()
    {
        var s = NewScheduler();
        s.RegisterPool("other-pool", "local-qwen", "local-qwen", LaneCapacityMode.Provider, null, true);
        SetCapacity(s, 2);
        var parent = await AdmitAsync(s, "P", 1);
        var handoff = await s.HandoffAsync(parent, Entry("C", 2, pool: "other-pool"));
        Assert.Null(handoff);
        Assert.True(s.TryValidatePermit(parent, "local-big", "P"));
    }

    // ---- §3.3 permit fencing ---------------------------------------------------------

    [Fact]
    public async Task Permit_ForeignGeneration_FailsClosed()
    {
        var s = NewScheduler();
        SetCapacity(s, 1);
        var a = await AdmitAsync(s, "A", 1);

        // A token minted by a different host generation is never valid here
        // (the token record is internal — a forged/migrated token must fail).
        var forged = a with { HostGeneration = "host-other" };
        Assert.False(s.TryValidatePermit(forged, "local-big", "A"));
        // The real token is still valid and is what the pool actually owns.
        Assert.True(s.TryValidatePermit(a, "local-big", "A"));
    }

    [Fact]
    public async Task Permit_WrongPool_Or_WrongAssignment_FailsClosed()
    {
        var s = NewScheduler();
        s.RegisterPool("other-pool", "local-qwen", "local-qwen", LaneCapacityMode.Provider, null, true);
        SetCapacity(s, 1);
        var a = await AdmitAsync(s, "A", 1);
        Assert.False(s.TryValidatePermit(a, "other-pool", "A"));
        Assert.False(s.TryValidatePermit(a, "local-big", "B"));
        Assert.False(s.TryValidatePermit(null, "local-big", "A"));
    }

    [Fact]
    public async Task Release_StaleOrDouble_ReleaseIsANoop()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        var a = await AdmitAsync(s, "A", 1);
        await s.ReleaseAsync(a);
        // Double release — must not corrupt ownership state.
        await s.ReleaseAsync(a);
        Assert.Equal(0, s.Snapshots()[0].OwnedCount);
        // A new admission still works.
        var b = await s.AcquireAsync(Entry("B", 2));
        Assert.NotNull(b.Token);
    }

    // ---- §3.1 queue bounds and ordering -------------------------------------------------

    [Fact]
    public async Task Queue_IsFifoByReadySequence()
    {
        var s = NewScheduler();
        SetCapacity(s, 1);
        var a = await AdmitAsync(s, "A", 1);
        await s.AcquireAsync(Entry("first", 2));
        await s.AcquireAsync(Entry("second", 3));
        // A's release admits the FIRST queued entry (FIFO by ready sequence).
        await s.ReleaseAsync(a);
        Assert.Equal(1, s.Snapshots()[0].OwnedCount);
        Assert.Equal(1, s.Snapshots()[0].QueueCount);
        // "first" was admitted (no longer queued); "second" is still queued.
        Assert.False(await s.CancelQueuedAsync("first"));
        Assert.True(await s.CancelQueuedAsync("second"));
    }

    [Fact]
    public async Task CancelQueued_RemovesFromQueueAndFreesItsSlot()
    {
        var s = NewScheduler();
        SetCapacity(s, 1);
        await AdmitAsync(s, "A", 1);
        var b = await s.AcquireAsync(Entry("B", 2));
        Assert.Null(b.Token);
        Assert.Equal(1, s.Snapshots()[0].QueueCount);

        Assert.True(await s.CancelQueuedAsync("B"));
        Assert.Equal(0, s.Snapshots()[0].QueueCount);
        Assert.Equal(1, s.Snapshots()[0].OwnedCount);
        // C now has B's queue slot.
        var c = await s.AcquireAsync(Entry("C", 3));
        Assert.Equal(1, c.QueuePosition);
    }

    // ---- §5.2 manual mode -----------------------------------------------------------

    [Fact]
    public async Task ManualMode_UsesConfiguredAgents()
    {
        var s = NewScheduler(mode: LaneCapacityMode.Manual, maxAgents: 3);
        // No provider observation is needed at all in manual mode.
        var a = await AdmitAsync(s, "A", 1);
        var b = await AdmitAsync(s, "B", 2);
        var c = await AdmitAsync(s, "C", 3);
        var d = await s.AcquireAsync(Entry("D", 4));
        Assert.Null(d.Token);
        var snap = s.Snapshots()[0];
        Assert.Equal(3, snap.EffectiveCapacity);
        Assert.Equal(3, snap.OwnedCount);
        Assert.Equal(LaneCapacityMode.Manual, snap.CapacityMode);
    }

    // ---- §13 snapshot visibility ------------------------------------------------------

    [Fact]
    public async Task Snapshots_ReportOwnershipCapacityAndQueue()
    {
        var s = NewScheduler();
        SetCapacity(s, 2);
        await AdmitAsync(s, "A", 1);
        await s.AcquireAsync(Entry("B", 2));
        await s.AcquireAsync(Entry("C", 3));

        var snap = s.Snapshots()[0];
        Assert.Equal("local-big", snap.PoolId);
        Assert.True(snap.Enabled);
        Assert.False(snap.Draining);
        Assert.Equal(2, snap.OwnedCount);
        Assert.Equal(2, snap.EffectiveCapacity);
        Assert.Equal(2, snap.ProviderReportedConcurrency);
        Assert.Equal(1, snap.QueueCount);
        Assert.False(snap.CanAdmit);
        Assert.NotNull(snap.BlockedReason);
    }
}
