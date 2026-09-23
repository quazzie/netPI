using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Web;

/// <summary>
/// WebSocket clients. Subscribes once to the bus and fans every <c>Ui</c> event out: the envelope is serialized once
/// and queued to every client (broadcast events) or only to clients subscribed to the event's session.
/// </summary>
internal sealed class WsHub : IDisposable
{
    private readonly HostKernel _k;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, WsClient> _clients = new();
    private readonly IDisposable _subscription;

    public WsHub(HostKernel kernel)
    {
        _k = kernel;
        _log = kernel.LoggerFactory.CreateLogger("NetPI.WebSocket");
        _subscription = kernel.Bus.Subscribe("*", OnEvent);
    }

    public int ClientCount => _clients.Count;

    private void OnEvent(BusEvent e)
    {
        if (!e.Ui || _clients.IsEmpty) return;
        byte[]? payload = null;
        foreach (var client in _clients.Values)
        {
            if (e.SessionId is not null && !client.WantsSession(e.SessionId)) continue;
            payload ??= Wire.Event(e, _log);
            if (payload is null) return;
            client.Enqueue(payload);
        }
    }

    public async Task RunClientAsync(WebSocket socket, CancellationToken ct)
    {
        var client = new WsClient(socket, _k, _log);
        client.Enqueue(Wire.Hello(client.Id, HostInfo.Version)); // before any event
        _clients[client.Id] = client;
        _log.LogDebug("WebSocket client {Id} connected ({Count} total)", client.Id, _clients.Count);
        try { await client.RunAsync(ct).ConfigureAwait(false); }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _log.LogDebug("WebSocket client {Id} disconnected", client.Id);
        }
    }

    public void CloseAll()
    {
        foreach (var c in _clients.Values) c.Abort();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        CloseAll();
    }
}

internal sealed class WsClient
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;
    private const int MaxPending = 20_000;

    private readonly WebSocket _ws;
    private readonly HostKernel _k;
    private readonly ILogger _log;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private volatile SessionFilter _filter = SessionFilter.None;
    private int _pending;

    public WsClient(WebSocket ws, HostKernel kernel, ILogger log)
    {
        _ws = ws;
        _k = kernel;
        _log = log;
        Id = "c_" + Ids.Short(10);
    }

    public string Id { get; }

    public bool WantsSession(string sessionId)
    {
        var f = _filter;
        return f.All || f.Ids.Contains(sessionId);
    }

    public void Enqueue(byte[] message)
    {
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            _log.LogWarning("WebSocket client {Id} is not reading ({Pending} messages queued); disconnecting it", Id, MaxPending);
            Abort();
            return;
        }
        if (!_out.Writer.TryWrite(message)) Interlocked.Decrement(ref _pending);
    }

    public void Abort()
    {
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(static s => ((WsClient)s!).Abort(), this);
        var sender = Task.Run(SendLoopAsync);
        try
        {
            await ReceiveLoopAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WebSocket client {Id} failed", Id);
        }
        finally
        {
            Abort();
            try { await sender.ConfigureAwait(false); } catch { }
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
                }
                catch { }
            }
            // _cts is intentionally not disposed: in-flight RPC handlers may still read its token.
        }
    }

    private async Task SendLoopAsync()
    {
        var reader = _out.Reader;
        var token = _cts.Token;
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var message))
                {
                    Interlocked.Decrement(ref _pending);
                    await _ws.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { } // a dead socket also ends the receive loop
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var count = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageBytes)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct).ConfigureAwait(false);
                        return;
                    }
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxMessageBytes));
                    buffer.AsSpan(0, count).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                var result = await _ws.ReceiveAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                count += result.Count;
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text) Handle(buffer.AsMemory(0, count));
                count = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Handle(ReadOnlyMemory<byte> json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            _log.LogDebug("WebSocket client {Id} sent invalid JSON", Id);
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "rpc":
                    // Clone: the document (and the receive buffer behind it) is reused for the next message.
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
                    var method = root.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    var p = root.TryGetProperty("p", out var pEl) ? pEl.Clone() : default;
                    if (string.IsNullOrEmpty(method))
                    {
                        Enqueue(Wire.Error(id, "bad_request", "Missing method 'm'"));
                        return;
                    }
                    _ = Task.Run(() => InvokeAsync(id, method, p));
                    break;
                case "sub":
                    _filter = SessionFilter.From(root.TryGetProperty("sessions", out var s) ? s : default);
                    break;
                case "ping":
                    Enqueue(Wire.Pong);
                    break;
            }
        }
    }

    private async Task InvokeAsync(JsonElement id, string method, JsonElement p)
    {
        byte[] response;
        try
        {
            var result = await _k.Rpc.InvokeAsync(method, p, Id, _cts.Token).ConfigureAwait(false);
            response = Wire.Result(id, result);
        }
        catch (Exception ex)
        {
            var (code, message, _) = Wire.MapError(ex, _log, method);
            response = Wire.Error(id, code, message);
        }
        Enqueue(response);
    }

    private sealed class SessionFilter
    {
        public static readonly SessionFilter None = new(false, FrozenSet<string>.Empty);

        private SessionFilter(bool all, FrozenSet<string> ids)
        {
            All = all;
            Ids = ids;
        }

        public bool All { get; }
        public FrozenSet<string> Ids { get; }

        public static SessionFilter From(JsonElement sessions)
        {
            if (sessions.ValueKind == JsonValueKind.String && sessions.GetString() == "*") return new SessionFilter(true, FrozenSet<string>.Empty);
            if (sessions.ValueKind != JsonValueKind.Array) return None;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in sessions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var v = item.GetString()!;
                if (v == "*") return new SessionFilter(true, FrozenSet<string>.Empty);
                ids.Add(v);
            }
            return new SessionFilter(false, ids.ToFrozenSet(StringComparer.Ordinal));
        }
    }
}
