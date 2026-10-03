using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Web;

/// <summary>
/// The chat's Browser view: a WebSocket at <c>/api/p/netpi.tools.web/view?session=…</c> (the host's token, like every
/// UI call) that streams the chat's tab as JPEG frames (<c>Page.startScreencast</c>) and takes the user's mouse, keys and
/// text back to the page — so the user watches the agent, or logs in to a site in the agents' own browser, without a
/// window of its own. It follows the chat's tab when the agent opens, moves or closes it.
/// <para>Wire (JSON text). To the view: <c>{ type: "state", hasTab, kind?, url?, title?, shared? }</c>,
/// <c>{ type: "frame", data, meta }</c>. From the view: <c>{ type: "mouse", event, x, y, button?, clickCount?, deltaX?,
/// deltaY?, modifiers? }</c> (x/y in the page's CSS pixels), <c>{ type: "key", event: "keyDown"|"keyUp", key, code,
/// keyCode, text?, modifiers? }</c>, <c>{ type: "text", text }</c> (a paste), <c>{ type: "open", url }</c> (opens the
/// chat's tab in the default browser when it has none), <c>{ type: "nav", to: "back"|"forward"|"reload" }</c>,
/// <c>{ type: "front" }</c> (bring a tab in the user's Chrome forward).</para>
/// </summary>
internal sealed class BrowserView(IPluginContext ctx, BrowserHost host)
{
    public void Register() => ctx.Http.Map("view", HandleAsync);

