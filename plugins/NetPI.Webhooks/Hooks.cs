using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Webhooks;

/// <summary>
/// Hooks: actions NetPI itself runs at fixed points of a webhook's life, so a notification never depends on the agent remembering to
/// send it. A hook is <c>{ on, when?, afterMinutes?, once?, do }</c>:
/// <list type="bullet">
/// <item><c>received</c> — every payload that arrives (before any filter or grouping): the pre-hook; an immediate alert for the severe ones, a "clear" for a recovery.</item>
/// <item><c>started</c> — the incident's chat was started.</item>
/// <item><c>finished</c> — the chat's run ended (after the first alert, and again after each follow-up that woke it): the post-hook, with the agent's final answer and its parsed verdict.</item>
/// <item><c>asking</c> — the chat is waiting for the user (<c>ask_user</c>).</item>
/// <item><c>stalled</c> — the chat has been running for <c>afterMinutes</c> (default 10) without finishing.</item>
/// <item><c>failed</c> — the alerts could not be turned into a chat (the retries ran out, the project or profile is gone).</item>
/// </list>
/// <c>when</c> is a list of match conditions over the hook's context: the alert's own fields (<c>host</c>, <c>state</c>…) plus <c>event</c>,
/// <c>key</c>, <c>count</c>, <c>sessionId</c>, <c>title</c>, <c>status</c> (ok|error|no-answer), <c>verdict</c>, <c>result</c> and <c>summary</c>.
/// The verdict is the last <c>VERDICT: word</c> line of the final answer (<c>unknown</c> when there is none), so a gate fails open: a hook
/// that says "when the verdict is not noise" also fires when the agent said nothing, died, or stalled. <c>once</c> names a key that fires at most once
/// per incident (so a "notify" reached from two places notifies once). <c>do</c> is an <c>mqtt</c> publish or an <c>http</c> request, templated
/// like a prompt; a <c>json</c> object is serialised after its strings are expanded, so no value can break the JSON.
/// </summary>
internal sealed class HookEngine(IPluginContext ctx, WebhookStore store, TimeProvider time)
{
    public static readonly string[] Events = ["received", "started", "finished", "asking", "stalled", "failed"];
    public const int MaxHooks = 20, DefaultStalledMinutes = 10;
    private static readonly Regex VerdictLine = new(@"^\s*VERDICT:\s*([A-Za-z][A-Za-z_-]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // ---------------------------------------------------------------- validation (when a webhook is saved)

    public static JsonArray Validate(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray list) throw new RpcException("bad_request", "hooks must be an array of { on, when?, once?, do }");
        if (list.Count > MaxHooks) throw new RpcException("bad_request", $"at most {MaxHooks} hooks");
        var clean = new JsonArray();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject h) throw new RpcException("bad_request", $"hook {i + 1} must be an object");
            var what = $"hook {i + 1}";
            var on = h["on"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (on is null || !Events.Contains(on)) throw new RpcException("bad_request", $"{what}: on must be one of {string.Join(", ", Events)}");
            var allowed = new HashSet<string> { "on", "when", "once", "do", "afterMinutes", "name" };
            if (h.Select(kv => kv.Key).FirstOrDefault(k => !allowed.Contains(k)) is { } unknown) throw new RpcException("bad_request", $"{what}: unknown field “{unknown}”");
            var c = new JsonObject { ["on"] = on };
            if (h["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name) c["name"] = Template.Clip(name, 80);
            if (h["when"] is not null) c["when"] = Template.ValidateConditions(h["when"]);
            if (h["once"]?.GetValue<string>()?.Trim() is { Length: > 0 } once) c["once"] = Template.Clip(once, 50);
            if (h["afterMinutes"] is not null)
            {
                if (on != "stalled") throw new RpcException("bad_request", $"{what}: afterMinutes only belongs to a stalled hook");
                if (h["afterMinutes"] is not JsonValue v || !v.TryGetValue<int>(out var m) || m < 1 || m > 1440) throw new RpcException("bad_request", $"{what}: afterMinutes must be 1–1440");
                c["afterMinutes"] = m;
            }
            c["do"] = ValidateAction(h["do"], what);
            clean.Add(c);
        }
        return clean;
    }

    private static JsonObject ValidateAction(JsonNode? node, string what)
    {
        if (node is not JsonObject a) throw new RpcException("bad_request", $"{what}: do must be an object with a type of mqtt or http");
        var type = a["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var clean = new JsonObject { ["type"] = type };
        switch (type)
        {
            case "mqtt":
                clean["topic"] = a["topic"]?.GetValue<string>()?.Trim() is { Length: > 0 } t ? t : throw new RpcException("bad_request", $"{what}: an mqtt action needs a topic");
                Body(a, clean, what);
                if (a["retain"] is JsonValue r && r.TryGetValue<bool>(out var retain)) clean["retain"] = retain;
                break;
            case "http":
                var url = a["url"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    throw new RpcException("bad_request", $"{what}: an http action needs an http(s) url");
                clean["url"] = url;
                var method = a["method"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "POST";
                clean["method"] = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" ? method : throw new RpcException("bad_request", $"{what}: method must be GET, POST, PUT, PATCH or DELETE");
                if (a["headers"] is JsonObject hd) clean["headers"] = hd.DeepClone();
                Body(a, clean, what, optional: true);
                break;
            default:
                throw new RpcException("bad_request", $"{what}: do.type must be mqtt or http");
        }
        return clean;
    }

    private static void Body(JsonObject a, JsonObject clean, string what, bool optional = false)
    {
        if (a["json"] is JsonObject j) clean["json"] = j.DeepClone();
        else if (a["text"]?.GetValue<string>() is { } text) clean["text"] = text;
        else if (!optional) throw new RpcException("bad_request", $"{what}: give the payload as json (an object of templates) or text");
    }

    // ---------------------------------------------------------------- the verdict

    /// <summary>The last <c>VERDICT: word</c> line of an answer, lower-cased, else <c>unknown</c>. A deterministic read of a line the prompt asks for: a missing one is "unknown", never a guess.</summary>
    public static string Verdict(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return "unknown";
        string? last = null;
        try { foreach (Match m in VerdictLine.Matches(answer)) last = m.Groups[1].Value; }
        catch (RegexMatchTimeoutException) { return "unknown"; }
        return last?.ToLowerInvariant() ?? "unknown";
    }

    /// <summary>The answer without its verdict line, collapsed to one clipped paragraph: what a notification can carry.</summary>
    public static string Summary(string? answer, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(answer)) return "";
        string cleaned;
        try { cleaned = VerdictLine.Replace(answer, ""); } catch (RegexMatchTimeoutException) { cleaned = answer; }
        return Template.Clip(Regex.Replace(cleaned.Trim(), @"\s+", " "), max);
    }

    // ---------------------------------------------------------------- running hooks

    /// <summary>
    /// Run the hooks of <paramref name="webhook"/> for <paramref name="evt"/>. <paramref name="context"/> is mutated (event is set).
    /// <paramref name="fired"/> is the incident's once-keys (null for a payload with no incident); true when it changed.
    /// A failing action is logged and recorded in the history, and never stops the others or the webhook.
    /// </summary>
    public async Task<bool> FireAsync(JsonObject webhook, string evt, JsonObject context, JsonArray? fired, string? sessionId, string? incidentKey, CancellationToken ct)
    {
        if (webhook["hooks"] is not JsonArray hooks || hooks.Count == 0) return false;
        context["event"] = evt;
        var changed = false;
        var id = webhook["id"]!.GetValue<string>();
        for (var i = 0; i < hooks.Count; i++)
        {
            if (hooks[i] is not JsonObject h || h["on"]?.GetValue<string>() != evt) continue;
            if (evt == "stalled")
            {
                var after = h["afterMinutes"] is JsonValue av && av.TryGetValue<int>(out var am) ? am : DefaultStalledMinutes;
                if (context["elapsedMinutes"] is not JsonValue ev || !ev.TryGetValue<double>(out var elapsed) || elapsed < after) continue;
            }
            if (h["when"] is JsonArray when && Template.FailedCondition(when, context) is not null) continue;
            // a stalled or asking hook fires once per wait for an answer (the incident's awaiting period), whatever else it says
            var once = h["once"]?.GetValue<string>() ?? (evt is "stalled" or "asking" ? $"{evt}#{i}@{Template.Show(context["awaitingSince"])}" : null);
            if (once is not null && fired is not null && fired.Any(f => f?.GetValue<string>() == once)) continue;
            var name = h["name"]?.GetValue<string>() ?? $"{evt} #{i + 1}";
            string status, reason;
            try
            {
                var detail = await RunAsync(h["do"] as JsonObject ?? new JsonObject(), context, ct).ConfigureAwait(false);
                status = WebhookStore.Hook;
                reason = $"{name}: {detail}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Logger.LogWarning(ex, "Webhook {Webhook}: hook {Hook} failed", id, name);
                status = WebhookStore.Hook;
                reason = $"{name}: FAILED — {ex.Message}";
            }
            if (once is not null && fired is not null) { fired.Add(once); changed = true; }
            store.Record(id, time.GetUtcNow(), status, reason, sessionId, incidentKey, Template.Clip(Template.Show(context["title"]) is { Length: > 0 } t ? t : evt, 160), null, touch: false);
            ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });
        }
        return changed;
    }

    /// <summary>Run one action with a context; the one-line result. Used by the hooks and by <c>webhooks.testHook</c>.</summary>
    public async Task<string> RunAsync(JsonObject action, JsonObject context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        switch (action["type"]?.GetValue<string>())
        {
            case "mqtt":
            {
                var host = ctx.Settings.Get("webhooks.mqtt.host", "")?.Trim();
                if (string.IsNullOrEmpty(host)) throw new InvalidOperationException("webhooks.mqtt.host is not set");
                var topic = Template.Expand(action["topic"]!.GetValue<string>(), context, now);
                var body = Payload(action, context, now);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await Mqtt.PublishAsync(host, Math.Clamp(ctx.Settings.Get("webhooks.mqtt.port", 1883), 1, 65535), ctx.Settings.Get("webhooks.mqtt.user", ""),
                    ctx.Settings.Get("webhooks.mqtt.password", ""), topic, Encoding.UTF8.GetBytes(body ?? ""), action["retain"] is JsonValue r && r.TryGetValue<bool>(out var retain) && retain, timeout.Token).ConfigureAwait(false);
                return $"mqtt {topic} published";
            }
            case "http":
            {
                var url = Template.Expand(action["url"]!.GetValue<string>(), context, now);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException($"“{url}” is not an http(s) url");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new HttpRequestMessage(new HttpMethod(action["method"]?.GetValue<string>() ?? "POST"), uri);
                if (action["headers"] is JsonObject headers)
                    foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, Template.Expand(Template.Show(v), context, now));
                if (Payload(action, context, now) is { } body)
                    request.Content = new StringContent(body, Encoding.UTF8, action["json"] is not null ? "application/json" : "text/plain");
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{request.Method} {uri.Host}{uri.AbsolutePath} answered HTTP {(int)response.StatusCode}");
                return $"{request.Method} {uri.Host}{uri.AbsolutePath} → {(int)response.StatusCode}";
            }
            default:
                throw new InvalidOperationException("unknown action type");
        }
    }

