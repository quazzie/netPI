using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Phone;

/// <summary>
/// The phone companion: a mobile page on the plugin's own small listener (<see cref="PluginListener"/>), apart from NetPI's API, which stays
/// on loopback with its per-run token. A phone pairs once with a code the desktop shows and then lists chats, reads them live, starts new
/// ones, sends / queues / steers / stops, and answers questions and approvals — through an allow-list of the host's own RPCs
/// (<see cref="PhoneApi"/>). Off until <c>phone.enabled</c>; how the phone reaches the listener (LAN, a VPN, a port forward, a reverse proxy)
/// is the user's own network, not something NetPI knows about.
/// </summary>
[NetPiPlugin("netpi.phone", Name = "Phone", Description = "A mobile page on its own listener: chats, new chats, send/steer/stop, questions and approvals from a paired phone", Order = 98)]
public sealed class PhonePlugin : INetPiPlugin
{
    private readonly TimeProvider _time;
    private readonly bool _listen;
    private IPluginContext? _ctx;
    private PluginListener? _listener;
    private string? _listenerKey;
    private string? _listenerError;
    private AddressList _allow = new([]);
    private bool _trustProxy;
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly List<Task> _reconciles = [];
    private CancellationTokenSource? _life;

    internal PhoneAuth? Auth { get; private set; }
    internal PhoneApi? Api { get; private set; }

    public PhonePlugin() : this(TimeProvider.System, listen: true) { }

