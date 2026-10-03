using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Web;

/// <summary>
/// The NetPI Chrome extension's end: a WebSocket at <c>/api/p/netpi.tools.web/extension?key=…</c> (an open route: the
/// extension cannot hold the host's per-run token, so it proves itself with a key of its own, written next to the
/// extension in <c>&lt;home&gt;/browser/chrome-extension/config.json</c>). Over it the extension forwards DevTools
/// commands to the user's tabs through <c>chrome.debugger</c> — so the relay is an <see cref="ICdp"/> like a browser's own
/// endpoint — and its popup shares a tab with a chat.
/// <para>Wire (JSON text frames). NetPI → extension: <c>{ id, method, params, sessionId? }</c>; the extension answers
/// <c>{ id, result }</c> or <c>{ id, error: { message } }</c> and sends events as <c>{ method, params, sessionId? }</c>.
/// Its own requests are <c>{ rid, op, … }</c> answered with <c>{ rid, result }</c> or <c>{ rid, error }</c>: <c>hello</c>,
/// <c>chats</c>, <c>share</c>, <c>unshare</c>, <c>shared</c>. NetPI tells it <c>{ type: "shared", tabs }</c> when the shared tabs change.</para>
/// </summary>
internal sealed class ExtensionRelay : ICdp
{
    private readonly IPluginContext _ctx;
    private readonly BrowserHost _host;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly byte[] _key;
    private WebSocket? _socket;
    private int _id;

    public ExtensionRelay(IPluginContext ctx, BrowserHost host)
    {
        _ctx = ctx;
        _host = host;
        _key = Encoding.UTF8.GetBytes(LoadKey(ctx));
        Folder = Path.Combine(ctx.Paths.Home, "browser", "chrome-extension");
        ctx.Http.Map("extension", HandleAsync, open: true);
    }

    public string Kind => "extension";

    /// <summary>Where the extension is kept for the user to load (chrome://extensions → Load unpacked).</summary>
    public string Folder { get; }

    public bool IsOpen => _socket?.State == WebSocketState.Open;

    /// <summary>What the extension said about itself (version, browser) when it connected.</summary>
    public JsonObject? Hello { get; private set; }

    public event Action<string, JsonElement, string?>? Event;

    public event Action? Disconnected;

    private static string LoadKey(IPluginContext ctx)
    {
        var file = Path.Combine(ctx.Paths.Home, "browser", "extension-key");
        try { if (File.ReadAllText(file).Trim() is { Length: >= 32 } existing) return existing; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, key);
        return key;
    }

