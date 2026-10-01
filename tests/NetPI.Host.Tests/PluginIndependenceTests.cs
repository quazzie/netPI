using System.Xml.Linq;

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
                        Check.Contains(text, "NetPI.Abstractions", $"peer project reference in {file}: {include}");
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
            r.Add($"plugins independence: {name} starts and stops with no peers", async () =>
            {
                var app = Environment.GetEnvironmentVariable("NETPI_APP_DIR") ?? Path.Combine(T.RepoRoot, "artifacts", "dev", "app");
                var built = Path.Combine(app, "plugins", name);
                Check.True(File.Exists(Path.Combine(built, name + ".dll")), $"build {name} first");
                var root = T.TempDir("plugin-independent");
                T.CopyDir(built, Path.Combine(root, name));
                await using var server = await PluginTests.StartAsync(root).WaitAsync(TimeSpan.FromSeconds(10));
                var plugin = server.Plugins.List().Single();
                Check.Equal("running", plugin.State, plugin.Error);
                await server.Plugins.SetEnabledAsync(plugin.Id, false).WaitAsync(TimeSpan.FromSeconds(10));
                Check.False(server.Rpc.List().Any(m => m.PluginId == plugin.Id), "RPC registrations removed");
                Check.False(server.Kernel.Tools.Registrations.Any(t => t.PluginId == plugin.Id), "tools removed");
                Check.False(server.Kernel.Ui.Tabs.Any(t => t.PluginId == plugin.Id), "tabs removed");
            });
        }
    }
}
