using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPI.Tools.Web;

/// <summary>A DevTools-protocol connection to a browser endpoint, with flattened sessions for its pages.</summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;
    private int _id;

    /// <summary>Events: method, params, and the session they belong to (null for the browser's own).</summary>
    public event Action<string, JsonElement, string?>? Event;

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var c = new CdpConnection();
        c._ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await c._ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        c._reader = Task.Run(c.ReadLoopAsync);
        return c;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, string? sessionId, CancellationToken ct, int timeoutSeconds = 30)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CdpMessage(id, method, parameters ?? new { }, sessionId), Json);
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException($"{method} got no answer within {timeoutSeconds} s"); }
        finally { _pending.TryRemove(id, out _); }
    }

    private sealed record CdpMessage(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object Params,
        [property: JsonPropertyName("sessionId")] string? SessionId);

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                Dispatch(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception) { /* closed */ }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("the browser connection closed"));
    }

    private void Dispatch(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
        {
            if (!_pending.TryGetValue(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new InvalidOperationException(err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText()));
            else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            return;
        }
        if (root.TryGetProperty("method", out var me) && me.GetString() is { } method)
        {
            try
            {
                Event?.Invoke(method, root.TryGetProperty("params", out var p) ? p.Clone() : default,
                    root.TryGetProperty("sessionId", out var s) ? s.GetString() : null);
            }
            catch (Exception) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception) { }
        _ws.Dispose();
        if (_reader is not null) try { await _reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
        _send.Dispose();
    }
}
