using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

internal sealed class StdioTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Task _reader, _stderr;
    private readonly int _limit;
    private readonly ServerConfig _config;
    private long _next;
    private string? _subscription;
    private Subscription _subscriptionState = new();
    private int _disposed;
    public event Action<JsonObject>? Notification;
    public event Action<Exception>? Closed;
    public string Version { get; set; } = Protocol.Modern;

    public StdioTransport(ServerConfig config, int limit, Action<string> log)
    {
        _config = config; _limit = limit;
        var info = new ProcessStartInfo(config.Command)
        {
            WorkingDirectory = config.Cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in config.Args) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        foreach (var (name, source) in config.Env) info.Environment[name] = config.Secret(source);
        _process = Process.Start(info) ?? throw new McpException("Could not start MCP server.");
        _reader = ReadAsync();
        _stderr = DrainErrorsAsync(log);
    }
    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, JsonObject? headers = null)
    {
        var id = Interlocked.Increment(ref _next);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop.IsCancellationRequested) throw new McpException("MCP server is disconnected.");
        _pending[key] = completion;
        if (method == "subscriptions/listen") { _subscriptionState = new Subscription(); _subscription = key; }
        try
        {
            await SendAsync(Protocol.Message(method, parameters, id, Version), ct).ConfigureAwait(false);
            return Protocol.Result(await completion.Task.WaitAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                using var timeout = new CancellationTokenSource(500);
                await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "Client cancelled" }, timeout.Token).ConfigureAwait(false);
            }
            catch { }
            throw;
        }
        finally { _pending.TryRemove(key, out _); }
    }
    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct) =>
        SendAsync(Protocol.Message(method, parameters, null, Version), ct);
    public async Task ListenAsync(bool modern, CancellationToken ct)
    {
        if (!modern) { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return; }
        // Subscription stays pending. Notifications on this channel carry its request id.
        await RequestAsync("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } }, ct).ConfigureAwait(false);
        throw new McpException("MCP notification subscription ended.");
    }
    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var json = message.ToJsonString();
        if (json.Length > _limit) throw new McpException("MCP request exceeds the configured size limit.");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        Exception failure = new McpException("MCP server closed stdout.");
        try
        {
            while (!_stop.IsCancellationRequested && await Protocol.ReadLineAsync(_process.StandardOutput, _limit, _stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                var m = Protocol.Parse(line, _limit);
                if (m["method"] is not null)
                {
                    if (m["id"] is not null)
                    {
                        // We advertise no client-request capabilities. Legacy servers still get a definitive reply.
                        if (Version == Protocol.Modern) throw new McpException("Modern MCP servers must not send JSON-RPC requests.");
                        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = m["id"]!.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Client method is not supported." } }, _stop.Token).ConfigureAwait(false);
                    }
                    else if (Version == Protocol.Legacy) Notification?.Invoke(m);
                    else
                    {
                        if (_subscription is not null && _subscriptionState.Accept(m, _subscription)) Notification?.Invoke(m);
                    }
                }
                else if (_pending.TryGetValue(Protocol.Id(m["id"]), out var completion)) completion.TrySetResult(m);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var item in _pending.Values) item.TrySetException(failure);
            _stop.Cancel();
            if (Volatile.Read(ref _disposed) == 0) Closed?.Invoke(failure);
        }
    }
    private async Task DrainErrorsAsync(Action<string> log)
    {
        // Drain arbitrarily long stderr lines without buffering or publishing secrets.
        var buffer = new char[2048];
        try { while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false) is var count && count > 0)
            log(_config.Redact(new string(buffer, 0, count))); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var p in _pending.Values) p.TrySetException(new McpException("MCP server stopped."));
        try { _process.StandardInput.Close(); } catch { }
        try
        {
            using var timeout = new CancellationTokenSource(1000);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch { try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { } }
        try { await Task.WhenAll(_reader, _stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _process.Dispose();
        _stop.Dispose();
        _write.Dispose();
    }
}
