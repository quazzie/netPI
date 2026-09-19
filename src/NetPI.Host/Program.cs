using System.Runtime;
using Microsoft.Extensions.Logging;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Logging;
using NetPI.Host.Plugins;

var runtimeDir = Environment.GetEnvironmentVariable("NETPI_HOME") ?? ConfigService.DefaultRuntimeDirectory();
var pluginDir = Environment.GetEnvironmentVariable("NETPI_PLUGINS") ?? "./plugins";
var skipCache = Environment.GetEnvironmentVariable("NETPI_SKIP_CACHE") == "1";
if (!Path.IsPathRooted(pluginDir))
    pluginDir = Path.GetFullPath(pluginDir);

var options = new PluginManagerOptions
{
    PluginDirectory = pluginDir,
    RuntimeDirectory = runtimeDir,
    SkipCacheCopy = skipCache,
    };


var logDir = Path.Combine(runtimeDir, "logs");
var logger = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    b.AddProvider(new FileLoggerProvider(logDir));
}).CreateLogger("host");

await using var runtime = new HostRuntime(options, logger, new ConfigService(runtimeDir));

logger.LogInformation("netPI host: plugins='{Plugins}' runtime='{Home}' cacheCopy={Mode}",
    options.PluginDirectory, runtimeDir, skipCache ? "off" : "on");

await runtime.StartAsync();

// ---- host-owned plugin surfaces (PLAN §36) ----------------------------------
// The Web plugin resolves these cross-ALC; the host registers them with a null
// owner so they live for the whole process.
{
    var facade = new NetPI.Host.Services.PluginManagerFacade(runtime.Plugins);
    runtime.Services.Register("plugins", facade);
    var configUpdater = new NetPI.Host.Services.HostConfigUpdater(runtime.Config);
    runtime.Services.Register("host-config", configUpdater);
    // AgentIdle reload gate: defer AgentIdle-policy reloads while a run is active.
    runtime.Plugins.AgentIdleGate = async () =>
    {
        try
        {
            var runner = runtime.Services.Resolve<NetPI.Abstractions.IAgentRunner>("runner");
            return !runner.IsRunning;
        }
        catch
        {
            return true; // agent plugin not loaded yet
        }
    };
}


Console.WriteLine();
Console.WriteLine("netPI host is running. Commands: plugins | reload <id> | reloadall | gc | exit");
var done = false;

while (true)
{
    Console.Write("netpi> ");
    Console.Out.Flush();
    var command = Console.ReadLine();
    if (command is null)
    {
        Console.WriteLine();
        break;
    }
    command = command.Trim();
    if (command.Length == 0)
        continue;

    var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    switch (parts[0].ToLowerInvariant())
    {
        case "exit":
        case "quit":
            Console.WriteLine();
            done = true;
            break;

        case "plugins":
            PrintPlugins(runtime);
            break;

        case "reload":
            if (parts.Length < 2)
            {
                Console.WriteLine("usage: reload <pluginId>");
                break;
            }
            await RunReload(runtime, parts[1]);
            break;

        case "reloadall":
            foreach (var id in runtime.Plugins.CurrentSnapshots().Select(p => p.PluginId).OrderBy(x => x, StringComparer.Ordinal))
                await RunReload(runtime, id);
            break;

        case "gc":
            GC.Collect();
            GC.WaitForPendingFinalizers();
            PrintCollected(runtime);
            break;

        default:
            Console.WriteLine($"unknown command: {parts[0]} (plugins | reload <id> | reloadall | gc | exit)");
            break;
    }
    if (done) break;
}



await runtime.ShutdownAsync();
Console.WriteLine("bye");
return;

static void PrintPlugins(HostRuntime runtime)
{
    var rows = runtime.Plugins.GetStatus();
    if (rows.Count == 0)
    {
        Console.WriteLine("(no plugins loaded)");
        return;
    }
    Console.WriteLine(
        string.Format("{0,-22} {1,6} {2,-12} {3,8} {4,13} {5,13} {6,14}",
            "plugin", "gen", "state", "leases", "registrations", "subscriptions", "alc unload"));
    foreach (var r in rows)
    {
        Console.WriteLine(
            string.Format("{0,-22} {1,6} {2,-12} {3,8} {4,13} {5,13} {6,14}",
                r.PluginId,
                r.Generation,
                r.State,
                r.ActiveLeases,
                r.RegistrationCount,
                r.SubscriptionCount,
                r.UnloadPending ? "pending" : "-"));
    }

    foreach (var (label, collected) in runtime.Plugins.UnloadedAlocs())
    {
        var status = collected ? "collected" : "UNCOLLECTED (leak)";
        Console.WriteLine($"  [alc] {label}: {status}");
    }

}

static void PrintCollected(HostRuntime runtime)
{
    foreach (var (label, collected) in runtime.Plugins.UnloadedAlocs())
    {
        var status = collected ? "collected" : "UNCOLLECTED (leak)";
        Console.WriteLine($"  [alc] {label}: {status}");
    }

}

static async Task RunReload(HostRuntime runtime, string pluginId)
{
    try
    {
        var gen = await runtime.Plugins.ReloadAsync(pluginId);
        if (gen is null)
            Console.WriteLine($"reload '{pluginId}' did not happen (plugin not loaded / busy / failed)");
        else
            Console.WriteLine($"reloaded '{pluginId}' -> gen {gen.Generation} ({gen.State})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"reload '{pluginId}' failed: {ex.Message}");
    }
    // Give the GC a chance to collect the unloaded ALC before we report.
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
