using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.Phone;

/// <summary>
/// What the phone listener serves: the page (static files of <c>wwwroot/phone</c>), <c>POST /api/pair</c> (a one-time code for a device
/// token, set as an HttpOnly cookie), and for a paired device <c>GET /api/me</c>, <c>POST /api/rpc/{method}</c> — a fixed allow-list of
/// the host's RPCs with a fixed list of parameters each, so nothing that reaches files, the shell or settings is callable from here —
/// and <c>GET /api/events</c>, a server-sent event stream of the events a chat view needs.
/// </summary>
internal sealed class PhoneApi : IDisposable
{
    /// <summary>The throttle's limit, for the tests (the throttle type itself is shared source, one per plugin assembly).</summary>
    internal const int FailureLimit = FailureThrottle.Limit;
    public const int MaxBody = 12 * 1024 * 1024;
    public const string Cookie = "netpi_phone";

    /// <summary>The RPCs a phone may call, each with the parameters it may pass. Anything else is refused before it reaches the host.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["projects.list"] = [],
        ["models.list"] = [],
        ["sessions.list"] = ["projectId", "search", "limit", "offset", "includeArchived", "archivedOnly"],
        ["sessions.create"] = ["title", "projectId", "model", "reasoning"],
        ["sessions.messages"] = ["id", "beforeSeq", "afterSeq", "limit"],
        ["agent.send"] = ["sessionId", "text", "images", "mode"],
        ["agent.abort"] = ["sessionId", "subagents"],
        ["runs.list"] = ["includeFinished"],
        ["work.snapshot"] = [],
        ["ask.pending"] = ["sessionId"],
        ["ask.answer"] = ["id", "answers", "text"],
        ["guard.pending"] = ["sessionId"],
        ["guard.answer"] = ["approvalId", "allow", "scope"],
    };

    private static readonly HashSet<string> Relayed = new(StringComparer.Ordinal)
    {
        "agent.status", "agent.queue", "agent.notice", "message.added", "message.updated", "messages.compacted",
        "stream.start", "stream.delta", "stream.end", "stream.reset", "stream.tool", "tool.start", "tool.end",
        "ask.asked", "ask.closed", "guard.asked", "guard.closed", "agents.changed", "process.started", "process.exited", "usage.changed", "session.created", "session.updated", "session.deleted", "session.context",
    };

    private static readonly Dictionary<string, (string File, string Type)> Files = new(StringComparer.Ordinal)
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
        ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
        ["/manifest.webmanifest"] = ("manifest.webmanifest", "application/manifest+json"),
        ["/icon.svg"] = ("icon.svg", "image/svg+xml"),
    };

    private readonly IPluginContext _ctx;
    private readonly PhoneAuth _auth;
    private readonly FailureThrottle _throttle;
    private readonly Func<AddressList> _allow;
    private readonly Func<bool> _trustProxy;
    private readonly string _root;
    private readonly Dictionary<Guid, EventStream> _streams = [];
    private readonly IDisposable _subscription;
    private readonly CancellationToken _stopping;

    public PhoneApi(IPluginContext ctx, PhoneAuth auth, FailureThrottle throttle, Func<AddressList> allow, Func<bool> trustProxy)
    {
        _ctx = ctx;
        _auth = auth;
        _throttle = throttle;
        _allow = allow;
        _trustProxy = trustProxy;
        _stopping = ctx.Stopping;
        _root = Path.Combine(ctx.PluginDirectory, "wwwroot", "phone");
        _subscription = ctx.Events.Subscribe("*", Relay);
    }

    public int Connections { get { lock (_streams) return _streams.Count; } }

    public void Dispose()
    {
        _subscription.Dispose();
        lock (_streams) foreach (var c in _streams.Values) c.Channel.Writer.TryComplete();
    }

    // ---------------------------------------------------------------- requests

    public async Task HandleAsync(HttpContext http)
    {
        var res = http.Response;
        res.Headers.XContentTypeOptions = "nosniff";
        res.Headers.CacheControl = "no-store";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers.XFrameOptions = "DENY";
        try
        {
            var peer = http.Connection.RemoteIpAddress;
            if (!_allow().Contains(peer)) { await Json(http, 403, Err("forbidden", "this address may not use the phone listener (phone.allowFrom)")); return; }
            var client = ClientAddress(http, peer);
            var path = http.Request.Path.Value ?? "/";
            var method = http.Request.Method;

            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (method is not ("GET" or "HEAD")) { await Json(http, 405, Err("method_not_allowed", "GET only")); return; }
                await ServeFile(http, path);
                return;
            }
            if (_throttle.Blocked(client)) { await Json(http, 429, Err("too_many_failures", "too many wrong attempts from this address; try again later")); return; }
            if (method == "POST" && !SameOrigin(http)) { await Json(http, 403, Err("forbidden", "cross-origin request")); return; }

            if (path == "/api/pair" && method == "POST") { await Pair(http, client); return; }

            var device = _auth.Authenticate(http.Request.Cookies[Cookie], client);
            if (device is null)
            {
                if (http.Request.Cookies.ContainsKey(Cookie)) _throttle.Fail(client);
                await Json(http, 401, Err("unauthorized", "this device is not paired (or was revoked)"));
                return;
            }
            switch (path)
            {
                case "/api/me" when method == "GET":
                    await Json(http, 200, new JsonObject { ["ok"] = true, ["device"] = PhoneAuth.Shown(device) });
                    return;
                case "/api/logout" when method == "POST":
                    _auth.Revoke(device["id"]!.GetValue<string>());
                    res.Cookies.Delete(Cookie);
                    await Json(http, 200, new JsonObject { ["ok"] = true });
                    return;
                case "/api/events" when method == "GET":
                    await Events(http);
                    return;
            }
            if (path.StartsWith("/api/rpc/", StringComparison.Ordinal) && method == "POST") { await Rpc(http, path["/api/rpc/".Length..]); return; }
            await Json(http, 404, Err("not_found", "no such path"));
        }
        catch (OperationCanceledException) { }
        catch (BadHttpRequestException) { if (!res.HasStarted) await Json(http, 413, Err("too_large", "the request is too large")); }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Phone: a request failed");
            if (!res.HasStarted) await Json(http, 500, Err("error", "the phone listener failed; see NetPI's log"));
        }
    }

    private async Task Pair(HttpContext http, string client)
    {
        JsonNode? body = await ReadJson(http);
        var code = body?["code"]?.GetValue<string>() ?? "";
        var redeemed = _auth.Redeem(code, body?["name"]?.GetValue<string>() ?? "", client, http.Request.Headers.UserAgent.ToString());
        if (redeemed is not { } r)
        {
            _throttle.Fail(client);
            _ctx.Logger.LogWarning("Phone: a wrong or expired pairing code from {From}", client);
            await Json(http, 401, Err("unauthorized", "that code is wrong, used or expired"));
            return;
        }
        _throttle.Clear(client);
        http.Response.Cookies.Append(Cookie, r.Token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = IsHttps(http), Path = "/", MaxAge = TimeSpan.FromDays(365),
        });
        _ctx.Logger.LogInformation("Phone: paired {Device} from {From}", r.Device["name"], client);
        _ctx.Events.Publish("phone.changed", new { });
        await Json(http, 200, new JsonObject { ["ok"] = true, ["device"] = PhoneAuth.Shown(r.Device) });
    }

    private async Task Rpc(HttpContext http, string method)
    {
        if (!Allowed.TryGetValue(method, out var keys)) { await Json(http, 403, Err("forbidden", $"{method} is not available from the phone")); return; }
        var body = await ReadJson(http);
        var args = new JsonObject();
        if (body is JsonObject o)
        {
            foreach (var kv in o)
            {
                if (Array.IndexOf(keys, kv.Key) < 0) { await Json(http, 400, Err("bad_request", $"{method} does not take “{kv.Key}” from the phone")); return; }
                args[kv.Key] = kv.Value?.DeepClone();
            }
        }
        else if (body is not null) { await Json(http, 400, Err("bad_request", "the body must be a JSON object")); return; }
        // A chat the phone starts is not the owner's own surface: the consult tool (the Claude CLI on the owner's
        // subscription, Anthropic's terms) reads meta.origin and refuses it, as it refuses webhooks, schedules and scripts.
        if (method == "sessions.create")
        {
            var meta = args["meta"] as JsonObject ?? new JsonObject();
            meta["origin"] ??= "phone";
            args["meta"] = meta;
        }
        try
        {
            var result = await _ctx.Rpc.InvokeAsync(method, args, http.RequestAborted).ConfigureAwait(false);
            await Json(http, 200, new JsonObject { ["result"] = NetPiJson.ToNode(result) });
        }
        catch (RpcException ex)
        {
            await Json(http, ex.Code switch { "not_found" => 404, "forbidden" => 403, "conflict" => 409, "too_large" => 413, "unavailable" => 503, "bad_request" => 400, _ => 500 }, Err(ex.Code, ex.Message));
        }
    }

    private async Task ServeFile(HttpContext http, string path)
    {
        if (!Files.TryGetValue(path, out var f)) { await Json(http, 404, Err("not_found", "no such page")); return; }
        var file = Path.Combine(_root, f.File);
        if (!File.Exists(file)) { await Json(http, 404, Err("not_found", "the phone page is not installed")); return; }
        http.Response.ContentType = f.Type;
        http.Response.Headers.CacheControl = "no-cache";
        if (f.File == "index.html")
            http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        if (http.Request.Method == "HEAD") return;
        await http.Response.SendFileAsync(file, http.RequestAborted).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- events

    private async Task Events(HttpContext http)
    {
        var res = http.Response;
        res.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";   // a reverse proxy must not hold the stream back
        var stream = new EventStream();
        var channel = stream.Channel;
        var id = Guid.NewGuid();
        lock (_streams) _streams[id] = stream;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _stopping);
        var ct = linked.Token;
        try
        {
            await res.WriteAsync(": ok\n\n", ct).ConfigureAwait(false);
            await res.Body.FlushAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (stream.TakeResync())
                {
                    while (channel.Reader.TryRead(out _)) { }   // the backlog is stale: the page re-reads everything on resync
                    await res.WriteAsync("data: {\"type\":\"resync\"}\n\n", ct).ConfigureAwait(false);
                    await res.Body.FlushAsync(ct).ConfigureAwait(false);
                }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tick.CancelAfter(TimeSpan.FromSeconds(15));
                string? line;
                try { line = await channel.Reader.ReadAsync(tick.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { line = null; }   // quiet: a heartbeat keeps proxies and the phone's radio from dropping it
                catch (ChannelClosedException) { break; }
                await res.WriteAsync(line is null ? ": hb\n\n" : "data: " + line + "\n\n", ct).ConfigureAwait(false);
                await res.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_streams) _streams.Remove(id); }
    }

    private void Relay(BusEvent e)
    {
        if (!Relayed.Contains(e.Type)) return;
        lock (_streams) if (_streams.Count == 0) return;
        string line;
        try { line = Frame(e).ToJsonString(); }
        catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Phone: could not frame {Type}", e.Type); return; }
        List<EventStream> all;
        lock (_streams) all = [.. _streams.Values];
        foreach (var c in all)
            c.Offer(line);   // a client too slow for its backlog gets a resync frame instead of a gap
    }

    /// <summary>One phone's event queue. Full: the publisher never waits; the stream is marked and the writer loop drops the backlog and sends the resync frame itself.</summary>
    internal sealed class EventStream
    {
        private int _resync;
        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        public void Offer(string line) { if (!Channel.Writer.TryWrite(line)) Interlocked.Exchange(ref _resync, 1); }
        public bool TakeResync() => Interlocked.Exchange(ref _resync, 0) == 1;
    }

    /// <summary>One event as the phone gets it. A stored message is only announced (its session and seq): the page reads the message itself, so an image never rides the stream.</summary>
    internal static JsonObject Frame(BusEvent e)
    {
        // the Agents view re-reads work.snapshot on these: the bodies (every agent, every process) would only cost the phone's radio
        if (e.Type is "agents.changed" or "process.started" or "process.exited" or "usage.changed") return new JsonObject { ["type"] = e.Type, ["sessionId"] = e.SessionId };
        var node = NetPiJson.ToNode(e.Data);
        if (e.Type is "message.added" or "message.updated" && node is JsonObject d)
        {
            var m = d["message"] as JsonObject;
            node = new JsonObject { ["sessionId"] = d["sessionId"]?.DeepClone(), ["seq"] = m?["seq"]?.DeepClone(), ["role"] = m?["role"]?.DeepClone() };
        }
        return new JsonObject { ["type"] = e.Type, ["sessionId"] = e.SessionId, ["data"] = node };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The address a request counts as: the connection's, or — from an allowed proxy, with <c>phone.trustProxy</c> — the last hop the proxy itself wrote into X-Forwarded-For.</summary>
    internal string ClientAddress(HttpContext http, IPAddress? peer)
    {
        if (_trustProxy() && http.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } xff)
        {
            var last = xff.Split(',')[^1].Trim();
            if (IPAddress.TryParse(last, out var ip)) return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        }
        return peer is null ? "?" : (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString();
    }

    private bool IsHttps(HttpContext http) =>
        http.Request.IsHttps || (_trustProxy() && string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase));

    /// <summary>A browser's POST names its origin: it must be the host the request came to. (The cookie is SameSite=Strict as well; this is the second lock.)</summary>
    private bool SameOrigin(HttpContext http)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var host = (_trustProxy() && http.Request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } fh ? fh.Split(',')[0] : http.Request.Host.Value ?? "").Trim();
        return string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase)
            || (uri.IsDefaultPort && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonNode?> ReadJson(HttpContext http)
    {
        if (http.Request.ContentLength is { } n && n > MaxBody) throw new BadHttpRequestException("too large");
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBody) throw new BadHttpRequestException("too large");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return null;
        try { return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)); }
        catch (JsonException) { return null; }
    }

    private static async Task Json(HttpContext http, int status, JsonObject body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(body.ToJsonString(), http.RequestAborted).ConfigureAwait(false);
    }

    private static JsonObject Err(string code, string message) => new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