    /// <summary>
    /// Puts the extension (shipped in the plugin's <c>extension</c> folder) where the user loads it from, with
    /// <c>config.json</c>: the server's address and the key. Called at start and again once the server's address is known.
    /// </summary>
    public void Install(string? serverUrl)
    {
        var source = Path.Combine(_ctx.PluginDirectory, "extension");
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(Folder);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(Folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(file))) File.Copy(file, dest, overwrite: true);
        }
        var config = new JsonObject { ["url"] = serverUrl ?? "http://127.0.0.1:7431", ["key"] = Encoding.UTF8.GetString(_key) };
        var path = Path.Combine(Folder, "config.json");
        var text = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }

    private bool KeyMatches(string? candidate) =>
        !string.IsNullOrEmpty(candidate) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _key);

    private async Task HandleAsync(HttpContext http)
    {
        if (!KeyMatches(http.Request.Query["key"].ToString()))
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsync("NetPI: wrong extension key (reload the extension: its config.json is rewritten when NetPI starts)").ConfigureAwait(false);
            return;
        }
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(new JsonObject { ["ok"] = true, ["connected"] = IsOpen }.ToJsonString()).ConfigureAwait(false);
            return;
        }
        using var ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        // one Chrome at a time: a new connection (the extension reloaded, or its worker restarted) replaces the old one
        var old = Interlocked.Exchange(ref _socket, ws);
        if (old is not null) { Fail(); try { old.Abort(); } catch (Exception) { } }
        _ctx.Logger.LogInformation("browser: the NetPI extension connected");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _ctx.Stopping);
        try { await ReadLoopAsync(ws, stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            if (Interlocked.CompareExchange(ref _socket, null, ws) == ws)
            {
                Fail();
                Hello = null;
                _ctx.Logger.LogInformation("browser: the NetPI extension disconnected");
                Disconnected?.Invoke();
            }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private void Fail()
    {
        foreach (var (id, p) in _pending) { p.TrySetException(new InvalidOperationException("the NetPI extension disconnected")); _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            JsonElement root;
            try { using var doc = JsonDocument.Parse(bytes); root = doc.RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (root.TryGetProperty("rid", out _)) { _ = RequestAsync(root, ct); continue; }
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var tcs)) continue;
                if (root.TryGetProperty("error", out var err))
                    tcs.TrySetException(new InvalidOperationException(err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString()));
                else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
                continue;
            }
            if (root.TryGetProperty("method", out var me) && me.GetString() is { } method)
            {
                try
                {
                    Event?.Invoke(method, root.TryGetProperty("params", out var p) ? p.Clone() : default,
                        root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
                }
                catch (Exception ex) { _ctx.Logger.LogDebug(ex, "browser: an extension event failed"); }
            }
        }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, string? sessionId, CancellationToken ct, int timeoutSeconds = 30)
    {
        var ws = _socket ?? throw new InvalidOperationException("the NetPI extension is not connected");
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(ws, new { id, method, @params = parameters ?? new { }, sessionId }, ct).ConfigureAwait(false);
            try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException($"{method} got no answer from the extension within {timeoutSeconds} s"); }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(WebSocket ws, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, CdpConnection.Json);
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    /// <summary>Tells the extension which tabs are shared now (its badge and popup).</summary>
    public async Task PushSharedAsync()
    {
        if (_socket is not { State: WebSocketState.Open } ws) return;
        try { await WriteAsync(ws, new { type = "shared", tabs = SharedList() }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private JsonArray SharedList()
    {
        var list = new JsonArray();
        foreach (var (tabId, sid) in _host.SharedTabs())
            list.Add(new JsonObject { ["tabId"] = tabId, ["sessionId"] = sid, ["chat"] = sid is null ? null : _ctx.Sessions.GetSession(sid)?.Title });
        return list;
    }

    // ------------------------------------------------------------------ the extension's own requests

    private async Task RequestAsync(JsonElement req, CancellationToken ct)
    {
        var rid = req.GetProperty("rid").Clone();
        var op = req.TryGetProperty("op", out var o) ? o.GetString() : null;
        object? result;
        string? error = null;
        try
        {
            result = op switch
            {
                "hello" => Hi(req),
                "chats" => Chats(),
                "share" => await ShareAsync(req, ct).ConfigureAwait(false),
                "unshare" => await UnshareAsync(req).ConfigureAwait(false),
                "shared" => SharedList(),
                _ => throw new InvalidOperationException($"unknown op \"{op}\""),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = null;
            error = ex.Message;
        }
        if (_socket is not { State: WebSocketState.Open } ws) return;
        try { await WriteAsync(ws, error is null ? new { rid, result } : new { rid, error = new { message = error } }, ct).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private object Hi(JsonElement req)
    {
        Hello = JsonNode.Parse(req.GetRawText()) as JsonObject;
        return new { name = "NetPI", version = typeof(ExtensionRelay).Assembly.GetName().Version?.ToString() };
    }

    private object Chats() =>
        _ctx.Sessions.ListSessions(new SessionQuery { Limit = 40 })
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new { id = s.Id, title = string.IsNullOrWhiteSpace(s.Title) ? "New session" : s.Title, updatedAt = s.UpdatedAt })
            .ToArray();

    private async Task<object> ShareAsync(JsonElement req, CancellationToken ct)
    {
        var tabId = req.GetProperty("tabId").GetInt32();
        var url = req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var title = req.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var sessionId = req.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sid ? sid : null;
        var text = req.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null;
        if (sessionId == "new")
            sessionId = _ctx.Sessions.CreateSession(new SessionInfo { Title = string.IsNullOrWhiteSpace(title) ? "Shared tab" : title }).Id;
        else if (sessionId is not null && _ctx.Sessions.GetSession(sessionId) is null)
            throw new InvalidOperationException("That chat is gone.");
        await _host.ShareAsync(tabId, url, title, sessionId, ct).ConfigureAwait(false);
        await PushSharedAsync().ConfigureAwait(false);
        if (sessionId is not null && !string.IsNullOrEmpty(text))
        {
            // the user's message goes to the chat as if typed there, with the tab it is about
            var message = $"{text}\n\n(I shared a tab of my Chrome with you: {title} — {url}. It is this chat's browser tab now.)";
            await _ctx.Rpc.InvokeAsync("agent.send", new { sessionId, text = message, mode = "auto" }, ct).ConfigureAwait(false);
        }
        return new { sessionId, chat = sessionId is null ? null : _ctx.Sessions.GetSession(sessionId)?.Title };
    }

    private async Task<object> UnshareAsync(JsonElement req)
    {
        await _host.UnshareAsync(req.GetProperty("tabId").GetInt32()).ConfigureAwait(false);
        await PushSharedAsync().ConfigureAwait(false);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        var ws = Interlocked.Exchange(ref _socket, null);
        Fail();
        try { ws?.Abort(); } catch (Exception) { }
        _send.Dispose();
        return ValueTask.CompletedTask;
    }
}