    private async Task HandleAsync(HttpContext http)
    {
        var sessionId = http.Request.Query["session"].ToString();
        if (!http.WebSockets.IsWebSocketRequest || sessionId.Length == 0)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsync("a WebSocket with ?session=<id>").ConfigureAwait(false);
            return;
        }
        using var ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, ctx.Stopping);
        var viewer = new Viewer(ws, ctx.Logger);
        BrowserTab? tab = null;
        var gate = new SemaphoreSlim(1, 1);

        async Task BindAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = host.Peek(sessionId);
                if (!ReferenceEquals(now, tab))
                {
                    if (tab is not null)
                    {
                        tab.FrameReady -= viewer.Frame;
                        tab.Changed -= OnChanged;
                        await tab.StopScreencastAsync().ConfigureAwait(false);
                    }
                    tab = now;
                    if (tab is not null)
                    {
                        tab.FrameReady += viewer.Frame;
                        tab.Changed += OnChanged;
                        try { await tab.StartScreencastAsync(stop.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { ctx.Logger.LogDebug(ex, "browser view: no screencast"); }
                        if (tab.LastFrame is { } last) viewer.Frame(last.Data, last.Meta);
                    }
                }
                await viewer.SendAsync(await StateAsync(tab).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "browser view: binding failed"); }
            finally { gate.Release(); }
        }
        void OnChanged() => _ = BindAsync();
        void OnTabChanged(string sid) { if (sid == sessionId) _ = BindAsync(); }

        host.TabChanged += OnTabChanged;
        try
        {
            await BindAsync().ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, stop.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(message.ToArray()); msg = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                finally { message.SetLength(0); }
                try { await InputAsync(msg, sessionId, tab, BindAsync, stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or BrowserUnavailableException)
                {
                    await viewer.SendAsync(new JsonObject { ["type"] = "error", ["message"] = ex.Message }).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            host.TabChanged -= OnTabChanged;
            if (tab is not null)
            {
                tab.FrameReady -= viewer.Frame;
                tab.Changed -= OnChanged;
                await tab.StopScreencastAsync().ConfigureAwait(false);
            }
            viewer.Close();
        }
    }

    private static async Task<JsonObject> StateAsync(BrowserTab? tab)
    {
        if (tab is null || !tab.IsOpen) return new JsonObject { ["type"] = "state", ["hasTab"] = false };
        var state = new JsonObject { ["type"] = "state", ["hasTab"] = true, ["kind"] = tab.Kind, ["shared"] = tab.Shared };
        try
        {
            var (url, title) = await tab.InfoAsync(CancellationToken.None).ConfigureAwait(false);
            state["url"] = url;
            state["title"] = title;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        return state;
    }

    private async Task InputAsync(JsonElement m, string sessionId, BrowserTab? tab, Func<Task> rebind, CancellationToken ct)
    {
        var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
        double D(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        int I(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string? S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (type)
        {
            case "open":
            {
                var url = S("url")?.Trim();
                if (string.IsNullOrEmpty(url)) return;
                if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) url = (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + url;
                tab ??= await host.TabAsync(sessionId, create: true, WebOptions.Read(ctx.Settings).BrowserTarget, ct).ConfigureAwait(false);
                await rebind().ConfigureAwait(false);
                await tab!.NavigateAsync(url, ct).ConfigureAwait(false);
                return;
            }
        }
        if (tab is null || !tab.IsOpen) return;
        switch (type)
        {
            case "mouse":
                await tab.UserInputAsync("Input.dispatchMouseEvent", new
                {
                    type = S("event") ?? "mouseMoved", x = D("x"), y = D("y"), button = S("button") ?? "none",
                    clickCount = I("clickCount"), deltaX = D("deltaX"), deltaY = D("deltaY"), modifiers = I("modifiers"),
                }, ct).ConfigureAwait(false);
                break;
            case "key":
            {
                var text = S("text");
                var ev = S("event") == "keyUp" ? "keyUp" : text is { Length: > 0 } ? "keyDown" : "rawKeyDown";
                await tab.UserInputAsync("Input.dispatchKeyEvent", new
                {
                    type = ev, key = S("key"), code = S("code"), windowsVirtualKeyCode = I("keyCode"), nativeVirtualKeyCode = I("keyCode"),
                    modifiers = I("modifiers"), text = ev == "keyDown" ? text : null, unmodifiedText = ev == "keyDown" ? text : null,
                }, ct).ConfigureAwait(false);
                break;
            }
            case "text":
                if (S("text") is { Length: > 0 } pasted) await tab.UserInputAsync("Input.insertText", new { text = pasted }, ct).ConfigureAwait(false);
                break;
            case "nav":
                switch (S("to"))
                {
                    case "back": await tab.HistoryAsync(-1, ct).ConfigureAwait(false); break;
                    case "forward": await tab.HistoryAsync(1, ct).ConfigureAwait(false); break;
                    case "reload": await tab.UserInputAsync("Page.reload", new { }, ct).ConfigureAwait(false); break;
                }
                break;
            case "front":
                await tab.ActivateAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// One view's socket: frames go out as they come, but never more than one in flight — a slow view gets the newest
    /// frame, not a queue of old ones.
    /// </summary>
    private sealed class Viewer(WebSocket ws, ILogger log)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private string? _pendingData;
        private JsonElement _pendingMeta;
        private int _pumping;

        public void Frame(string data, JsonElement meta)
        {
            lock (this) { _pendingData = data; _pendingMeta = meta; }
            if (Interlocked.Exchange(ref _pumping, 1) == 0) _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    string? data;
                    JsonElement meta;
                    lock (this) { data = _pendingData; meta = _pendingMeta; _pendingData = null; }
                    if (data is null) break;
                    var frame = new JsonObject { ["type"] = "frame", ["data"] = data, ["meta"] = JsonNode.Parse(meta.GetRawText()) };
                    await SendAsync(frame).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "browser view: a frame was not sent"); }
            finally
            {
                Volatile.Write(ref _pumping, 0);
                bool more;
                lock (this) more = _pendingData is not null;
                if (more && Interlocked.Exchange(ref _pumping, 1) == 0) _ = PumpAsync();
            }
        }

        public async Task SendAsync(JsonObject message)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(message.ToJsonString());
            await _send.WaitAsync().ConfigureAwait(false);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
            finally { _send.Release(); }
        }

        public void Close() => _send.Dispose();
    }
}
