using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Lsp;

/// <summary>
/// A language server's client end: JSON-RPC 2.0 over two streams with the LSP's <c>Content-Length</c> framing, written on
/// <see cref="System.Text.Json"/> alone (like the MCP plugin's client: no package). Requests wait for their answer; the
/// server's notifications go to <see cref="OnNotification"/> (publishDiagnostics); the server's own requests are answered
/// with what a minimal client says (no configuration, progress tokens accepted, registrations acknowledged).
/// </summary>
internal sealed class LspClient : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private long _nextId;

    /// <summary>A notification from the server: its method and params.</summary>
    public Action<string, JsonNode?>? OnNotification { get; set; }
    /// <summary>Set when the server's stream ended (it exited or crashed): every request in flight fails with it.</summary>
    public Exception? Closed { get; private set; }

    public LspClient(Stream fromServer, Stream toServer, ILogger log)
    {
        _input = fromServer;
        _output = toServer;
        _log = log;
        _reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            // checked after the request is registered: a server that ends from here on fails it in Close
            if (Closed is { } closed) throw new LspException($"The language server is gone: {closed.Message}");
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            limit.CancelAfter(timeout);
            try { return await answer.Task.WaitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (_stop.IsCancellationRequested) throw new LspException("The language server is gone: the client was stopped");
                // the server did not answer in time: tell it, so it drops the work
                _ = NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id }, CancellationToken.None).ContinueWith(
                    t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new LspException($"The language server did not answer {method} within {timeout.TotalSeconds:0} s.");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, ct);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex) { throw new LspException("The language server is gone: " + ex.Message); }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_input, _stop.Token).ConfigureAwait(false);
                if (message is null) break;
                await DispatchAsync(message).ConfigureAwait(false);
            }
            Close(new LspException("its output ended"));
        }
        catch (OperationCanceledException) { Close(new LspException("the client was stopped")); }
        catch (Exception ex)
        {
            // a broken stream, a malformed header or body: the conversation is over, and whoever waits learns it now
            _log.LogDebug(ex, "lsp: reading from the server failed");
            Close(ex);
        }
    }

    private void Close(Exception why)
    {
        Closed ??= why;
        foreach (var (_, answer) in _pending) answer.TrySetException(new LspException($"The language server is gone: {why.Message}"));
    }

    private async Task DispatchAsync(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];
        if (method is null)
        {
            // an answer to one of ours
            if (id is JsonValue v && v.TryGetValue<long>(out var n) && _pending.TryRemove(n, out var answer))
            {
                if (message["error"] is JsonObject error)
                    answer.TrySetException(new LspException($"{error["message"]?.GetValue<string>() ?? "error"} ({error["code"]})"));
                else answer.TrySetResult(message["result"]);
            }
            return;
        }
        if (id is null)
        {
            try { OnNotification?.Invoke(method, message["params"]); }
            catch (Exception ex) { _log.LogDebug(ex, "lsp: handling {Method} failed", method); }
            return;
        }
        // the server asks something of the client: the answers a client with no settings and no UI gives
        JsonNode? result = method switch
        {
            "workspace/configuration" => new JsonArray([.. (message["params"]?["items"] as JsonArray ?? []).Select(_ => (JsonNode?)null)]),
            "workspace/workspaceFolders" => null,
            _ => null, // window/workDoneProgress/create, client/registerCapability, client/unregisterCapability, …
        };
        try { await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }, _stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is LspException or OperationCanceledException) { }
    }

    /// <summary>One framed message, or null at the end of the stream. Headers other than Content-Length are skipped.</summary>
    internal static async Task<JsonObject?> ReadMessageAsync(Stream input, CancellationToken ct)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            // header lines, ASCII, each ending in \r\n; an empty one ends the headers
            line.Clear();
            while (true)
            {
                if (await input.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') line.Append((char)one[0]);
                if (line.Length > 1024) throw new FormatException("an LSP header line longer than 1024 characters");
            }
            if (line.Length == 0) break;
            var text = line.ToString();
            if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(text["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0) throw new FormatException("an LSP message without Content-Length");
        if (length > 64 * 1024 * 1024) throw new FormatException($"an LSP message of {length} bytes");
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await input.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(body) as JsonObject ?? throw new FormatException("an LSP message that is not an object");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _reader.ConfigureAwait(false); } catch { /* ended */ }
        _stop.Dispose();
        _write.Dispose();
    }
}

internal sealed class LspException(string message) : Exception(message);
