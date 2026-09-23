using System.Reflection;
using System.Runtime.Loader;

namespace NetPI.Tools.Tests;

/// <summary>Loads the built plugin assemblies from artifacts/app/plugins the way the host does (collectible ALC, shared contracts).</summary>
public static class LoadTests
{
    private sealed class PluginLoadContext(string dir) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "NetPI.Abstractions") return null; // shared from the default context
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    public static void Register(TestRunner r)
    {
        foreach (var (project, expectedTools) in new[] { ("NetPI.Tools.Files", 6), ("NetPI.Tools.Shell", 5) })
        {
            r.Add($"load: {project} starts and stops in a collectible AssemblyLoadContext", async () =>
            {
                var dir = FindPluginDir(project);
                Check.False(File.Exists(Path.Combine(dir, "NetPI.Abstractions.dll")), "contracts must not be copied next to the plugin");
                var alc = new PluginLoadContext(dir);
                var asm = alc.LoadFromAssemblyPath(Path.Combine(dir, project + ".dll"));
                var types = asm.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(INetPiPlugin).IsAssignableFrom(t)).ToList();
                Check.Equal(1, types.Count, "exactly one plugin class");
                var attr = types[0].GetCustomAttribute<NetPiPluginAttribute>();
                Check.True(attr is not null && attr.Order == 20 && attr.Id.StartsWith("netpi.tools."));
                var plugin = (INetPiPlugin)Activator.CreateInstance(types[0])!;
                var ctx = new FakePluginContext(T.TempDir("load"));
                await plugin.StartAsync(ctx, CancellationToken.None);
                Check.Equal(expectedTools, ctx.ToolsFake.Tools.Count);
                Check.True(ctx.RpcFake.Handlers.Count >= 2);
                await plugin.StopAsync(CancellationToken.None);
                alc.Unload();
            });
        }
    }

    private static string FindPluginDir(string project)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NetPI.slnx"))) d = d.Parent;
        if (d is null) throw new AssertException("repository root not found");
        var dir = Path.Combine(d.FullName, "artifacts", "app", "plugins", project);
        if (!File.Exists(Path.Combine(dir, project + ".dll"))) throw new AssertException($"{project} is not built ({dir})");
        return dir;
    }
}
