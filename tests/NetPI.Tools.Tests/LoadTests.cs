using System.Reflection;
using System.Runtime.Loader;

namespace NetPI.Tools.Tests;

/// <summary>Loads the built plugin assemblies from artifacts/dev/app/plugins the way the host does (collectible ALC, shared contracts).</summary>
public static class LoadTests
{
    /// <summary>The contract assemblies the host preloads into the default context, so a plugin never ships a copy.</summary>
    private static readonly string[] Shared = ["NetPI.Abstractions", "NetPI.Contracts"];

    private sealed class PluginLoadContext(string dir) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (Shared.Contains(name.Name)) return null; // shared from the default context
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    public static void Register(TestRunner r)
    {
        foreach (var (project, expectedTools) in new[] { ("NetPI.Tools.Files", 7), ("NetPI.Tools.Shell", NetPI.Tools.Shell.ShellLocator.FindPwsh(null, out _) is null ? 2 : 3) })
        {
            r.Add($"load: {project} starts and stops in a collectible AssemblyLoadContext", async () =>
            {
                var dir = FindPluginDir(project);
                foreach (var contracts in Shared)
                    Check.False(File.Exists(Path.Combine(dir, contracts + ".dll")), $"{contracts} must not be copied next to the plugin");
                var alc = new PluginLoadContext(dir);
                var asm = alc.LoadFromAssemblyPath(Path.Combine(dir, project + ".dll"));
                var types = asm.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(INetPiPlugin).IsAssignableFrom(t)).ToList();
                Check.Equal(1, types.Count, "exactly one plugin class");
                var attr = types[0].GetCustomAttribute<NetPiPluginAttribute>();
                Check.True(attr is not null && attr.Order == 20 && attr.Id.StartsWith("netpi.tools."));
                var plugin = (INetPiPlugin)Activator.CreateInstance(types[0])!;
                using var ctx = new FakePluginContext(T.TempDir("load"));
                await plugin.StartAsync(ctx, CancellationToken.None);
                Check.Equal(expectedTools, ctx.ToolsFake.Tools.Count);
                Check.True(ctx.RpcFake.Handlers.Count >= 2);
                if (project == "NetPI.Tools.Files")
                    Check.True(ctx.UiFake.Tabs.Any(t => t.Id == "files" && t.Panel == UiPanel.Left), "files tab registered");
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
        // The plugins a plain build produces are in artifacts/dev/app (AppOutDir, Directory.Build.props): artifacts/app
        // is only what build.ps1 -Publish installs, and a worktree's artifacts/ is its own. NETPI_APP_DIR overrides it.
        var app = Environment.GetEnvironmentVariable("NETPI_APP_DIR") is { Length: > 0 } custom ? custom : Path.Combine(d.FullName, "artifacts", "dev", "app");
        var dir = Path.Combine(app, "plugins", project);
        if (!File.Exists(Path.Combine(dir, project + ".dll"))) throw new AssertException($"{project} is not built ({dir})");
        return dir;
    }
}
