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
    /// <summary>How many WebSocket clients one app serves at once (see the refusal in WebServer.WebSocketAsync).</summary>
    public const int MaxClients = 50;

    /// <summary>Largest single WebSocket message the server will read. The UI is told this in <c>app.info</c>
    /// (<c>maxMessageBytes</c>) so it can fit what it sends into it instead of being cut off mid-sentence.</summary>
    public const int MaxMessageBytes = 2 * 1024 * 1024;

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
    // The websocket protocol carries small JSON envelopes; a request body over the HTTP API is what needs room, and that
    // limit is set on the server (MaxRequestBodySize). Renting 64 MB per client because one message was big is a way to
    // lose the process, not a way to serve a big request.
    private const int MaxMessageBytes = WsHub.MaxMessageBytes;
    private const int MaxPending = 20_000;
    /// <summary>Queued bytes per client, besides the message count: a client that never reads is cut off by whichever
    /// limit it reaches first, so a few large messages cannot sit in the queue for nothing.</summary>
    private const int MaxPendingBytes = 32 * 1024 * 1024;
    /// <summary>
    /// RPC frames one connection may have unanswered at once. Every frame runs its handler on the pool with no end to
    /// it (a flush that waits, a plugin method that blocks), so without a cap a client in a retry loop grows the pool's
    /// queue without bound; past it a frame is answered at once with <c>busy</c>, and the client waits for answers
    /// before it sends more.
    /// </summary>
    internal const int MaxInFlight = 64;

    private readonly WebSocket _ws;
    private readonly HostKernel _k;
    private readonly ILogger _log;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private volatile SessionFilter _filter = SessionFilter.None;
    private volatile bool _aborted;
    private int _pending;
    private long _pendingBytes;
    private int _inFlight;

    public WsClient(WebSocket ws, HostKernel kernel, ILogger log)
    {
        _ws = ws;
        _k = kernel;
        _log = log;
        Id = "c_" + Ids.Short(10);
    }

    public string Id { get; }

    /// <summary>For tests: whether <see cref="Abort"/> has cut this client off.</summary>
    internal bool Aborted => _aborted;

    /// <summary>For tests: what the backlog is right now.</summary>
    internal (int Pending, long Bytes) Backlog => (Volatile.Read(ref _pending), Interlocked.Read(ref _pendingBytes));

    /// <summary>For tests: the RPC frames whose handlers have not answered yet.</summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

    public bool WantsSession(string sessionId)
    {
        var f = _filter;
        return f.All || f.Ids.Contains(sessionId);
    }

    /// <summary>
    /// Queue a message for the send loop. The count and byte caps cut off a client that stops reading, so a backlog
    /// cannot grow without bound — but only while something is ALREADY queued: one message on an empty queue gets in
    /// however big it is, and a huge one is the handler's problem (InvokeAsync answers it with <c>too_large</c> when
    /// it cannot even cross an empty queue) instead of a socket that dies and a client logged as "not reading".
    /// </summary>
    public void Enqueue(byte[] message)
    {
        var hadWork = Volatile.Read(ref _pending) > 0 || Interlocked.Read(ref _pendingBytes) > 0;
        var pending = Interlocked.Increment(ref _pending);
        var bytes = Interlocked.Add(ref _pendingBytes, message.Length);
        if (hadWork && (pending > MaxPending || bytes > MaxPendingBytes))
        {
            Release(message.Length);
            _log.LogWarning("WebSocket client {Id} is not reading ({Pending} messages, {Bytes} bytes queued); disconnecting it",
                Id, Volatile.Read(ref _pending), Interlocked.Read(ref _pendingBytes));
            Abort();
            return;
        }
        if (!_out.Writer.TryWrite(message)) Release(message.Length);
    }

    private void Release(int bytes)
    {
        Interlocked.Decrement(ref _pending);
        Interlocked.Add(ref _pendingBytes, -bytes);
    }

    public void Abort()
    {
        _aborted = true;
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
                    Release(message.Length);
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
                        var reason = $"message too big: {MaxMessageBytes / (1024 * 1024)} MB per message (app.info.maxMessageBytes)";
                        _log.LogWarning("WebSocket client {Id} sent a message over the limit; closing: {Reason}", Id, reason);
                        await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, reason, ct).ConfigureAwait(false);
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
                    if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
                    {
                        var inFlight = Interlocked.Decrement(ref _inFlight);
                        Enqueue(Wire.Error(id, "busy",
                            $"{inFlight} requests are in flight on this connection, the most it takes at once ({MaxInFlight}): wait for their answers before sending more"));
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
        try
        {
            byte[] response;
            try
            {
                var result = await _k.Rpc.InvokeAsync(method, p, Id, _cts.Token).ConfigureAwait(false);
                response = Wire.Result(id, result);
                if (response.Length > MaxPendingBytes)
                {
                    // One message bigger than the whole queue: it would make the byte cap trip on the next message of a
                    // reading client and never fit into a client that is not. The caller gets an error it can act on
                    // (request less: a shorter page, an earlier beforeSeq) instead of a socket that dies.
                    response = Wire.Error(id, "too_large",
                        $"The response is {response.Length / (1024 * 1024)} MB, over the {MaxPendingBytes / (1024 * 1024)} MB a client can take in one message: request less (an earlier or shorter page)");
                }
            }
            catch (Exception ex)
            {
                var (code, message, _) = Wire.MapError(ex, _log, method);
                response = Wire.Error(id, code, message);
            }
            Enqueue(response);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);   // counted in Handle, whatever the handler did
        }
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
