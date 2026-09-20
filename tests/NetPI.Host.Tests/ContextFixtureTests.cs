using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NetPI.Context.Pi;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50: Context fixture directory trees. Builds a real temp tree with a
/// workspace + parent + child .netpi/ layers, an override, and SYSTEM/
/// APPEND_SYSTEM, then asserts the discovery layering (broad→specific,
/// override replaces, project SYSTEM overrides global, APPEND global→project).
/// </summary>
public class ContextFixtureTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;

    public ContextFixtureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "netpi-ctx-" + Guid.NewGuid().ToString("N"));
        _ws = Path.Combine(_root, "proj");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void WriteLayer(string dir, string file, string content)
    {
        var netpi = Path.Combine(dir, ".netpi");
        Directory.CreateDirectory(netpi);
        File.WriteAllText(Path.Combine(netpi, file), content);
    }

    [Fact]
    public void AgentsLayers_ParentThenChild_BroadToSpecific()
    {
        // parent .netpi + child .netpi
        WriteLayer(_root, "AGENTS.md", "PARENT AGENTS");
        WriteLayer(_ws, "AGENTS.md", "CHILD AGENTS");

        var d = new ContextDiscovery();
        var layers = d.DiscoverAgentsLayers(_ws);

        var labels = layers.Select(l => l.Label).ToList();
        var contents = layers.Select(l => l.Content).ToList();
        Assert.Contains("PARENT AGENTS", contents);
        Assert.Contains("CHILD AGENTS", contents);

        // broad → specific: parent layer precedes child layer.
        var parentIdx = contents.IndexOf("PARENT AGENTS");
        var childIdx = contents.IndexOf("CHILD AGENTS");
        Assert.True(parentIdx < childIdx, "parent must come before child (broad→specific)");
    }

    [Fact]
    public void AgentsLayers_OverrideReplacesOrdinaryInThatDir()
    {
        // A directory that has BOTH AGENTS.md and AGENTS.override.md must use
        // only the override (PLAN §16).
        WriteLayer(_ws, "AGENTS.md", "ORDINARY");
        WriteLayer(_ws, "AGENTS.override.md", "OVERRIDE");

        var d = new ContextDiscovery();
        var layers = d.DiscoverAgentsLayers(_ws);

        var wsLayers = layers.Where(l => l.Content == "ORDINARY" || l.Content == "OVERRIDE").ToList();
        Assert.Equal(1, wsLayers.Count);
        Assert.Equal("OVERRIDE", wsLayers[0].Content);
        Assert.DoesNotContain(layers, l => l.Content == "ORDINARY");
    }

    [Fact]
    public void SystemLayers_ProjectSystemOverridesGlobal_AppendsLayerGlobalFirst()
    {
        WriteLayer(_ws, "SYSTEM.md", "PROJECT SYSTEM");
        WriteLayer(_ws, "APPEND_SYSTEM.md", "PROJECT APPEND");

        var d = new ContextDiscovery();
        var (system, appends) = d.DiscoverSystemLayers(_ws);

        // Project SYSTEM.md always wins for the override slot.
        Assert.Equal("PROJECT SYSTEM", system);

        // Project APPEND must be present; if a global APPEND exists on this
        // machine it must come BEFORE the project one.
        var appendContents = appends.Select(a => a.Content).ToList();
        Assert.Contains("PROJECT APPEND", appendContents);
        if (appendContents.Count > 1)
        {
            // The last append is the project-level one (global→project order).
            Assert.Equal("PROJECT APPEND", appendContents[^1]);
        }
    }

    [Fact]
    public void BuildForWorkspace_SystemOverrideBecomesBasePrompt()
    {
        WriteLayer(_ws, "SYSTEM.md", "CUSTOM BASE");
        WriteLayer(_ws, "AGENTS.md", "WS AGENTS");

        var d = new ContextDiscovery();
        var inputs = d.BuildForWorkspace(_ws);

        // §17: SYSTEM.md REPLACES the base prompt.
        Assert.Equal("CUSTOM BASE", inputs.BasePrompt);
        // AGENTS context is always kept as a context section, even under a
        // SYSTEM.md override.
        Assert.Contains(inputs.ContextFiles, s => s.Content == "WS AGENTS");
    }

    [Fact]
    public async Task BuildForWorkspace_NoSystemMd_UsesBuiltInBase()
    {
        WriteLayer(_ws, "AGENTS.md", "WS AGENTS");

        var d = new ContextDiscovery();
        var provider = new SystemPromptProvider();
        var inputs = d.BuildForWorkspace(_ws);

        // No SYSTEM.md anywhere on this machine (project level) → base is the
        // built-in prompt (or the global one if the box has one); either way it
        // is not empty and is distinct from the AGENTS section.
        Assert.False(string.IsNullOrWhiteSpace(inputs.BasePrompt));
        Assert.Contains(inputs.ContextFiles, s => s.Content == "WS AGENTS");

        // A well-formed build should render into a non-empty string.
        var rendered = await provider.BuildAsync(inputs);
        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.Contains("WS AGENTS", rendered);
    }
}