    /// <summary>A test's clock, and no network listener unless the test enables the setting.</summary>
    internal PhonePlugin(TimeProvider time, bool listen) { _time = time; _listen = listen; }

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        ctx.Services.Register(new SettingsSection
        {
            Id = "phone", Title = "Phone", Group = "Agents", Order = 62,
            Help = "A mobile page for your own phone: chats, new chats, steering and approvals. It is its own small listener, separate from NetPI's API, and off until you switch it on. Pair a phone from the Phone tab. Anyone who can reach the port and holds a paired device's token can run what the agent can run: keep it on your LAN, a VPN, or behind a reverse proxy you trust.",
            Settings =
            [
                SettingInfo.Bool("phone.enabled", "Serve the phone page", false, "Off: nothing listens."),
                SettingInfo.Str("phone.bind", "Listen on", "127.0.0.1", "127.0.0.1 (this PC only, for a reverse proxy on this PC), this PC's LAN address, or 0.0.0.0 (every interface). Anything but 127.0.0.1 needs a firewall rule for the port.", "127.0.0.1"),
                SettingInfo.Int("phone.port", "Port", 7433, "The listener's port (not NetPI's own).", 1, 65535),
                SettingInfo.List("phone.allowFrom", "Accept connections from", ["127.0.0.1/32", "::1/128"], "Addresses or CIDR ranges that may connect, e.g. 192.168.1.0/24 for the home network, or the reverse proxy's address. Everything else is refused before anything is looked at.", "192.168.1.0/24"),
                SettingInfo.Bool("phone.trustProxy", "Behind a reverse proxy", false, "Read the phone's address from X-Forwarded-For, its scheme from X-Forwarded-Proto and its host from X-Forwarded-Host. Only requests from an address in the list above are believed. Leave off when phones connect directly."),
                SettingInfo.Str("phone.publicUrl", "Address the phone opens", "", "Shown in the pairing link, e.g. https://phone.example.com. Empty: the listener's own address.", "https://phone.example.com"),
            ],
        });
        var auth = Auth = new PhoneAuth(ctx.Data, _time);
        var throttle = new FailureThrottle(_time);
        Api = new PhoneApi(ctx, auth, throttle, () => _allow, () => _trustProxy);
        ctx.Track(Api);
        Register(ctx, auth);
        ctx.Ui.AddTab(new UiTabInfo
        {
            Id = "phone", Title = "Phone", Panel = UiPanel.Left, Order = 27, Module = "ui.js",
            Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'><rect x='7' y='2.5' width='10' height='19' rx='2'/><path d='M11 18.5h2'/></svg>",
        });
        var life = _life = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        var lifeToken = life.Token;
        ctx.Events.Subscribe("settings.changed", e => Reconcile(lifeToken));
        ReadSettings();
        Reconcile(lifeToken);   // in the background: on a hot reload the previous generation still holds the port until this start returns
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _life?.Cancel();
        Task[] pending;
        lock (_reconciles) { pending = _reconciles.ToArray(); _reconciles.Clear(); }
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { _ctx?.Logger.LogWarning("Phone: a listener reconcile was still running when the plugin stopped"); }
        await _reconcile.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await CloseListenerAsync().ConfigureAwait(false); }
        finally { _reconcile.Release(); }
        _life?.Dispose();
    }

    // ---------------------------------------------------------------- the listener

    private void Reconcile(CancellationToken ct)
    {
        var task = Task.Run(async () =>
        {
            try { await ReconcileAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx?.Logger.LogWarning(ex, "Phone: the listener did not come up"); }
        });
        lock (_reconciles) { _reconciles.RemoveAll(t => t.IsCompleted); _reconciles.Add(task); }
    }

    private void ReadSettings()
    {
        var ctx = _ctx!;
        var allow = ctx.Settings.GetNode("phone.allowFrom") is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToList() : ["127.0.0.1/32", "::1/128"];
        _allow = new AddressList(allow);
        _trustProxy = ctx.Settings.Get("phone.trustProxy", false);
        if (_allow.Rejected.Count > 0) ctx.Logger.LogWarning("Phone: ignoring unreadable allow-list entries {Entries}", string.Join(", ", _allow.Rejected));
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        if (_ctx is not { } ctx) return;
        await _reconcile.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReadSettings();
            var enabled = _listen && ctx.Settings.Get("phone.enabled", false);
            var bind = ctx.Settings.Get("phone.bind", "127.0.0.1")?.Trim() ?? "127.0.0.1";
            var port = Math.Clamp(ctx.Settings.Get("phone.port", 7433), 1, 65535);
            var key = enabled ? $"{bind}:{port}" : null;
            if (key == _listenerKey && (key is null || _listener is not null)) return;
            await CloseListenerAsync().ConfigureAwait(false);
            _listenerError = null;
            if (key is null) return;
            if (!IPAddress.TryParse(bind, out var address)) { _listenerError = $"“{bind}” is not an IP address"; ctx.Logger.LogWarning("Phone: {Error}", _listenerError); return; }
            try
            {
                _listener = await PluginListener.StartAsync("Phone", address, port, PhoneApi.MaxBody, Api!.HandleAsync, ctx.Logger, ct).ConfigureAwait(false);
                _listenerKey = key;
                ctx.Logger.LogInformation("Phone: serving http://{Address}:{Port}/", address, _listener.Port);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _listenerError = ex.Message;
                ctx.Logger.LogWarning(ex, "Phone: could not listen on {Address}:{Port}", address, port);
            }
        }
        finally
        {
            _reconcile.Release();
            try { ctx.Events.Publish("phone.changed", new { }); } catch (ObjectDisposedException) { /* unloading */ }
        }
    }

    private async Task CloseListenerAsync()
    {
        var l = _listener;
        if (l is null) { _listenerKey = null; return; }
        try { await l.DisposeAsync().ConfigureAwait(false); }
        finally { _listener = null; _listenerKey = null; }
    }

    // ---------------------------------------------------------------- RPC

    internal JsonObject Status()
    {
        var ctx = _ctx!;
        var urls = new JsonArray();
        if (_listener is { } l)
        {
            var pub = ctx.Settings.Get("phone.publicUrl", "")?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(pub)) urls.Add(pub);
            else foreach (var host in HostsFor(l.Address)) urls.Add($"http://{host}:{l.Port}");
        }
        return new JsonObject
        {
            ["enabled"] = ctx.Settings.Get("phone.enabled", false),
            ["listening"] = _listener is not null,
            ["address"] = _listener?.Address,
            ["port"] = _listener?.Port,
            ["error"] = _listenerError,
            ["urls"] = urls,
            ["connections"] = Api?.Connections ?? 0,
        };
    }

    /// <summary>The host names a phone on the same network could use for a listener bound to <paramref name="bind"/>: the address itself, or every private IPv4 of this PC when it listens on all of them.</summary>
    private static IEnumerable<string> HostsFor(string bind)
    {
        if (bind is not ("0.0.0.0" or "::")) return [bind.Contains(':') ? $"[{bind}]" : bind];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString()).Distinct().ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    private void Register(IPluginContext ctx, PhoneAuth auth)
    {
        ctx.Rpc.Register(new RpcMethod("phone.status", "The phone listener: { enabled, listening, address?, port?, urls[] (what a phone opens), error?, connections }", ReadOnly: true, []),
            (_, _) => Task.FromResult<object?>(Status()));

        ctx.Rpc.Register(new RpcMethod("phone.devices", "The paired phones, newest first: { id, name, createdAt, lastSeenAt, lastFrom?, userAgent? }[]", ReadOnly: true, []),
            (_, _) => Task.FromResult<object?>(new JsonArray([.. auth.List().Select(d => (JsonNode)d)])));

        ctx.Rpc.Register(new RpcMethod("phone.pair", "A one-time pairing code, good for five minutes: → { code, expiresAt, urls[], links[] }. A phone opens `links[i]` (the page's address + `#pair=<code>`) or types the code in. The code buys a device token; it is not itself a credential after use",
            ReadOnly: false, []), (_, _) =>
        {
            var (code, expires) = auth.NewCode();
            var urls = Status()["urls"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            return Task.FromResult<object?>(new JsonObject
            {
                ["code"] = code,
                ["expiresAt"] = expires.ToUniversalTime().ToString("O"),
                ["urls"] = new JsonArray([.. urls.Select(u => (JsonNode)u)]),
                ["links"] = new JsonArray([.. urls.Select(u => (JsonNode)$"{u}/#pair={code}")]),
            });
        });

        ctx.Rpc.Register(new RpcMethod("phone.revoke", "Unpair a phone: { id } → true; its token stops working at once", ReadOnly: false, [RpcParam.Req("id")]),
            (req, _) =>
            {
                var ok = auth.Revoke(req.Required("id"));
                if (!ok) throw new RpcException("not_found", "no such device");
                ctx.Events.Publish("phone.changed", new { });
                return Task.FromResult<object?>(true);
            });
    }
}
