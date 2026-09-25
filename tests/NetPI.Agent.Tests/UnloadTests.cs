using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace NetPI.Agent.Tests;

/// <summary>Loads the built plugins from artifacts/app/plugins into collectible load contexts (like the host), runs them, unloads them.</summary>
public static class UnloadTests
{
    public static void Register(TestRunner t)
    {
        t.Add("unload: plugins run from collectible load contexts and unload cleanly", LoadRunUnload);
    }

    private sealed class PluginAlc(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "NetPI.Abstractions") return null; // shared contracts from the default context
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetPI.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static readonly string[] PluginNames = ["NetPI.Agents", "NetPI.Context", "NetPI.AgentsMd", "NetPI.Runtime", "NetPI.Tools.Agents"];

    private static async Task LoadRunUnload()
    {
        var root = FindRepoRoot();
        var dlls = PluginNames.Select(n => root is null ? "" : Path.Combine(root, "artifacts", "app", "plugins", n, n + ".dll")).ToList();
        if (dlls.Any(d => !File.Exists(d)))
        {
            Console.WriteLine("        (skipped: build the plugins first)");
            return;
        }
        var refs = await RunAsync(dlls);
        NetPiJson.ResetCollectibleCache();
        for (var i = 0; i < 60 && refs.Any(r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        var alive = refs.Select((r, i) => (r, i)).Where(x => x.r.IsAlive).Select(x => PluginNames[x.i]).ToList();
        Check.True(alive.Count == 0, $"load contexts still alive: {string.Join(", ", alive)}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> RunAsync(List<string> dlls)
    {
        var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var alcs = new List<PluginAlc>();
        try
        {
            var ids = new List<string>();
            foreach (var dll in dlls)
            {
                var alc = new PluginAlc(dll);
                alcs.Add(alc);
                var asm = alc.LoadFromAssemblyPath(dll);
                var type = asm.GetTypes().Single(t => typeof(INetPiPlugin).IsAssignableFrom(t) && !t.IsAbstract);
                Check.True(type.Assembly.IsCollectible, "plugin loaded into a collectible context");
                ids.Add(type.GetCustomAttribute<NetPiPluginAttribute>()!.Id);
                await h.StartPluginAsync((INetPiPlugin)Activator.CreateInstance(type)!);
            }

            // exercise everything: a parent that spawns and waits for a subagent, tool details, agents, prompt sections
            var parent = h.NewSession(model: "fake/solo");
            h.Catalog.Handler = (r, ct) =>
            {
                if (r.SystemPrompt?.Contains("a subagent working for") == true) return Reply.Text("child report");
                return Reply.HasToolResult(r)
                    ? Reply.Text("parent done")
                    : Reply.Tools(Reply.Call("agent_choices"), Reply.Call("agent_spawn", new { task = "work", name = "w", wait = true }));
            };
            var runtime = h.Services.Get<IAgentRuntime>()!;
            Check.True(runtime.GetType().Assembly.IsCollectible, "runtime comes from a collectible context");
            await runtime.SendAsync(parent.Id, new UserInput { Text = "go" });
            await h.IdleAsync(parent.Id, 15_000);
            Check.Equal("parent done", h.Messages(parent.Id)[^1].Text);
            var preview = await h.Rpc.CallAsync("context.preview", new { sessionId = parent.Id });
            Check.Contains((string?)preview!["systemPrompt"], "# Environment");
            await h.Rpc.CallAsync("agents.list");
            await h.Rpc.CallAsync("usage.summary");
            await h.Rpc.CallAsync("runs.list", new { });

            ids.Reverse();
            foreach (var id in ids) await h.StopPluginAsync(id);
            await Task.Delay(300); // let throttled status/agents timers fire and finish
            await h.Bus.DrainAsync();
            Check.Equal(null, h.Services.Get<IAgentRuntime>());
            Check.Equal(0, h.Tools.All.Count);
            Check.Equal(0, h.Rpc.List().Count);
        }
        finally
        {
            await h.DisposeAsync();
        }
        var refs = alcs.Select(a => new WeakReference(a)).ToList();
        foreach (var a in alcs) a.Unload();
        return refs;
    }
}
