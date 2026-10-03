using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using NetPI;

namespace NetPI.Host.Tests;

public static class PluginIndependenceTests
{
    public static void Register(TestRunner r)
    {
        var source = Path.Combine(T.RepoRoot, "plugins");
        var projects = Directory.GetFiles(source, "*.csproj", SearchOption.AllDirectories).Order().ToArray();
        r.Add("plugins independence: project and imported build files reference only shared contracts", () =>
        {
            Check.True(projects.Length >= 30, "all plugins are covered");
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".csproj") || f.EndsWith(".props") || f.EndsWith(".targets"))
                .Concat(new[] { Path.Combine(T.RepoRoot, "Directory.Build.props"), Path.Combine(T.RepoRoot, "Directory.Build.targets") }.Where(File.Exists)))
            {
                var doc = XDocument.Load(file);
                foreach (var node in doc.Descendants())
                {
                    var include = (string?)node.Attribute("Include") ?? "";
                    var text = include.Replace('\\', '/');
                    if (node.Name.LocalName == "ProjectReference")
                        Check.True(text.Contains("NetPI.Abstractions") || text.Contains("NetPI.Contracts"), $"peer project reference in {file}: {include}");
                    if (node.Name.LocalName is "Reference" or "HintPath" or "Compile" or "Import")
                    {
                        var value = text + " " + (string?)node.Attribute("Project") + " " + (node.Name.LocalName == "HintPath" ? node.Value : "");
                        foreach (var peer in projects.Select(Path.GetFileNameWithoutExtension))
                            Check.False(value.Contains(peer!, StringComparison.OrdinalIgnoreCase), $"peer assembly/source/import reference in {file}: {value}");
                    }
                }
            }
        });
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            r.Add($"plugins independence: {name} starts and stops with no peers, [NetPiPlugin] matches, context collects", async () =>
            {
                var app = Environment.GetEnvironmentVariable("NETPI_APP_DIR") ?? Path.Combine(T.RepoRoot, "artifacts", "dev", "app");
                var built = Path.Combine(app, "plugins", name);
                Check.True(File.Exists(Path.Combine(built, name + ".dll")), $"build {name} first");
                var root = T.TempDir("plugin-independent");
                T.CopyDir(built, Path.Combine(root, name));
                await using var server = await PluginTests.StartAsync(root).WaitAsync(TimeSpan.FromSeconds(10));
                var plugin = server.Plugins.List().Single();
                Check.Equal("running", plugin.State, plugin.Error);

                // The host read the plugin's identity from its [NetPiPlugin] attribute; the registered values must
                // be exactly what the attribute says. (Order: no plugin.json sets one, so the attribute's stands.)
                var attr = PluginAttribute(server, plugin.Id, name);
                Check.Equal(attr.Id, plugin.Id, $"{name}: the registered id differs from the attribute");
                if (attr.Name is { } n) Check.Equal(n, plugin.Name, $"{name}: the registered name differs from the attribute");
                Check.Equal(attr.Order, plugin.Order, $"{name}: the registered order differs from the attribute");

                await server.Plugins.SetEnabledAsync(plugin.Id, false).WaitAsync(TimeSpan.FromSeconds(10));
                Check.False(server.Rpc.List().Any(m => m.PluginId == plugin.Id), "RPC registrations removed");
                Check.False(server.Kernel.Tools.Registrations.Any(t => t.PluginId == plugin.Id), "tools removed");
                Check.False(server.Kernel.Ui.Tabs.Any(t => t.PluginId == plugin.Id), "tabs removed");

                // Collectible unload, for every plugin: the host's own weak-reference check must report the
                // stopped plugin's load context collected (something still referencing plugin types would leak it).
                await Wait.Until(() => server.Kernel.Plugins.GetLoadState(plugin.Id).LastUnloadCollected == true,
                    $"{name}: the load context was collected on stop", 30_000);
            });
        }
    }

    /// <summary>The plugin class's [NetPiPlugin] attribute, read from the assembly the host loaded into the
    /// plugin's own (collectible) load context.</summary>
    private static NetPiPluginAttribute PluginAttribute(NetPiServer server, string pluginId, string assemblyName)
    {
        var alc = server.Kernel.Plugins.GetLoadState(pluginId).Current?.Target as AssemblyLoadContext
            ?? throw new AssertException($"{assemblyName}: no load context to read the attribute from");
        var asm = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new AssertException($"{assemblyName}: the plugin assembly is not in its load context");
        var type = asm.GetTypes().SingleOrDefault(t => t.IsPublic && !t.IsAbstract && typeof(INetPiPlugin).IsAssignableFrom(t))
            ?? throw new AssertException($"{assemblyName}: no plugin class found");
        return type.GetCustomAttribute<NetPiPluginAttribute>() ?? throw new AssertException($"{assemblyName}: missing [NetPiPlugin]");
    }
}
