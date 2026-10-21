using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetPI.ListenerKit;

/// <summary>
/// A plugin's own small Kestrel listener, apart from the host's API (which listens on loopback only, with a per-run token: right
/// for a UI, impossible for a phone or a monitoring system, and widening it would widen the whole API). Shared SOURCE compiled into the
/// plugins that expose one (Webhooks, Phone); the address gate (<see cref="AddressList"/>) and the wrong-token throttle
/// (<see cref="FailureThrottle"/>) live here so the security-relevant part exists once.
/// </summary>
internal sealed class PluginListener : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public string Address { get; }

    private PluginListener(WebApplication app, string address, int port) { _app = app; Address = address; Port = port; }

    /// <summary>Start listening; an address that is still held by the previous generation of the plugin (a hot reload: the host stops the old generation after starting the new one) is retried every second for 90 s.</summary>
    /// <param name="name">The plugin's name, for the application name and the log.</param>
    /// <param name="maxBody">The hard stop for a request body (the handler enforces its own, smaller limit).</param>
    public static async Task<PluginListener> StartAsync(string name, IPAddress address, int port, long maxBody, Func<HttpContext, Task> handler, ILogger log, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "NetPI." + name, EnvironmentName = Environments.Production });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
            {
                o.Listen(address, port);
                o.AddServerHeader = false;
                o.Limits.MaxRequestBodySize = maxBody;
                o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            });
            var app = builder.Build();
            app.Run(context => handler(context));
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
                var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
                var actual = bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri) ? uri.Port : port;
                return new PluginListener(app, address.ToString(), actual);
            }
            catch (Exception ex) when (attempt < 90 && (ex is IOException || ex is SocketException || ex.InnerException is SocketException) && !ct.IsCancellationRequested)
            {
                log.LogInformation("{Name}: {Address}:{Port} is busy ({Error}); retrying", name, address, port, ex.Message);
                await app.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _app.StopAsync(grace.Token).ConfigureAwait(false); }
        catch { /* shutting down */ }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>An address list in CIDR form (<c>192.168.1.0/24</c>, <c>10.0.0.5</c>, <c>::1</c>); an IPv4 address mapped into IPv6 is compared as IPv4.</summary>
internal sealed class AddressList
{
    private readonly List<(IPAddress Net, int Bits)> _entries = [];
    public List<string> Rejected { get; } = [];

    public AddressList(IEnumerable<string> entries)
    {
        foreach (var raw in entries)
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;
            var slash = text.IndexOf('/');
            var addr = slash < 0 ? text : text[..slash];
            if (!IPAddress.TryParse(addr, out var ip)) { Rejected.Add(text); continue; }
            ip = Normalize(ip);
            var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var bits = max;
            if (slash >= 0 && !(int.TryParse(text[(slash + 1)..], out bits) && bits >= 0 && bits <= max)) { Rejected.Add(text); continue; }
            _entries.Add((ip, bits));
        }
    }

    public bool Contains(IPAddress? address)
    {
        if (address is null) return false;
        var ip = Normalize(address);
        foreach (var (net, bits) in _entries)
        {
            if (net.AddressFamily != ip.AddressFamily) continue;
            if (Matches(net.GetAddressBytes(), ip.GetAddressBytes(), bits)) return true;
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool Matches(byte[] net, byte[] ip, int bits)
    {
        var full = bits / 8;
        for (var i = 0; i < full; i++) if (net[i] != ip[i]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (net[full] & mask) == (ip[full] & mask);
    }
}

/// <summary>Failed token attempts per address: after <see cref="Limit"/> in <see cref="Window"/> the address is refused for the rest of the window, so a guessing client is slowed to nothing.</summary>
internal sealed class FailureThrottle(TimeProvider time)
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (int Count, DateTimeOffset Since)> _failures = [];

    public bool Blocked(string key)
    {
        lock (_failures)
        {
            if (!_failures.TryGetValue(key, out var f)) return false;
            if (time.GetUtcNow() - f.Since > Window) { _failures.Remove(key); return false; }
            return f.Count >= Limit;
        }
    }

    public void Fail(string key)
    {
        lock (_failures)
        {
            var now = time.GetUtcNow();
            if (_failures.Count > 4096) _failures.Clear();   // a flood of distinct addresses cannot grow this without bound
            _failures[key] = _failures.TryGetValue(key, out var f) && now - f.Since <= Window ? (f.Count + 1, f.Since) : (1, now);
        }
    }

    public void Clear(string key) { lock (_failures) _failures.Remove(key); }
}

