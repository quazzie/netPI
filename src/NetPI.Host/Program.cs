using System.Runtime;
using Microsoft.Extensions.Logging;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Logging;
using NetPI.Host.Plugins;

var runtimeDir = Environment.GetEnvironmentVariable("NETPI_HOME") ?? ConfigService.DefaultRuntimeDirectory();
// The launcher (desktop shell / tools/keep-alive-host.ps1) sets the project root
// explicitly — a host executing from an immutable runtime cache cannot discover
// the repository by walking up the directory tree.
var projectRoot = Environment.GetEnvironmentVariable("NETPI_PROJECT_ROOT");
var pluginDir = Environment.GetEnvironmentVariable("NETPI_PLUGINS") ?? (projectRoot is { Length: > 0 } ? Path.Combine(projectRoot, "plugins") : "./plugins");
if (!Path.IsPathRooted(pluginDir))
    pluginDir = Path.GetFullPath(pluginDir);
if (projectRoot is { Length: > 0 })
    Directory.SetCurrentDirectory(projectRoot);
var options = new PluginManagerOptions
{
    PluginDirectory = pluginDir,
    RuntimeDirectory = runtimeDir,
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

logger.LogInformation("netPI host: plugins='{Plugins}' runtime='{Home}'",
    options.PluginDirectory, runtimeDir);

// ---- host-owned plugin surfaces (PLAN §36) ----------------------------------
// Registered BEFORE StartAsync because the Web plugin resolves these in its
// LoadAsync (it builds its WebApp + Kestrel there), which runs during startup.
{
    var facade = new NetPI.Host.Services.PluginManagerFacade(runtime.Plugins);
    runtime.Services.Register("plugins", facade);
    var configUpdater = new NetPI.Host.Services.HostConfigUpdater(runtime.Config);
    runtime.Services.Register("host-config", configUpdater);
    // AgentIdle reload gate: defer AgentIdle-policy reloads while a run is active.
    runtime.Plugins.AgentIdleGate = async (CancellationToken _) =>
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

await runtime.StartAsync();

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
        {
            // astra-1 P2: one queue op, one structured outcome per plugin.
            foreach (var o in await runtime.Plugins.ReloadAllOpAsync())
                PrintOutcome(o);
            break;
        }

        case "scan":
        {
            // PLAN §50: rescan the plugin directory for new folders and load
            // them — no host restart required.
            var loaded = await runtime.Plugins.ScanAsync();
            Console.WriteLine(loaded.Count == 0
                ? "scan: nothing new on disk"
                : $"scan: loaded and started {string.Join(", ", loaded)}");
            break;
        }

        case "gc":
            GC.Collect();
            GC.WaitForPendingFinalizers();
            PrintCollected(runtime);
            break;

        default:
            Console.WriteLine($"unknown command: {parts[0]} (plugins | reload <id> | reloadall | scan | gc | exit)");
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

static void PrintOutcome(NetPI.Abstractions.PluginOperationOutcome o)
{
    Console.WriteLine($"reload '{o.PluginId}': {o.Outcome} (phase {o.Phase})" +
        (string.IsNullOrEmpty(o.Error) ? "" : $" — {o.Error}") +
        (o.RestartRequired ? " — restart required" : ""));
}

static async Task RunReload(HostRuntime runtime, string pluginId)
{
    // astra-1 P2: the structured outcome IS the report (no generic "failed").
    var o = await runtime.Plugins.ReloadPluginAsync(pluginId);
    PrintOutcome(o);
    var gen = runtime.Plugins.Get(pluginId);
    if (gen is not null)
        Console.WriteLine($"  now: gen {gen.Generation} ({gen.State}) build={o.ActiveBuildId ?? gen.BuildId}");
    // Give the GC a chance to collect the unloaded ALC before we report.
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
