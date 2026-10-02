using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NetPI.Host;

namespace NetPI.Server;

internal static class Program
{
    private const string Usage = """
        netpi-server - headless NetPI host

        Usage: netpi-server [options]
          --port N        Port on 127.0.0.1 (0 = random; default: settings server.port or 7431)
          --home DIR      Data directory (default: NETPI_HOME or ~/.netpi)
          --plugins DIR   Extra plugin directory (repeatable)
          --webroot DIR   Web UI directory (default: <app>/wwwroot)
          --token TOKEN   Fixed auth token (default: the NETPI_TOKEN environment variable, else random per run)
          --open          Open the launch URL in the default browser
          --quiet         No log output on the console
          --ephemeral     Keep everything in memory: nothing is stored in the home's database and nothing survives the run
          -h, --help      Show this help
        """;

    public static async Task<int> Main(string[] args)
    {
        var options = new NetPiServerOptions();
        var plugins = new List<string>();
        var open = false;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--port": options.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--home": options.Home = Next(); break;
                    case "--plugins": plugins.Add(Next()); break;
                    case "--webroot": options.WebRoot = Next(); break;
                    case "--token": options.Token = Next(); break;
                    case "--open": open = true; break;
                    case "--quiet": options.ConsoleLogging = false; break;
                    case "--ephemeral": options.Ephemeral = true; break;
                    case "-h" or "--help" or "/?":
                        Console.WriteLine(Usage);
                        return 0;
                    default: throw new ArgumentException($"Unknown option {args[i]}");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        options.ExtraPluginDirs = plugins;
        // The environment keeps the token out of argv (visible to every local user on a shared box through /proc).
        options.Token ??= Environment.GetEnvironmentVariable("NETPI_TOKEN");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // graceful shutdown instead of process kill
            stop.Cancel();
        };
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Cancel();
        });

        NetPiServer server;
        try
        {
            server = await NetPiServer.StartAsync(options, stop.Token);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NetPI failed to start: {ex.Message}");
            return 1;
        }

        await using (server)
        {
            Console.WriteLine();
            Console.WriteLine($"  NetPI is running at {server.BaseUrl}");
            Console.WriteLine($"  Open: {server.LaunchUrl}");
            Console.WriteLine($"  Home: {server.Paths.Home}");
            Console.WriteLine("  Press Ctrl+C to stop.");
            Console.WriteLine();
            if (open) OpenBrowser(server.LaunchUrl);
            try { await Task.Delay(Timeout.Infinite, stop.Token); }
            catch (OperationCanceledException) { }
            Console.WriteLine("Stopping...");
        }
        return 0;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open a browser ({ex.Message}); open the URL above manually.");
        }
    }
}
