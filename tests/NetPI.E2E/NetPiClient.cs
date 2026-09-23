using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.E2E;

/// <summary>A bus event received over the WebSocket.</summary>
public sealed class Ev
{
    public required string Type { get; init; }
    public string? Sid { get; init; }
    public JsonElement D { get; init; }
    public long Seq { get; init; }
    /// <summary>Position in the client's event log (use with <see cref="NetPiClient.Mark"/>).</summary>
    public long Index { get; init; }
    public DateTimeOffset At { get; init; }

    public override string ToString() => $"#{Index} {Type} {D.GetRawText()}";
}

public sealed class RpcError(string method, string code, string detail) : Exception($"{method}: {code}: {detail}")
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}

/// <summary>The UI's view of the server: JSON RPC over <c>/ws</c> plus the event stream (everything is recorded).</summary>
public sealed class NetPiClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<Ev> _events = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receive;
    private int _nextId;

    public string? ClientId { get; private set; }
    public bool Closed { get; private set; }

    public static async Task<NetPiClient> ConnectAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        var c = new NetPiClient();
        c._ws.Options.SetRequestHeader("X-NetPI-Token", token);
        c._ws.Options.Proxy = null;
        await c._ws.ConnectAsync(new Uri(baseUrl.Replace("http://", "ws://") + "/ws"), ct);
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c._receive = Task.Run(() => c.ReceiveLoopAsync(hello));
        await hello.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        return c;
    }

    private async Task ReceiveLoopAsync(TaskCompletionSource hello)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var json = ms.ToArray();
                ms.SetLength(0);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                switch (root.S("t"))
                {
                    case "hello":
                        ClientId = root.S("clientId");
                        hello.TrySetResult();
                        break;
                    case "res":
                        if (root.P("id").ValueKind == JsonValueKind.Number && _pending.TryRemove(root.P("id").GetInt32(), out var tcs))
                        {
                            if (root.Has("e")) tcs.TrySetException(new RpcError("?", root.P("e").S("code") ?? "?", root.P("e").S("message") ?? ""));
                            else tcs.TrySetResult(root.P("r").Clone());
                        }
                        break;
                    case "ev":
                        lock (_gate)
                        {
                            _events.Add(new Ev
                            {
                                Type = root.S("type") ?? "", Sid = root.S("sid"), D = root.P("d").Clone(), Seq = root.L("seq"),
                                Index = _events.Count, At = DateTimeOffset.UtcNow,
                            });
                            var old = _changed;
                            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            old.TrySetResult();
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            Closed = true;
            foreach (var p in _pending.Values) p.TrySetException(new IOException("WebSocket closed"));
            lock (_gate) _changed.TrySetResult();
        }
    }

    private async Task SendAsync(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _send.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        finally { _send.Release(); }
    }

    public async Task<JsonElement> Rpc(string method, object? p = null, int timeoutMs = 30_000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendAsync(new { t = "rpc", id, m = method, p = p ?? new { } });
        try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { _pending.TryRemove(id, out _); throw new AssertException($"RPC {method} timed out after {timeoutMs}ms"); }
        catch (RpcError e) { throw new RpcError(method, e.Code, e.Detail); }
    }

    public Task Subscribe(params string[] sessions) => SendAsync(new { t = "sub", sessions });

    /// <summary>Current end of the event log.</summary>
    public long Mark()
    {
        lock (_gate) return _events.Count;
    }

    public List<Ev> Since(long mark, Func<Ev, bool>? filter = null)
    {
        lock (_gate) return _events.Skip((int)mark).Where(e => filter is null || filter(e)).ToList();
    }

    public List<Ev> All(Func<Ev, bool>? filter = null) => Since(0, filter);

    /// <summary>First event after <paramref name="mark"/> matching <paramref name="match"/>.</summary>
    public async Task<Ev> WaitFor(long mark, Func<Ev, bool> match, string what, int timeoutMs = 30_000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                for (var i = (int)mark; i < _events.Count; i++)
                    if (match(_events[i])) return _events[i];
                changed = _changed.Task;
            }
            if (Closed) throw new AssertException($"WebSocket closed while waiting for {what}");
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                var recent = Since(Math.Max(mark, Mark() - 15)).Select(e => "      " + Check.Show(e.ToString()));
                throw new AssertException($"timed out after {timeoutMs}ms waiting for {what}. Last events:\n{string.Join("\n", recent)}");
            }
            await Task.WhenAny(changed, Task.Delay(left));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token);
            }
        }
        catch { }
        _cts.Cancel();
        if (_receive is not null) { try { await _receive.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        _ws.Dispose();
    }
}
