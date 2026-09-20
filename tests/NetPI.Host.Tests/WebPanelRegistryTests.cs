using Xunit;
using NetPI.Abstractions;
using NetPI.Host.Services;

namespace NetPI.Host.Tests;

public sealed class WebPanelRegistryTests
{
    [Fact]
    public void ScopedPanelsDisappearOnUnload()
    {
        var global = new WebPanelRegistry();
        var scoped = new ScopedWebPanels(global);

        scoped.Register(new WebPanelDefinition(
            "git", "Git", "G", "/plugins/git/panel", 20));

        var panel = Assert.Single(global.All());
        Assert.Equal("git", panel.Id);

        scoped.Unload();

        Assert.Empty(global.All());
    }

    [Fact]
    public void NewRegistrationReplacesSamePanelId()
    {
        var global = new WebPanelRegistry();
        using var first = global.Register(new WebPanelDefinition(
            "jobs", "Jobs", "J", "/old", 10));
        using var second = global.Register(new WebPanelDefinition(
            "jobs", "Jobs", "J", "/new", 10));

        var panel = Assert.Single(global.All());
        Assert.Equal("/new", panel.EntryUrl);
    }
}
