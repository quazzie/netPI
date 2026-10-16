using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.Webhooks;

/// <summary>
/// Inbound webhooks (idea-wmpsrw, from the Checkmk rollout): another system POSTs to <c>/hook/{id}</c> on the plugin's own
/// receiver (<see cref="WebhookListener"/>) with the webhook's bearer token, and the payload starts a fresh chat on a project —
/// on a named agent, <c>any</c> agent with a free instance, the first <c>free</c>-priced agent, or the first of a list — with a
/// prompt filled in from the payload. Payloads are grouped into conversations by a key the Before command names (say the host): the first starts its chat at once,
/// and later ones are appended to the chat that handles the incident — nothing is dropped, and a storm becomes one conversation per incident instead of a pile of chats.
/// NetPI holds no payload back to group it: a sender that wants a batch groups its own alerts before it posts them. The receiver is off until <c>webhooks.enabled</c>, listens on <c>webhooks.bind</c>:<c>webhooks.port</c> and
/// answers only <c>webhooks.allowFrom</c>. Starting a run is <see cref="WebhookRunner"/>; the webhooks are in <see cref="WebhookStore"/>.
/// </summary>
[NetPiPlugin("netpi.webhooks", Name = "Webhooks", Description = "Inbound webhooks: an authenticated POST from another system starts a fresh chat on a project, on a chosen agent, any free agent or the first free-priced one", Order = 97)]
public sealed class WebhooksPlugin : INetPiPlugin
{
    private readonly TimeProvider _time;
    private readonly bool _listen;
    private IPluginContext? _ctx;
    private WebhookListener? _listener;
    private string? _listenerKey;
    private string? _listenerError;
    private AddressList _allow = new([]);
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private FailureThrottle? _throttle;
    private CancellationTokenSource? _life;
    private readonly List<Task> _reconciles = [];   // the reconciles in flight; a stop waits for them, bounded
    private readonly bool _loop;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _stop;
    private Task? _worker;

    internal WebhookStore? Store { get; private set; }
    internal WebhookRunner? Runner { get; private set; }

    public WebhooksPlugin() : this(TimeProvider.System, listen: true, loop: true) { }

    /// <summary>A test's clock, no background loop (the test calls <see cref="WebhookRunner.TickAsync"/>) and no network listener unless the test enables the setting.</summary>
    internal WebhooksPlugin(TimeProvider time, bool listen, bool loop = false)
    {
        _time = time;
        _listen = listen;
        _loop = loop;
    }

