using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetPI.Host.Web;

namespace NetPI.Host;

/// <summary>Options for <see cref="NetPiServer.StartAsync"/>.</summary>
public sealed class NetPiServerOptions
{
    /// <summary>Port on 127.0.0.1. Null = setting <c>server.port</c> (default 7431); 0 = a random free port. A busy port falls back to a random one.</summary>
    public int? Port { get; set; }
    /// <summary>User data directory. Null = env <c>NETPI_HOME</c> or <c>~/.netpi</c>.</summary>
    public string? Home { get; set; }
    /// <summary>Running inside the desktop shell (reported by <c>app.info</c>).</summary>
    public bool Desktop { get; set; }
    /// <summary>Auth token. Null = random per run.</summary>
    public string? Token { get; set; }
    /// <summary>Additional plugin directories (after AppDir/plugins, Home/plugins and setting <c>plugins.dirs</c>).</summary>
    public IReadOnlyList<string>? ExtraPluginDirs { get; set; }
    /// <summary>Web UI root. Null = AppDir/wwwroot.</summary>
    public string? WebRoot { get; set; }
    /// <summary>Also write log lines to the console.</summary>
    public bool ConsoleLogging { get; set; } = true;
}

/// <summary>
/// A running NetPI host: kernel (settings, SQLite, event bus, registries, sessions, model catalog, plugins) plus the
/// local web/WebSocket server. Used in-process by netpi-server and the desktop shell.
/// </summary>
public sealed class NetPiServer : IAsyncDisposable
{
    private readonly HostKernel _kernel;
    private readonly WebServer _web;
    private readonly Lock _stopLock = new();
    private Task? _stopTask;

    private NetPiServer(HostKernel kernel, WebServer web)
    {
        _kernel = kernel;
        _web = web;
    }

    public static async Task<NetPiServer> StartAsync(NetPiServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        var kernel = await Task.Run(() => HostKernel.Create(options), ct).ConfigureAwait(false);
        WebServer? web = null;
        try
        {
            web = await WebServer.StartAsync(kernel, ct).ConfigureAwait(false);
            await kernel.Plugins.StartAsync(ct).ConfigureAwait(false);
            kernel.Models.Invalidate();
            kernel.Log.LogInformation("NetPI {Version} ready at {Url} in {Ms} ms (home: {Home})", HostInfo.Version, web.BaseUrl, sw.ElapsedMilliseconds, kernel.Paths.Home);
            return new NetPiServer(kernel, web);
        }
        catch
        {
            if (web is not null) await web.DisposeAsync().ConfigureAwait(false);
            await kernel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary><c>http://127.0.0.1:PORT</c></summary>
    public string BaseUrl => _web.BaseUrl;
    public int Port => _web.Port;
    public string Token => _kernel.Token;
    /// <summary>Open this in a browser/WebView: sets the auth cookie and redirects to the app.</summary>
    public string LaunchUrl => BaseUrl + "/?token=" + Uri.EscapeDataString(Token);
    public NetPiPaths Paths => _kernel.Paths;

    public IEventBus Events => _kernel.Bus;
    public IServiceRegistry Services => _kernel.Services;
    public IRpcRegistry Rpc => _kernel.Rpc;
    public ISettings Settings => _kernel.Settings;
    public ISessionStore Sessions => _kernel.Sessions;
    public IModelCatalog Models => _kernel.Models;
    public IPluginManager Plugins => _kernel.Plugins;

    internal HostKernel Kernel => _kernel;

    /// <summary>Graceful stop: web server first, then plugins (reverse order), bus, database and logs. Idempotent.</summary>
    public Task StopAsync()
    {
        lock (_stopLock) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _kernel.Log.LogInformation("NetPI stopping");
        try { await _web.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _kernel.Log.LogWarning(ex, "Stopping the web server failed"); }
        await _kernel.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
