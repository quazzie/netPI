using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.BackgroundTasks;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P5 dependency item: reloading the Tools plugin REPLACES the registry
/// instance behind the "tools" service id. The BackgroundTasks plugin's four
/// contributed tools must RE-REGISTER into the new registry without a
/// restart (a restart would kill its running jobs). This drives the real
/// <see cref="BackgroundTasksPlugin"/> against a real host <see cref="ServiceRegistry"/>
/// (the replacement watcher lives there) and simulates the reload's two
/// physical steps: retire the old generation's registrations, then register
/// the new generation's registry instance under the same id.
/// </summary>
public class ToolsReloadDependencyTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    /// <summary>
    /// Wraps the host <see cref="ServiceRegistry"/> so a test <see cref="PluginInstance"/>
    /// can own registrations without a full ALC.
    /// </summary>
    private sealed class RegistryScope(ServiceRegistry host) : IServiceRegistry
    {
        private readonly PluginInstance _owner = new("netPI.BackgroundTasks", 1) { State = PluginState.Active };
        internal PluginInstance _ownerForTest => _owner;
        public IDisposable Register<T>(string id, T instance) where T : notnull
            => host.Register(id, instance, _owner);
        public IValueLease<T> Acquire<T>(string id) where T : notnull => host.Acquire<T>(id);
        public IValueLease<object> Acquire(string id, Type expectedType)
            => host.Acquire(id, expectedType);
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => host.AcquireSelfLease<T>();
        public T Resolve<T>(string id) where T : notnull => host.Resolve<T>(id);
        public IDisposable WatchServiceReplacement(string id, Action<string> onReplaced)
            => host.WatchServiceReplacement(id, onReplaced);
    }

    private sealed class FakePanels : IWebPanelRegistry
    {
        private readonly List<WebPanelDefinition> _panels = [];
        public IDisposable Register(WebPanelDefinition panel)
        {
            _panels.Add(panel);
            return new Noop();
        }
        public IReadOnlyList<WebPanelDefinition> All() => _panels;
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeContext(
        RegistryScope services, JsonElement ownConfig) : IPluginContext
    {
        public PluginInfo Info { get; } = new("netPI.BackgroundTasks", "Background Tasks Test", "0.2.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels { get; } = new FakePanels();
        public IEventBus Events => throw new NotSupportedException();
        public JsonElement OwnConfig => ownConfig;
        public IPluginLogger Log { get; } = new NullLogger();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    /// <summary>Resolves to powershell -Command so the manager can spawn a real job.</summary>
    private sealed class FakeResolver : IShellCommandResolver
    {
        public string ShellId { get; } = "powershell";
        public ValueTask<ResolvedCommand> ResolveAsync(string command, string workingDirectory, CancellationToken ct)
            => ValueTask.FromResult(new ResolvedCommand(
                "powershell", ["-NoProfile", "-NonInteractive", "-Command", command], workingDirectory, null));
    }

    /// <summary>A stand-in for Tools' ToolRegistryImpl — one per "generation".</summary>
    private sealed class FakeToolRegistry : IToolRegistry
    {
        private readonly List<IAgentTool> _tools = [];
        public IDisposable Register(IAgentTool tool)
        {
            _tools.Add(tool);
            var t = tool;
            return new Handle(_tools, t);
        }
        public IReadOnlyList<IAgentTool> All() => _tools.ToList();
        public IAgentTool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);
        private sealed class Handle(List<IAgentTool> list, IAgentTool tool) : IDisposable
        {
            public void Dispose() => list.Remove(tool);
        }
    }

    private static bool WaitFor(Func<bool> probe, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (probe()) return true;
            Task.Delay(20).Wait();
        }
        return probe();
    }

    [Fact]
    public async Task BackgroundToolsSurviveAToolsReloadWithoutRestart()
    {
        var hostRegistry = new ServiceRegistry();
        var reg = new RegistryScope(hostRegistry);
        var cfg = JsonDocument.Parse("{}").RootElement.Clone();
        var ctx = new FakeContext(reg, cfg);
        var tempRoot = Path.Combine(Path.GetTempPath(), "netpi-toolsreload-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        // Tools normally owns the shell resolvers; register one directly here.
        hostRegistry.Register("resolver:powershell", new FakeResolver(), reg._ownerForTest);

        // A "tools" owner generation (the Tools plugin) registers its registry.
        var toolsOwner = new PluginInstance("netPI.Tools", 1) { State = PluginState.Active };
        var registryV1 = new FakeToolRegistry();
        var toolsHandle = hostRegistry.Register("tools", registryV1, toolsOwner);

        var plugin = new BackgroundTasksPlugin();
        await plugin.LoadAsync(ctx, CancellationToken.None);
        await plugin.StartAsync(CancellationToken.None);
        var job = await (reg.Resolve<IBackgroundJobManager>("background")).StartAsync(
            "powershell", "Start-Sleep -Seconds 60", tempRoot, null);
        try
        {
            // Sanity: all four background tools live in v1.
            Assert.All(["background_start", "background_output", "background_list", "background_kill"],
                name => Assert.NotNull(registryV1.Find(name)));

            // --- simulate a Tools reload ---
            // Step 1: retire the old generation (host RemoveAllFor — physical removal).
            hostRegistry.RemoveAllFor(toolsOwner);
            toolsHandle.Dispose();
            // Step 2: the new Tools generation registers its OWN registry instance
            // under the same id — this is the replacement the watcher fires on.
            var toolsOwnerV2 = new PluginInstance("netPI.Tools", 2) { State = PluginState.Active };
            var registryV2 = new FakeToolRegistry();
            hostRegistry.Register("tools", registryV2, toolsOwnerV2);

            // The replacement callback runs detached; poll for re-registration.
            var names = new[] { "background_start", "background_output", "background_list", "background_kill" };
            var reRegistered = WaitFor(() =>
                names.All(name => registryV2.Find(name) is not null), 5000);

            Assert.True(reRegistered,
                $"background tools did not re-register into the new registry. " +
                $"v2 contains: {string.Join(", ", registryV2.All().Select(t => t.Name))}");

            // The OLD registry is UNTOUCHED by the re-registration: its entries
            // remain (leaked with the old generation) and the stale handles were
            // retired without disposal — disposing them would reach into an
            // unloading ALC and deadlock against the drain.
            Assert.Equal(4, registryV1.All().Count);
            Assert.All(names, name => Assert.NotNull(registryV1.Find(name)));

            // And the plugin kept RUNNING: no restart — the job that was
            // started BEFORE the tools reload is still running (a restart
            // would have killed it via StopAsync -> StopAll).
            var mgr = hostRegistry.Resolve<IBackgroundJobManager>("background");
            var after = await mgr.ListAsync(CancellationToken.None);
            var info = Assert.Single(after, j => j.JobId == job.JobId);
            Assert.Equal(BackgroundJobState.Running, info.State);
        }
        finally
        {
            if (job is not null) await (reg.Resolve<IBackgroundJobManager>("background")).KillAsync(job.JobId);
            await plugin.StopAsync(CancellationToken.None);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
