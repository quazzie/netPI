using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;

namespace NetPI.Providers.Tests;

/// <summary>Loads a built plugin from artifacts/app/plugins into a collectible ALC (like the host), runs it, unloads it.</summary>
internal static class PluginLoadTest
{
    private sealed class PluginAlc(string mainAssembly) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // Both contract assemblies are in the default context (the host preloads the second one by path), so a
            // plugin resolves the same Type for them and never carries a private copy.
            if (name.Name is "NetPI.Abstractions" or "NetPI.Contracts") return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetPI.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    public sealed record Result(WeakReference Alc, string PluginId, int Providers, int Models, string? StreamedText, bool AbstractionsShared);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<Result> LoadRunUnloadAsync(string dllPath, JsonObject settings, Func<IModelProvider, ModelRequest?> request)
    {
        var alc = new PluginAlc(dllPath);
        var asm = alc.LoadFromAssemblyPath(dllPath);
        var type = asm.GetTypes().Single(t => typeof(INetPiPlugin).IsAssignableFrom(t) && !t.IsAbstract);
        var attr = type.GetCustomAttribute<NetPiPluginAttribute>();
        var shared = type.GetInterfaces().Contains(typeof(INetPiPlugin));

        var ctx = new FakePluginContext(settings, attr?.Id ?? "?");
        var plugin = (INetPiPlugin)Activator.CreateInstance(type)!;
        await plugin.StartAsync(ctx, CancellationToken.None);

        var providers = ctx.ServicesImpl.GetAll<IModelProvider>();
        var models = 0;
        string? text = null;
        foreach (var p in providers)
        {
            models += (await p.ListModelsAsync(true, CancellationToken.None)).Count;
            if (request(p) is { } r)
                await foreach (var e in p.StreamAsync(r, CancellationToken.None))
                    if (e is StreamCompleted c) text = c.Message.Text;
        }

        await plugin.StopAsync(CancellationToken.None);
        ctx.Stop();
        var result = new Result(new WeakReference(alc), attr?.Id ?? "?", providers.Count, models, text, shared);
        alc.Unload();
        return result;
    }

    public static async Task<bool> WaitCollectedAsync(WeakReference wr)
    {
        for (var i = 0; i < 40 && wr.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        return !wr.IsAlive;
    }
}