    public async Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        ctx.Services.Register(new SettingsSection
        {
            Id = "webhooks", Title = "Webhooks", Group = "Agents", Order = 61,
            Help = "A system that can POST (a monitoring tool, a CI, a script) starts a chat here. The receiver is its own small listener, separate from NetPI's API, and off until you switch it on. Each webhook has its own token (shown once).",
            Settings =
            [
                SettingInfo.Bool("webhooks.enabled", "Receive webhooks", false, "Off: the receiver does not listen at all."),
                SettingInfo.Str("webhooks.bind", "Listen on", "127.0.0.1", "The address to listen on: 127.0.0.1 (this PC only), this PC's LAN address, or 0.0.0.0 (every interface). Anything but 127.0.0.1 needs a firewall rule for the port.", "127.0.0.1"),
                SettingInfo.Int("webhooks.port", "Port", 7432, "The receiver's port (not NetPI's own).", 1, 65535),
                SettingInfo.List("webhooks.allowFrom", "Accept requests from", ["127.0.0.1/32", "::1/128"], "Addresses or CIDR ranges that may call the receiver, e.g. 192.168.1.2/32 for one server. Everything else is refused before the token is looked at.", "192.168.1.2/32"),
                SettingInfo.Int("webhooks.maxBodyKb", "Largest payload", 256, "Bigger requests are refused.", 1, 1024, "KB"),
            ],
        });
        var store = Store = new WebhookStore(ctx.Data);
        var runner = Runner = new WebhookRunner(ctx, store, _time);
        _throttle = new FailureThrottle(_time);
        Register(ctx, store, runner);
        ctx.Tools.Register(new WebhooksTool(ctx));
        ctx.Ui.AddTab(new UiTabInfo
        {
            Id = "webhooks", Title = "Webhooks", Panel = UiPanel.Left, Order = 26, Module = "ui.js",
            Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3v6'/><circle cx='12' cy='11' r='2'/><path d='M10.5 12.5 6 19'/><circle cx='5' cy='20' r='2'/><path d='M13.5 12.5 18 19'/><circle cx='19' cy='20' r='2'/></svg>",
        });
        // a sync handler that only schedules the work: the bus keeps no task of ours in its worker between events (a plugin's async handler
        // left its last task, and with it the plugin's load context, rooted until the subscription ended)
        var life = _life = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        var lifeToken = life.Token;
        ctx.Events.Subscribe("settings.changed", e => Reconcile(lifeToken));
        // The receiver binds in the background. On a hot reload the host starts this generation BEFORE it stops the previous one, which
        // still holds the port: waiting for the bind here would wait for a stop that only comes after StartAsync returns (the port stayed
        // down until someone toggled the setting). The allow list is read first, so a request never meets an empty one.
        ApplyAllowList();
        Reconcile(lifeToken);
        if (_loop)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
            _worker = LoopAsync(ctx, store, runner, _stop.Token);
        }
    }

    /// <summary>
    /// A reconcile in the background, observed: a failure is logged (a bind the previous generation still holds, a setting that does
    /// not parse), and <see cref="StopAsync"/> waits for the ones it has started, so none of them outlives the plugin's stopping token.
    /// </summary>
    private void Reconcile(CancellationToken ct)
    {
        var task = Task.Run(async () =>
        {
            try { await ReconcileAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx?.Logger.LogWarning(ex, "Webhooks: the receiver did not come up"); }
        });
        lock (_reconciles)
        {
            _reconciles.RemoveAll(t => t.IsCompleted);
            _reconciles.Add(task);
        }
    }

    /// <summary>Tries again the chats that are waiting to start when they are due; sleeps until the soonest one (at most 30 s), or until a payload starts waiting.</summary>
    private async Task LoopAsync(IPluginContext ctx, WebhookStore store, WebhookRunner runner, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _time, ct).ConfigureAwait(false);   // let startup settle first
            while (!ct.IsCancellationRequested)
            {
                try { await runner.TickAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogError(ex, "Webhooks: a tick failed"); }
                var wait = TimeSpan.FromSeconds(store.AnyAwaiting() ? 10 : 30);   // watching a chat for its finished / asking / timeout hooks needs a closer look
                if (store.NextDue() is { } next) wait = TimeSpan.FromTicks(Math.Clamp((next - _time.GetUtcNow()).Ticks, TimeSpan.TicksPerSecond, wait.Ticks));
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _life?.Cancel();   // ends a bind that is still waiting for the previous generation's port, which would otherwise hold _reconcile
        _stop?.Cancel();
        // the reconciles started by a settings change: wait for them, bounded — a bind held up by the previous generation's port ends
        // with the cancellation above, and a stuck one must not hold the stop (and the port) forever
        Task[] reconciles;
        lock (_reconciles) { reconciles = _reconciles.ToArray(); _reconciles.Clear(); }
        try { await Task.WhenAll(reconciles).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { _ctx?.Logger.LogWarning("Webhooks: a receiver reconcile was still running when the plugin stopped; it is left to end on its own"); }
        if (_worker is { } w) { try { await w.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        await _reconcile.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await CloseListenerAsync().ConfigureAwait(false); }
        finally { _reconcile.Release(); }
        _life?.Dispose();
        _stop?.Dispose();
    }

    // ---------------------------------------------------------------- the receiver

    /// <summary>Bring the listener in line with the settings: started, stopped or moved. A failure to bind is kept for <c>webhooks.status</c>, not thrown into the host.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        if (_ctx is not { } ctx) return;
        await _reconcile.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var enabled = _listen && ctx.Settings.Get("webhooks.enabled", false);
            var bind = ctx.Settings.Get("webhooks.bind", "127.0.0.1")?.Trim() ?? "127.0.0.1";
            var port = Math.Clamp(ctx.Settings.Get("webhooks.port", 7432), 1, 65535);
            var allow = ApplyAllowList();
            var key = enabled ? $"{bind}:{port}" : null;
            if (key == _listenerKey && (key is null || _listener is not null)) return;
            await CloseListenerAsync().ConfigureAwait(false);
            _listenerError = null;
            if (key is null) return;
            if (!IPAddress.TryParse(bind, out var address)) { _listenerError = $"“{bind}” is not an IP address"; ctx.Logger.LogWarning("Webhooks: {Error}", _listenerError); return; }
            try
            {
                _listener = await WebhookListener.StartAsync(address, port, HandleAsync, ctx.Logger, ct).ConfigureAwait(false);
                _listenerKey = key;
                ctx.Logger.LogInformation("Webhooks: receiving on http://{Address}:{Port}/hook/{{id}} from {Allow}", address, _listener.Port, string.Join(", ", allow));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _listenerError = ex.Message;
                ctx.Logger.LogWarning(ex, "Webhooks: could not listen on {Address}:{Port}", address, port);
            }
        }
        finally
        {
            _reconcile.Release();
            try { ctx.Events.Publish(WebhookRunner.ChangedEvent, new { }); } catch (ObjectDisposedException) { /* the plugin is being unloaded */ }
        }
    }

    /// <summary>Read <c>webhooks.allowFrom</c> into the list requests are checked against; the entries as written (for the log).</summary>
    private List<string> ApplyAllowList()
    {
        var ctx = _ctx!;
        var allow = ctx.Settings.GetNode("webhooks.allowFrom") is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToList() : ["127.0.0.1/32", "::1/128"];
        _allow = new AddressList(allow);
        if (_allow.Rejected.Count > 0) ctx.Logger.LogWarning("Webhooks: ignoring unreadable allow-list entries {Entries}", string.Join(", ", _allow.Rejected));
        return allow;
    }

    private async Task CloseListenerAsync()
    {
        var l = _listener;
        if (l is null) { _listenerKey = null; return; }
        try { await l.DisposeAsync().ConfigureAwait(false); }
        finally { _listener = null; _listenerKey = null; }   // "listening" stays true until the socket is really closed
    }

    /// <summary>The receiver's state as <c>webhooks.status</c> shows it.</summary>
    internal JsonObject Status() => new()
    {
        ["enabled"] = _ctx?.Settings.Get("webhooks.enabled", false) ?? false,
        ["listening"] = _listener is not null,
        ["address"] = _listener?.Address,
        ["port"] = _listener?.Port,
        ["error"] = _listenerError,
        ["url"] = _listener is null ? null : $"http://{_listener.Address}:{_listener.Port}/hook/",
    };

    internal async Task<(int Status, JsonObject Body)> ReceiveAsync(string path, string method, IPAddress? remote, string? contentType,
        Func<int, Task<string?>> readBody, string? bearer, CancellationToken ct)
    {
        var ctx = _ctx!;
        var from = remote?.IsIPv4MappedToIPv6 == true ? remote.MapToIPv4().ToString() : remote?.ToString() ?? "?";
        if (!_allow.Contains(remote)) return (403, Err("forbidden", "this address may not call the receiver (webhooks.allowFrom)"));
        if (path.TrimEnd('/').Equals("/health", StringComparison.OrdinalIgnoreCase))
            return method == "GET" ? (200, new JsonObject { ["ok"] = true }) : (405, Err("method_not_allowed", "GET only"));
        var parts = path.Trim('/').Split('/');
        if (parts.Length != 2 || !parts[0].Equals("hook", StringComparison.OrdinalIgnoreCase)) return (404, Err("not_found", "no such path"));
        if (method != "POST") return (405, Err("method_not_allowed", "POST only"));
        if (_throttle!.Blocked(from)) return (429, Err("too_many_failures", "too many wrong tokens from this address; try again later"));

        var hook = Store!.Resolve(parts[1]);
        if (hook is null || !WebhookStore.TokenMatches(hook, bearer ?? ""))
        {
            _throttle.Fail(from);
            ctx.Logger.LogWarning("Webhooks: refused a request from {From} for {Id} (unknown webhook or wrong token)", from, parts[1]);
            return (401, Err("unauthorized", "unknown webhook or wrong token"));
        }
        _throttle.Clear(from);
        var maxBytes = Math.Clamp(ctx.Settings.Get("webhooks.maxBodyKb", 256), 1, 1024) * 1024;
        var text = await readBody(maxBytes).ConfigureAwait(false);
        if (text is null) return (413, Err("too_large", $"the payload is larger than {maxBytes / 1024} KB (webhooks.maxBodyKb)"));
        var payload = Body.Payload(text, contentType);
        var result = await Runner!.HandleAsync(hook, payload, from, ct).ConfigureAwait(false);
        if (result.Status == WebhookStore.Waiting) _wake.Release();
        var body = new JsonObject { ["status"] = result.Status, ["reason"] = result.Reason, ["sessionId"] = result.SessionId };
        return result.Status switch
        {
            WebhookStore.Started or WebhookStore.Waiting or WebhookStore.Appended => (202, body),   // accepted: a chat has it, or it waits for its chat to start
            WebhookStore.Skipped or WebhookStore.Duplicate => (200, body),
            _ => (500, body),
        };
    }

    private async Task HandleAsync(HttpContext http)
    {
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.ContentType = "application/json";
        (int Status, JsonObject Body) answer;
        try
        {
            string? bearer = null;
            var auth = http.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) bearer = auth[7..].Trim();
            else if (http.Request.Headers.TryGetValue("X-Webhook-Token", out var h)) bearer = h.ToString().Trim();
            answer = await ReceiveAsync(http.Request.Path.Value ?? "/", http.Request.Method, http.Connection.RemoteIpAddress, http.Request.ContentType,
                max => Body.ReadAsync(http.Request, max, http.RequestAborted), bearer, http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _ctx?.Logger.LogError(ex, "Webhooks: a request failed");
            answer = (500, Err("error", "the receiver failed; see NetPI's log"));
        }
        http.Response.StatusCode = answer.Status;
        await http.Response.WriteAsync(answer.Body.ToJsonString()).ConfigureAwait(false);
    }

    private static JsonObject Err(string code, string message) => new() { ["error"] = code, ["message"] = message };

    // ---------------------------------------------------------------- RPC

    private void Changed(IPluginContext ctx, string? id) => ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });

    private void Register(IPluginContext ctx, WebhookStore store, WebhookRunner runner)
    {
        const string Doc = "Webhook: { id, name, slug?, enabled, path (\"/hook/<slug or id>\"), tokenHint, projectId?, agent?, priority, profile?, prompt, pre? / post? { command, args?, timeoutSeconds? }, timeoutMinutes, maxChatsPerHour, proposedBy?, lastAt?, lastStatus?, lastReason?, lastSessionId? }";

        ctx.Rpc.Register(new RpcMethod("webhooks.status", "The receiver: { enabled, listening, address?, port?, url?, error? }", ReadOnly: true, []),
            (_, _) => Task.FromResult<object?>(Status()));

        ctx.Rpc.Register(new RpcMethod("webhooks.list", "The webhooks, oldest first: { projectId? } → Webhook[]. " + Doc, ReadOnly: true,
            [RpcParam.Opt("projectId", RpcParamType.String, "only this project's")]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.List(req.Str("projectId")).Select(d => (JsonNode)Shown(d))])));

        ctx.Rpc.Register(new RpcMethod("webhooks.get", "One webhook: { id } → Webhook", ReadOnly: true, [RpcParam.Req("id")]),
            (req, _) => Task.FromResult<object?>(Shown(Require(store, req.Required("id")))));

        ctx.Rpc.Register(new RpcMethod("webhooks.history", "Deliveries, newest first: { id?, limit? (50) } → { webhookId, at, status (waiting|started|appended|duplicate|skipped|failed), reason?, sessionId?, key?, summary?, from? }[]. waiting: its conversation's chat cannot start yet (tried again every 30 s for 30 min); started: a chat began; appended: added to the incident's open chat; duplicate: that chat was just told the same", ReadOnly: true,
            [RpcParam.Opt("id", RpcParamType.String, "one webhook's; all when omitted"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.History(req.Str("id"), Math.Clamp(req.Int("limit") ?? 50, 1, 500)).Select(d => (JsonNode)d)])));

        ctx.Rpc.Register(new RpcMethod("webhooks.incidents", "Incidents, most recent first: { id? } → { webhookId, key, state (waiting|open), sessionId?, alertCount, openedAt?, lastAt, verdict? (the agent's last VERDICT: line), awaiting? (the chat is being watched for its finished hook), startsAt? (the next try), waiting? (why its chat has not started yet) }[]. waiting: the chat has not started (no agent, the model not loaded, the fuse) and its payloads wait with it; open: the chat that handles the key", ReadOnly: true,
            [RpcParam.Opt("id", RpcParamType.String, "one webhook's; all when omitted")]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.Incidents(req.Str("id")).Select(i => (JsonNode)ShownIncident(i))])));

        ctx.Rpc.Register(new RpcMethod("webhooks.add", "Add a webhook: { name, slug? (the readable name in its URL, /hook/<slug>; default: the name, lower-cased; the id path always works too), prompt, projectId?, pre? / post? ({ command, args?, timeoutSeconds? } — a program NetPI runs with no shell and a JSON context on standard input. pre (the Before command) runs for every payload and may answer with JSON on stdout: skip, start (false = only join an open conversation), key (the conversation: the same key joins the chat that handles it), text (how the item reads in the prompt), title; post (the After command) runs when the chat finished, asked, timed out or could not start; see PROTOCOL.md), timeoutMinutes? (10, 0 = never: when post is told the chat is still running), profile? (a profile id from profiles.list: the chat's tools and system prompt; without one it gets the automatic profile, what a new chat gets: name a profile when the chat needs particular tools), agent? (a key, \"any\" = whichever agent has a free instance first, \"free\" = the first free-priced one, or \"a,b\" = the first of these that is available), priority? (low | normal (the default) | high | critical: the order its chats queue in while their agent is busy; a critical chat takes the instance of a lower chat that was started for the user, between two of that chat's model calls), maxChatsPerHour? (60, 0 = no limit: a fuse, not a quota), enabled? (true), proposedBy? } → Webhook plus token (shown once). The prompt is a template: {{a.b.0.c}} fields of the payload, {{payload}}, {{now}}, {{alerts}} (every item of the batch, as the Before command's text or the payload JSON), {{count}}, {{key}}. proposedBy stores it paused, as a proposal the user enables",
            Params:
            [
                RpcParam.Req("name"), RpcParam.Req("prompt"), RpcParam.Opt("slug"), RpcParam.Opt("projectId"), RpcParam.Opt("agent"), RpcParam.Opt("priority", RpcParamType.String, "low | normal | high | critical"), RpcParam.Opt("profile"), RpcParam.Opt("pre", RpcParamType.Object), RpcParam.Opt("post", RpcParamType.Object), RpcParam.Opt("timeoutMinutes", RpcParamType.Integer),
                RpcParam.Opt("maxChatsPerHour", RpcParamType.Integer),
                RpcParam.Opt("enabled", RpcParamType.Boolean), RpcParam.Opt("proposedBy"),
            ]),
            async (req, ct) =>
            {
                var now = _time.GetUtcNow();
                var token = WebhookStore.NewToken();
                var proposedBy = Proposer(req);
                var doc = new JsonObject
                {
                    ["id"] = Ids.New("whk"),
                    ["name"] = Name(req.Required("name")),
                    ["slug"] = SlugFor(store, null, req.Str("slug"), req.Required("name")),
                    ["enabled"] = proposedBy is null && (req.Bool("enabled") ?? true),
                    ["tokenHash"] = WebhookStore.Hash(token),
                    ["tokenHint"] = token[^4..],
                    ["projectId"] = await ProjectAsync(ctx, req.Str("projectId")).ConfigureAwait(false),
                    ["agent"] = await AgentAsync(ctx, req.Str("agent"), ct).ConfigureAwait(false),
                    ["priority"] = Priority(req.Str("priority") ?? "normal"),
                    ["profile"] = await ProfileAsync(ctx, req.Str("profile"), ct).ConfigureAwait(false),
                    ["prompt"] = Prompt(req.Required("prompt")),
                    ["pre"] = HookEngine.Validate(NetPiJson.ToNode(req.Prop("pre")), "pre"),
                    ["post"] = HookEngine.Validate(NetPiJson.ToNode(req.Prop("post")), "post"),
                    ["timeoutMinutes"] = Timeout(req.Int("timeoutMinutes") ?? HookEngine.DefaultTimeoutMinutes),
                    ["maxChatsPerHour"] = Fuse(req.Int("maxChatsPerHour") ?? WebhookRunner.DefaultMaxChatsPerHour),
                    ["createdAt"] = WebhookStore.Iso(now),
                    ["updatedAt"] = WebhookStore.Iso(now),
                };
                if (proposedBy is not null) doc["proposedBy"] = proposedBy;
                store.Put(doc);
                Changed(ctx, doc["id"]!.GetValue<string>());
                var shown = Shown(doc);
                shown["token"] = token;
                return shown;
            });

        ctx.Rpc.Register(new RpcMethod("webhooks.update", "Change a webhook: { id, name?, slug? (\"\" = only the id path), prompt?, pre? / post? ({} clears), timeoutMinutes?, projectId? (\"\" clears), agent? (\"\" clears), priority?, profile? (\"\" = automatic), maxChatsPerHour?, enabled?, proposedBy? } → Webhook. proposedBy (the webhooks tool sets it): a change of what runs pauses the webhook as a proposal for the user, and it cannot enable one",
            Params:
            [
                RpcParam.Req("id"), RpcParam.Opt("name"), RpcParam.Opt("slug"), RpcParam.Opt("prompt"), RpcParam.Opt("projectId"), RpcParam.Opt("agent"), RpcParam.Opt("priority", RpcParamType.String, "low | normal | high | critical"), RpcParam.Opt("profile"), RpcParam.Opt("pre", RpcParamType.Object), RpcParam.Opt("post", RpcParamType.Object), RpcParam.Opt("timeoutMinutes", RpcParamType.Integer),
                RpcParam.Opt("maxChatsPerHour", RpcParamType.Integer),
                RpcParam.Opt("enabled", RpcParamType.Boolean), RpcParam.Opt("proposedBy"),
            ]),
            async (req, ct) =>
            {
                var id = req.Required("id");
                Require(store, id);
                var project = req.Str("projectId") is { } p ? (p.Length == 0 ? null : await ProjectAsync(ctx, p).ConfigureAwait(false)) : null;
                var agent = req.Str("agent") is { } a ? (a.Length == 0 ? null : await AgentAsync(ctx, a, ct).ConfigureAwait(false)) : null;
                var profile = req.Str("profile") is { } pf ? (pf.Length == 0 ? null : await ProfileAsync(ctx, pf, ct).ConfigureAwait(false)) : null;
                var priority = req.Str("priority") is { } pr ? Priority(pr) : null;
                var pre = req.Prop("pre") is not null ? HookEngine.Validate(NetPiJson.ToNode(req.Prop("pre")), "pre") : null;
                var post = req.Prop("post") is not null ? HookEngine.Validate(NetPiJson.ToNode(req.Prop("post")), "post") : null;
                var updated = store.Change(id, doc =>
                {
                    var now = _time.GetUtcNow();
                    var changesWhatRuns = false;
                    if (req.Str("name") is { } name) doc["name"] = Name(name);
                    if (req.Str("slug") is { } slug) { doc["slug"] = slug.Trim().Length == 0 ? null : SlugFor(store, id, slug, null); changesWhatRuns = true; }
                    if (req.Str("prompt") is { } prompt) { doc["prompt"] = Prompt(prompt); changesWhatRuns = true; }
                    if (req.Str("projectId") is not null) { doc["projectId"] = project; changesWhatRuns = true; }
                    if (req.Str("agent") is not null) { doc["agent"] = agent; changesWhatRuns = true; }
                    if (priority is not null) { doc["priority"] = priority; changesWhatRuns = true; }
                    if (req.Str("profile") is not null) { doc["profile"] = profile; changesWhatRuns = true; }
                    if (req.Prop("pre") is not null) { doc["pre"] = pre; changesWhatRuns = true; }
                    if (req.Prop("post") is not null) { doc["post"] = post; changesWhatRuns = true; }
                    if (req.Int("timeoutMinutes") is { } timeout) { doc["timeoutMinutes"] = Timeout(timeout); changesWhatRuns = true; }
                    if (req.Int("maxChatsPerHour") is { } fuse) doc["maxChatsPerHour"] = Fuse(fuse);
                    var enabled = req.Bool("enabled") ?? WebhookStore.Enabled(doc);
                    if (Proposer(req) is { } proposer)
                    {
                        if (req.Bool("enabled") == true) throw new RpcException("bad_request", "an agent cannot enable a webhook: the user does, in the Webhooks tab");
                        if (changesWhatRuns) { enabled = false; doc["proposedBy"] = proposer; }
                    }
                    else if (enabled) doc.Remove("proposedBy");
                    doc["enabled"] = enabled;
                    doc["updatedAt"] = WebhookStore.Iso(now);
                });
                Changed(ctx, id);
                return Shown(updated);
            });

        ctx.Rpc.Register(new RpcMethod("webhooks.rotateToken", "Make a new token for a webhook (the old one stops working at once): { id } → { id, token } (shown once)", Params: [RpcParam.Req("id")]),
            (req, _) =>
            {
                var id = req.Required("id");
                var token = WebhookStore.NewToken();
                store.Change(id, doc => { doc["tokenHash"] = WebhookStore.Hash(token); doc["tokenHint"] = token[^4..]; doc["updatedAt"] = WebhookStore.Iso(_time.GetUtcNow()); });
                Changed(ctx, id);
                return Task.FromResult<object?>(new JsonObject { ["id"] = id, ["token"] = token });
            });

        ctx.Rpc.Register(new RpcMethod("webhooks.delete", "Delete a webhook with its history and incidents: { id } → true. The chats its deliveries started stay", Params: [RpcParam.Req("id")]),
            (req, _) =>
            {
                var id = req.Required("id");
                if (!store.Delete(id)) throw new RpcException("not_found", $"Webhook {id} not found");
                Changed(ctx, id);
                return Task.FromResult<object?>(true);
            });

        ctx.Rpc.Register(new RpcMethod("webhooks.testHook", "Run a webhook's pre or post hook now on a sample context (it really runs): { id, hook (pre|post), context? (what to put on standard input; default: a sample of that hook) } → { ok, detail, output (what it printed: for a Before command, the JSON it answers with) }. For checking the command and what it does with the input",
            Params: [RpcParam.Req("id"), RpcParam.Req("hook"), RpcParam.Opt("context", RpcParamType.Object)]),
            async (req, ct) =>
            {
                var webhook = Require(store, req.Required("id"));
                var slot = req.Required("hook").Trim().ToLowerInvariant();
                if (!HookEngine.Slots.Contains(slot)) throw new RpcException("bad_request", "hook must be pre or post");
                if (webhook[slot] is not JsonObject h) throw new RpcException("bad_request", $"the webhook has no {slot} hook");
                var context = NetPiJson.ToNode(req.Prop("context")) as JsonObject ?? SampleContext(slot);
                context["event"] ??= slot == "pre" ? "received" : "finished";
                context["webhook"] = new JsonObject { ["id"] = webhook["id"]!.DeepClone(), ["name"] = webhook["name"]?.DeepClone() };
                try
                {
                    var run = await runner.Hooks.RunAsync(h, context, ct).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true, ["detail"] = run.Detail, ["output"] = Template.Clip(run.Output, 2000) };
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return new JsonObject { ["ok"] = false, ["detail"] = ex.Message }; }
            });

        ctx.Rpc.Register(new RpcMethod("webhooks.test", "Try a payload on a webhook: { id, payload (any JSON), adapt? (what the Before command would answer: { skip?, start?, key?, text?, title? }; the command itself is not run in a dry run), run? (false) } → without run: { action (start|join|append|skip), skip?, key?, agent?, title, prompt, note? } — what would happen, the prompt (or the follow-up for an open incident) filled in, nothing started or stored; with run: true: the delivery's { status, reason?, sessionId? } as if the payload had arrived (it starts, joins or appends to a real conversation)",
            Params: [RpcParam.Req("id"), RpcParam.Opt("payload", RpcParamType.Any), RpcParam.Opt("adapt", RpcParamType.Object), RpcParam.Opt("run", RpcParamType.Boolean)]),
            async (req, ct) =>
            {
                var hook = Require(store, req.Required("id"));
                var payload = req.Prop("payload") is not null ? NetPiJson.ToNode(req.Prop("payload")) : new JsonObject();
                if (req.Bool("run") == true)
                {
                    var result = await runner.HandleAsync(hook, payload, "test", ct).ConfigureAwait(false);
                    if (result.Status == WebhookStore.Waiting) _wake.Release();
                    return new JsonObject { ["status"] = result.Status, ["reason"] = result.Reason, ["sessionId"] = result.SessionId };
                }
                var plan = await runner.PlanAsync(hook, payload, NetPiJson.ToNode(req.Prop("adapt")) as JsonObject, ct).ConfigureAwait(false);
                return new JsonObject { ["action"] = plan.Action, ["skip"] = plan.Skip, ["key"] = plan.Key, ["agent"] = plan.AgentKey, ["title"] = plan.Title, ["prompt"] = plan.Prompt, ["note"] = plan.Note };
            });
    }

    // ---------------------------------------------------------------- validation

    private static string? Proposer(RpcRequest req) => req.Str("proposedBy")?.Trim() is { Length: > 0 } by ? by : null;

    /// <summary>
    /// A webhook's slug: the readable name in its URL. Given, it must be a-z, 0-9, - or _ (1–48 characters, not starting with whk_, which is what ids look like)
    /// and not taken by another webhook; derived from the name (when none is given), a taken one gets -2, -3…
    /// </summary>
    private static string? SlugFor(WebhookStore store, string? selfId, string? given, string? name)
    {
        bool Taken(string s) => store.List().Any(h => h["id"]?.GetValue<string>() != selfId && string.Equals(h["slug"]?.GetValue<string>(), s, StringComparison.OrdinalIgnoreCase));
        if (given is not null && given.Trim().Length > 0)
        {
            var slug = given.Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9_-]{0,47}$") || slug.StartsWith("whk_", StringComparison.Ordinal))
                throw new RpcException("bad_request", "the URL name must be 1–48 characters of a-z, 0-9, - or _ (and not start with whk_)");
            return Taken(slug) ? throw new RpcException("bad_request", $"another webhook already uses the URL name “{slug}”") : slug;
        }
        var derived = System.Text.RegularExpressions.Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (derived.Length > 48) derived = derived[..48].Trim('-');
        if (derived.Length == 0 || derived.StartsWith("whk_", StringComparison.Ordinal)) return null;
        var candidate = derived;
        for (var n = 2; Taken(candidate); n++) candidate = (derived.Length > 44 ? derived[..44] : derived) + "-" + n;
        return candidate;
    }

    private static int Fuse(int value) =>
        value is >= 0 and <= 100_000 ? value : throw new RpcException("bad_request", "maxChatsPerHour must be between 0 (no limit) and 100000");

    private static int Timeout(int minutes) => minutes is >= 0 and <= 1440 ? minutes : throw new RpcException("bad_request", "timeoutMinutes must be 0–1440 (0 = never)");

    /// <summary>What <c>webhooks.testHook</c> puts on standard input when the caller gives nothing: the shape a real run has, with obvious sample values.</summary>
    private static JsonObject SampleContext(string slot) => slot == "pre"
        ? new JsonObject { ["event"] = "received", ["title"] = "sample: something happened", ["alert"] = new JsonObject { ["id"] = "sample-1", ["message"] = "this is a test" } }
        : new JsonObject
        {
            ["event"] = "finished", ["title"] = "sample: something happened", ["key"] = "sample-1", ["count"] = 1, ["sessionId"] = "ses_sample", ["status"] = "ok", ["verdict"] = "real",
            ["summary"] = "This is a test of the post hook.", ["result"] = "This is a test of the post hook.\nVERDICT: real",
            ["alert"] = new JsonObject { ["id"] = "sample-1", ["message"] = "this is a test" },
        };

    private static JsonObject Require(WebhookStore store, string id) =>
        store.Get(id) ?? throw new RpcException("not_found", $"Webhook {id} not found");

    private static string Name(string name) =>
        name.Trim() is { Length: > 0 } n ? Template.Clip(n, 80) : throw new RpcException("bad_request", "the name cannot be empty");

    private static string Prompt(string prompt) =>
        prompt.Trim() is { Length: > 0 } p ? p : throw new RpcException("bad_request", "the prompt cannot be empty");


    /// <summary>An incident as <c>webhooks.incidents</c> answers it: without the waiting payloads (they may be large), with when its chat is next tried.</summary>
    private static JsonObject ShownIncident(JsonObject i)
    {
        var shown = new JsonObject
        {
            ["webhookId"] = i["webhookId"]?.DeepClone(), ["key"] = i["key"]?.DeepClone(), ["state"] = i["state"]?.DeepClone(),
            ["sessionId"] = i["sessionId"]?.DeepClone(), ["alertCount"] = i["alertCount"]?.DeepClone(), ["openedAt"] = i["openedAt"]?.DeepClone(),
            ["verdict"] = i["lastVerdict"]?.DeepClone(), ["awaiting"] = i["awaiting"]?.DeepClone(),
            ["lastAt"] = i["lastAtMs"] is JsonValue v && v.TryGetValue<long>(out var ms) ? WebhookStore.Iso(DateTimeOffset.FromUnixTimeMilliseconds(ms)) : null,
        };
        if (i["state"]?.GetValue<string>() == WebhookStore.Waiting && i["startAtMs"] is JsonValue s && s.TryGetValue<long>(out var start))
        {
            shown["startsAt"] = WebhookStore.Iso(DateTimeOffset.FromUnixTimeMilliseconds(start));
            shown["waiting"] = i["lastReason"]?.DeepClone();
        }
        return shown;
    }

    /// <summary>The level its chats run at (<see cref="RunPriority"/>), checked.</summary>
    private static string Priority(string value) => RunPriority.Parse(value) is { } level
        ? RunPriority.Name(level)
        : throw new RpcException("bad_request", $"priority must be one of {string.Join(", ", RunPriority.Names)}");

    /// <summary>A webhook as the RPCs answer it: without its token hash, with the path to POST to.</summary>
    private static JsonObject Shown(JsonObject doc)
    {
        var shown = (JsonObject)doc.DeepClone();
        shown.Remove("tokenHash");
        shown["path"] = "/hook/" + (doc["slug"]?.GetValue<string>() is { Length: > 0 } slug ? slug : doc["id"]!.GetValue<string>());
        return shown;
    }

    private static Task<string?> ProjectAsync(IPluginContext ctx, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult<string?>(null);
        var id = projectId.Trim();
        var project = ctx.Sessions.GetProject(id)
            ?? ctx.Sessions.ListProjects().FirstOrDefault(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException("not_found", $"Project {id} not found");
        return Task.FromResult<string?>(project.Id);
    }

    /// <summary>The profile a webhook's chats get (its tools and system prompt), checked against <c>profiles.list</c> when the Profiles plugin is running; empty = the automatic profile.</summary>
    private static async Task<string?> ProfileAsync(IPluginContext ctx, string? profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var id = profile.Trim();
        if (!ctx.Rpc.Exists("profiles.list")) return id;
        var list = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("profiles.list", new JsonObject(), ct).ConfigureAwait(false)) as JsonObject;
        var match = (list?["profiles"] as JsonArray)?.FirstOrDefault(p => p?["id"] is JsonValue v && v.TryGetValue<string>(out var k) && string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
        if (list is not null && match is null) throw new RpcException("not_found", $"Profile {id} not found (profiles.list)");
        return match?["id"]?.GetValue<string>() ?? id;
    }

    /// <summary>The agent spec: empty, <c>any</c>, <c>free</c>, a key, or a comma list of keys — each key checked against <c>agents.list</c> when the Agents plugin is running.</summary>
    private static async Task<string?> AgentAsync(IPluginContext ctx, string? agent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent)) return null;
        var spec = agent.Trim();
        if (spec.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any";
        if (spec.Equals("free", StringComparison.OrdinalIgnoreCase)) return "free";
        var keys = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) return null;
        if (!ctx.Rpc.Exists("agents.list")) return string.Join(",", keys);
        var list = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("agents.list", new JsonObject(), ct).ConfigureAwait(false)) as JsonArray;
        var resolved = new List<string>();
        foreach (var key in keys)
        {
            var match = list?.FirstOrDefault(s => s?["key"] is JsonValue v && v.TryGetValue<string>(out var k) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (list is not null && match is null) throw new RpcException("not_found", $"Agent {key} not found (agents.list)");
            resolved.Add(match?["key"]?.GetValue<string>() ?? key);
        }
        return string.Join(",", resolved);
    }
}
