using NetPI.Abstractions;
using NetPI.Host.Services;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>PLAN §37: command registry — register, list, find, and scoped
/// unload (a plugin's commands disappear when its generation is unloaded).</summary>
public class CommandRegistryTests
{
    [Fact]
    public void Register_All_Finds_AndDisposes()
    {
        var reg = new CommandRegistry();
        using var h = reg.Register(new CommandDefinition("/compact", "Compact context now"));

        Assert.Single(reg.All());
        Assert.Equal("/compact", reg.Find("compact")!.Name);   // no leading slash
        Assert.Equal("/compact", reg.Find("/compact")!.Name);  // with leading slash
        Assert.Same(reg.All()[0], reg.Find("compact"));
    }

    [Fact]
    public void DisposingARegistrationRemovesIt()
    {
        var reg = new CommandRegistry();
        var h = reg.Register(new CommandDefinition("/a", "A"));
        Assert.Single(reg.All());

        h.Dispose();
        Assert.Empty(reg.All());
        Assert.Null(reg.Find("/a"));
    }

    [Fact]
    public void ScopedCommands_Unload_RemovesAllOwned()
    {
        var global = new CommandRegistry();
        var hostCmd = global.Register(new CommandDefinition("/host", "Host cmd"));
        var scoped = new ScopedCommands(global);
        scoped.Register(new CommandDefinition("/p1", "Plugin 1"));
        scoped.Register(new CommandDefinition("/p2", "Plugin 2"));

        Assert.Equal(3, global.All().Count);

        // Unloading the plugin generation removes its commands but keeps host ones.
        scoped.Unload();

        var remaining = global.All().Select(c => c.Name).ToList();
        Assert.Contains("/host", remaining);
        Assert.DoesNotContain("/p1", remaining);
        Assert.DoesNotContain("/p2", remaining);
        hostCmd.Dispose();
    }
}