    /// <summary>The action's body: its <c>json</c> object with every string expanded and serialised, else its <c>text</c> expanded, else null.</summary>
    private static string? Payload(JsonObject action, JsonObject context, DateTimeOffset now)
    {
        if (action["json"] is JsonNode json) return ExpandJson(json, context, now)?.ToJsonString();
        return action["text"]?.GetValue<string>() is { } text ? Template.Expand(text, context, now) : null;
    }

    private static JsonNode? ExpandJson(JsonNode? node, JsonObject context, DateTimeOffset now) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, ExpandJson(kv.Value, context, now)))),
        JsonArray a => new JsonArray([.. a.Select(x => ExpandJson(x, context, now))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Template.Expand(s, context, now)),
        _ => node.DeepClone(),
    };
}

/// <summary>A minimal MQTT 3.1.1 publisher (QoS 0): connect, publish, disconnect. Enough for a Home Assistant alert topic; no subscriptions, no TLS.</summary>
internal static class Mqtt
{
    public static async Task PublishAsync(string host, int port, string? user, string? password, string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var stream = client.GetStream();

        var flags = (byte)(0x02 | (string.IsNullOrEmpty(user) ? 0 : 0x80) | (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? 0 : 0x40));
        var connect = new List<byte>();
        connect.AddRange(Str("MQTT"));
        connect.Add(4);                    // protocol level 3.1.1
        connect.Add(flags);
        connect.Add(0); connect.Add(30);   // keep alive
        connect.AddRange(Str("netpi-webhooks-" + Guid.NewGuid().ToString("N")[..8]));
        if (!string.IsNullOrEmpty(user)) connect.AddRange(Str(user));
        if ((flags & 0x40) != 0) connect.AddRange(Str(password!));
        await stream.WriteAsync(Packet(0x10, connect), ct).ConfigureAwait(false);

        var ack = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(ack.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("the broker closed the connection");
            read += n;
        }
        if (ack[0] != 0x20 || ack[3] != 0) throw new IOException(ack[3] switch { 4 or 5 => "the broker refused the credentials", var c => $"the broker refused the connection (code {c})" });

        var publish = new List<byte>();
        publish.AddRange(Str(topic));
        publish.AddRange(payload);
        await stream.WriteAsync(Packet((byte)(0x30 | (retain ? 1 : 0)), publish), ct).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 0xE0, 0x00 }, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return [(byte)(b.Length >> 8), (byte)(b.Length & 0xFF), .. b];
    }

    private static byte[] Packet(byte header, List<byte> body)
    {
        var packet = new List<byte> { header };
        var n = body.Count;
        do { var d = (byte)(n % 128); n /= 128; packet.Add((byte)(n > 0 ? d | 0x80 : d)); } while (n > 0);
        packet.AddRange(body);
        return [.. packet];
    }
}
