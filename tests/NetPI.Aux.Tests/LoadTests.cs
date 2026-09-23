using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace NetPI.Aux.Tests;

/// <summary>
/// Loads each built plugin from artifacts/app/plugins the way the host does (collectible ALC, shared contracts),
/// starts it, disposes its registrations, and checks that the load context can actually be collected.
/// </summary>
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

    private static readonly (string Project, string Id, int Order)[] Plugins =
    [
        ("NetPI.Retry", "netpi.retry", 15),
        ("NetPI.Nudge", "netpi.nudge", 70),
        ("NetPI.ToolRepair", "netpi.toolrepair", 70),
        ("NetPI.Compaction", "netpi.compaction", 70),
        ("NetPI.Ideas", "netpi.ideas", 80),
        ("NetPI.Work", "netpi.work", 80),
        ("NetPI.Diagnostics", "netpi.diagnostics", 90),
    ];

    public static void Register(TestRunner r)
    {
        foreach (var (project, id, order) in Plugins)
        {
            r.Add($"load: {project} starts, stops and unloads from a collectible context", async () =>
            {
                var dir = FindPluginDir(project);
                Check.False(File.Exists(Path.Combine(dir, "NetPI.Abstractions.dll")), "contracts must not be copied next to the plugin");
                Check.True(File.Exists(Path.Combine(dir, "plugin.json")), "plugin.json copied");
                var (weak, ctx) = await StartAndUnloadAsync(dir, project, id, order);
                for (var i = 0; i < 20 && weak.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(20);
                }
                Check.False(weak.IsAlive, "the plugin's AssemblyLoadContext was not collected (something still references plugin types)");
                GC.KeepAlive(ctx);
            });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Weak, FakePluginContext Ctx)> StartAndUnloadAsync(string dir, string project, string id, int order)
    {
        var alc = new PluginLoadContext(dir);
        var asm = alc.LoadFromAssemblyPath(Path.Combine(dir, project + ".dll"));
        var types = asm.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(INetPiPlugin).IsAssignableFrom(t)).ToList();
        Check.Equal(1, types.Count, "exactly one plugin class");
        var attr = types[0].GetCustomAttribute<NetPiPluginAttribute>();
        Check.True(attr is not null);
        Check.Equal(id, attr!.Id);
        Check.Equal(order, attr.Order);

        var ctx = new FakePluginContext(pluginId: id);
        var plugin = (INetPiPlugin)Activator.CreateInstance(types[0])!;
        await plugin.StartAsync(ctx, CancellationToken.None);
        var registered = ctx.ServicesFake.GetAll<IAgentHook>().Count + ctx.ServicesFake.GetAll<IModelMiddleware>().Count
                         + ctx.RpcFake.Handlers.Count + ctx.ToolsFake.Tools.Count + ctx.UiFake.TabList.Count;
        Check.True(registered > 0, "registers something");
        if (id == "netpi.work")
        {
            // Exercise a code path that serializes through NetPiJson with plugin-owned delegates.
            await ctx.RpcFake.Call("work.snapshot");
        }
        await plugin.StopAsync(CancellationToken.None);
        ctx.Unload();
        Check.Equal(0, ctx.ServicesFake.GetAll<IAgentHook>().Count + ctx.ServicesFake.GetAll<IModelMiddleware>().Count
                       + ctx.RpcFake.Handlers.Count + ctx.ToolsFake.Tools.Count + ctx.UiFake.TabList.Count + ctx.UiFake.CommandList.Count);
        alc.Unload();
        return (new WeakReference(alc), ctx);
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
