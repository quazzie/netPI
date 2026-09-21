using NetPI.Abstractions;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P3 (Host): lease admission must be atomic with the transition to
/// Draining. Once draining has started, no NEW lease is admitted — but an
/// already-admitted lease stays valid (and counted) until its owner releases
/// it.
/// </summary>
public sealed class PluginLeaseAdmissionTests
{
    private static PluginInstance InState(PluginState state)
    {
        var inst = new PluginInstance("netPI.Test", 1);
        inst.State = state;
        return inst;
    }

    [Fact]
    public void TryBeginDrain_OnlyFromActiveOrFailed()
    {
        Assert.False(InState(PluginState.Loading).TryBeginDrain(out var p));
        Assert.Equal(PluginState.Loading, p);
        Assert.True(InState(PluginState.Active).TryBeginDrain(out p));
        Assert.Equal(PluginState.Active, p);
        Assert.True(InState(PluginState.Failed).TryBeginDrain(out p));
        Assert.Equal(PluginState.Failed, p);
        Assert.False(InState(PluginState.Draining).TryBeginDrain(out p));
    }

    [Fact]
    public void NewLeasesRejectedAfterDrainBegins()
    {
        var inst = InState(PluginState.Active);
        Assert.True(inst.TryBeginDrain(out _));

        var lease = new DummyLease();
        Assert.False(inst.TryAcquireLease(lease.Id, lease));
        Assert.Equal(0, inst.LeasesHeld);
        Assert.True(inst.TrySetState(PluginState.Draining, PluginState.Active)); // restore
    }

    [Fact]
    public void ExternalLeasesOnlyAdmittedFromLoadingOrActive()
    {
        Assert.True(InState(PluginState.Loading).TryAcquireLease(Guid.NewGuid(), new DummyLease()));
        Assert.True(InState(PluginState.Active).TryAcquireLease(Guid.NewGuid(), new DummyLease()));
        Assert.False(InState(PluginState.Draining).TryAcquireLease(Guid.NewGuid(), new DummyLease()));
        Assert.False(InState(PluginState.Unloading).TryAcquireLease(Guid.NewGuid(), new DummyLease()));
        // a Failed plugin takes no NEW external work — the retry path is reload, not acquire
        Assert.False(InState(PluginState.Failed).TryAcquireLease(Guid.NewGuid(), new DummyLease()));
    }

    [Fact]
    public void AlreadyAdmittedLeaseSurvivesUntilReleased()
    {
        var inst = InState(PluginState.Active);
        var id = Guid.NewGuid();
        Assert.True(inst.TryAcquireLease(id, new DummyLease()));
        Assert.Equal(1, inst.LeasesHeld);

        Assert.True(inst.TryBeginDrain(out _));
        Assert.Equal(1, inst.LeasesHeld); // still counted — the drain must wait for it

        inst.RemoveLease(id, out var removed);
        Assert.NotNull(removed);
        Assert.Equal(0, inst.LeasesHeld);
    }

    [Fact]
    public void SelfLease_AfterDrain_IsNotTracked()
    {
        var inst = InState(PluginState.Active);
        Assert.True(inst.TryBeginDrain(out _));
        var lease = inst.AcquireSelfLease();
        Assert.Equal(0, inst.LeasesHeld); // unadmitted — cannot block the drain
        Assert.Equal(inst, lease.Value);   // ...but the already-loaded owner is still reachable
        lease.Dispose();
        Assert.Equal(0, inst.LeasesHeld);
    }

    [Fact]
    public void SelfLease_BeforeDrain_IsTracked()
    {
        var inst = InState(PluginState.Active);
        var lease = inst.AcquireSelfLease();
        Assert.Equal(1, inst.LeasesHeld);
        Assert.True(inst.TryBeginDrain(out _));
        Assert.Equal(1, inst.LeasesHeld);
        lease.Dispose();
        Assert.Equal(0, inst.LeasesHeld);
    }

    private sealed class DummyLease : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public void Dispose() { }
    }
}
