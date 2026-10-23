using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Schedules;

/// <summary>
/// Scheduled runs (idea-1y1174): a prompt that starts as a fresh chat on a project once, every N minutes or daily at a time.
/// The schedules live in the plugin's collections (<see cref="ScheduleStore"/>), so a restart resumes from the persisted next
/// run; a run that fell due while the host was down runs once if it is less than <c>schedules.missedGraceHours</c> late and is
/// recorded as missed otherwise — never a backlog of catch-up runs. Starting one is <see cref="ScheduleRunner"/>.
/// <para>
/// A schedule an agent sets up runs unattended with every tool, so it is a proposal: stored paused with <c>proposedBy</c>,
/// and the tool call that made it waits for the user's OK on a card in the chat (<see cref="PendingProposals"/>):
/// <c>schedule.proposed</c> / <c>schedule.decided</c> events (unscoped), <c>schedules.decide</c> (allow enables it, the user's
/// own enable; discard deletes it) and <c>schedules.proposals</c> (what waits, for a reconnect). A subagent's add does not
/// wait — nobody watches its chat — and a stopped plugin withdraws the waits, leaving the proposal in the Schedules tab.
/// </para>
/// </summary>
[NetPiPlugin("netpi.schedules", Name = "Schedules", Description = "Scheduled runs: a prompt started as a fresh chat on a project at a time, an interval or daily, with availability, budget and missed-run checks", Order = 96)]
public sealed class SchedulesPlugin : INetPiPlugin
{
    private readonly TimeProvider _time;
    private readonly bool _loop;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _stop;
    private Task? _worker;

    internal ScheduleStore? Store { get; private set; }
    internal ScheduleRunner? Runner { get; private set; }
    internal PendingProposals? Proposals { get; private set; }

    public SchedulesPlugin() : this(TimeProvider.System, loop: true) { }

    /// <summary>A test's clock, and no background loop: the test calls <see cref="ScheduleRunner.TickAsync"/> itself.</summary>
    internal SchedulesPlugin(TimeProvider time, bool loop)
    {
        _time = time;
        _loop = loop;
    }

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Services.Register(new SettingsSection
        {
            Id = "schedules", Title = "Schedules", Group = "Automation", Order = 60,
            Help = "Prompts that start on their own as a new chat: once, every N minutes, or daily at a time. The agent's schedules tool and the Schedules tab manage them.",
            Settings =
            [
                SettingInfo.Bool("schedules.enabled", "Run schedules", true, "Off: nothing starts; due runs wait, and the missed-run rule applies when it is switched back on."),
                SettingInfo.Int("schedules.missedGraceHours", "Late runs within", 24, "A run that fell due while NetPI was not running still starts if it is at most this late; later, it is recorded as missed. 0 still allows the minute the timer needs.", 0, 720, "hours"),
                SettingInfo.Int("schedules.minIntervalMinutes", "Shortest interval", 5, "The smallest N an every-N-minutes schedule may have.", 1, 1440, "min"),
                SettingInfo.Int("schedules.maxRunsPerDay", "Runs per schedule per day", 48, "A schedule that started this many runs in the last 24 hours skips until the oldest is a day old.", 1, 1440),
            ],
        });
        var store = Store = new ScheduleStore(ctx.Data);
        var runner = Runner = new ScheduleRunner(ctx, store, _time);
        var proposals = Proposals = new PendingProposals(ctx);
        Register(ctx, store, runner, proposals);
        ctx.Tools.Register(new SchedulesTool(ctx));
        // the cards, from the runtime's waits of this kind (sync handlers: an async one left alive roots this load context)
        ctx.Events.Subscribe(EventTypes.AgentWaiting, e =>
        {
            if (e.As<JsonObject>()?["wait"] is JsonObject w && (string?)w["kind"] == PendingProposals.Kind) ctx.Events.Publish("schedule.proposed", PendingProposals.Json(w));
        });
        ctx.Events.Subscribe(EventTypes.AgentWaitEnded, e =>
        {
            if (e.As<JsonObject>() is not { } d || (string?)d["kind"] != PendingProposals.Kind || (string?)d["reason"] == "answered") return;   // a decision closes its own
            proposals.Closed((string)d["waitId"]!, (string?)d["sessionId"] ?? "", (string?)d["callId"], (string?)d["reason"] == "steered" ? "steered" : "cancelled");
        });
        ctx.Ui.AddTab(new UiTabInfo
        {
            Id = "schedules", Title = "Schedules", Panel = UiPanel.Left, Order = 25, Module = "ui.js",
            // the host icon set has no clock; UiTabInfo.Icon takes an inline SVG (docs/UI.md)
            Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round'><circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/></svg>",
        });
        if (_loop)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
            _worker = LoopAsync(ctx, store, runner, _stop.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stop?.Cancel();
        if (_worker is { } w) { try { await w.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        // nothing to withdraw: a proposal's wait is the runtime's, and it waits on through a reload of this plugin
    }

    private async Task LoopAsync(IPluginContext ctx, ScheduleStore store, ScheduleRunner runner, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _time, ct).ConfigureAwait(false);   // let startup settle first
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ctx.Settings.Get("schedules.enabled", true)) await runner.TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Logger.LogError(ex, "Schedules: a tick failed");
                }
                // sleep until the soonest run (at most a minute, so a changed setting or clock is seen), or until a change
                // (paused: nothing advances a due time, so it would read the documents once a second for nothing)
                var wait = TimeSpan.FromMinutes(1);
                if (ctx.Settings.Get("schedules.enabled", true) && store.NextDue() is { } next) wait = TimeSpan.FromTicks(Math.Clamp((next - _time.GetUtcNow()).Ticks, TimeSpan.TicksPerSecond, wait.Ticks));
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Changed(IPluginContext ctx, string? id)
    {
        _wake.Release();
        ctx.Events.Publish(ScheduleRunner.ChangedEvent, new { id });
    }

    private void Register(IPluginContext ctx, ScheduleStore store, ScheduleRunner runner, PendingProposals proposals)
    {
        const string Doc = "Schedule: { id, name, prompt, projectId?, agent?, priority, cadence: { kind: once|every|daily, at? | minutes? | time?, tz? }, enabled, nextRunAt?, lastRunAt?, lastStatus? (ran|skipped|failed|missed), lastReason?, lastSessionId?, finishedAt? }";

        ctx.Rpc.Register(new RpcMethod("schedules.list", "The schedules, oldest first: { projectId? } → Schedule[]. " + Doc, ReadOnly: true,
            [RpcParam.Opt("projectId", RpcParamType.String, "only this project's")]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.List(req.Str("projectId")).Select(d => (JsonNode)Shown(d))])));

        ctx.Rpc.Register(new RpcMethod("schedules.get", "One schedule: { id } → Schedule", ReadOnly: true, [RpcParam.Req("id")]),
            (req, _) => Task.FromResult<object?>(Shown(Require(store, req.Required("id")))));

        ctx.Rpc.Register(new RpcMethod("schedules.history", "Runs, newest first: { id?, limit? (50) } → { scheduleId, at, status, reason?, sessionId? }[] (the chat a run started is sessionId)", ReadOnly: true,
            [RpcParam.Opt("id", RpcParamType.String, "one schedule's; all when omitted"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.History(req.Str("id"), Math.Clamp(req.Int("limit") ?? 50, 1, 500)).Select(d => (JsonNode)d)])));

        ctx.Rpc.Register(new RpcMethod("schedules.add", "Add a schedule: { prompt, cadence, name?, projectId?, agent? (an agent key or \"any\"), priority? (background | low | normal (the default) | high | critical: the order its chats queue in while their agent is busy), enabled? (true), proposedBy? } → Schedule. The prompt starts a fresh chat on the project at each run. proposedBy (the schedules tool sets it: who proposed it) stores it paused, as a proposal the user enables in the Schedules tab",
            Params:
            [
                RpcParam.Req("prompt"), RpcParam.Req("cadence", RpcParamType.Object), RpcParam.Opt("name"), RpcParam.Opt("projectId"),
                RpcParam.Opt("agent"), RpcParam.Opt("priority", RpcParamType.String, "background | low | normal | high | critical"), RpcParam.Opt("enabled", RpcParamType.Boolean), RpcParam.Opt("proposedBy"),
            ]),
            async (req, ct) =>
            {
                var now = _time.GetUtcNow();
                var prompt = req.Required("prompt").Trim();
                var cadence = Cadence.Parse(NetPiJson.ToNode(req.Prop("cadence")), MinMinutes(ctx));
                var projectId = await ProjectAsync(ctx, req.Str("projectId")).ConfigureAwait(false);
                var agent = await AgentAsync(ctx, req.Str("agent"), ct).ConfigureAwait(false);
                if (cadence.Kind == Cadence.Once && cadence.At <= now) throw new RpcException("bad_request", "a once cadence must be in the future");
                // A schedule an agent sets up runs unattended with the full tool set, so it waits for the user's OK: a prompt
                // injected into a chat must not be able to make itself recurring (idea-96bh9b).
                var proposedBy = Proposer(req);
                var doc = new JsonObject
                {
                    ["id"] = Ids.New("sch"),
                    ["name"] = req.Str("name")?.Trim() is { Length: > 0 } n ? n : Clip(prompt.ReplaceLineEndings(" "), 60),
                    ["prompt"] = prompt,
                    ["projectId"] = projectId,
                    ["agent"] = agent,
                    ["priority"] = Priority(req.Str("priority") ?? "normal"),
                    ["cadence"] = cadence.ToJson(),
                    ["enabled"] = proposedBy is null && (req.Bool("enabled") ?? true),
                    ["createdAt"] = ScheduleStore.Iso(now),
                    ["updatedAt"] = ScheduleStore.Iso(now),
                };
                if (proposedBy is not null) doc["proposedBy"] = proposedBy;
                ScheduleStore.SetNext(doc, cadence.First(now));
                store.Put(doc);
                Changed(ctx, doc["id"]!.GetValue<string>());
                return Shown(doc);
            });

        ctx.Rpc.Register(new RpcMethod("schedules.update", "Change a schedule: { id, prompt?, cadence?, name?, projectId? (\"\" clears), agent? (\"\" clears), priority?, enabled?, proposedBy? } → Schedule. enabled: false pauses it, true resumes it from now (a finished once needs a new cadence) and approves a proposed one. proposedBy (the schedules tool sets it): a change of what runs (prompt, cadence, project, agent, priority) pauses the schedule as a proposal for the user, and it cannot enable one",
            Params:
            [
                RpcParam.Req("id"), RpcParam.Opt("prompt"), RpcParam.Opt("cadence", RpcParamType.Object), RpcParam.Opt("name"),
                RpcParam.Opt("projectId"), RpcParam.Opt("agent"), RpcParam.Opt("priority", RpcParamType.String, "background | low | normal | high | critical"), RpcParam.Opt("enabled", RpcParamType.Boolean), RpcParam.Opt("proposedBy"),
            ]),
            async (req, ct) =>
            {
                var id = req.Required("id");
                Require(store, id);
                // What the request names is resolved first (an agent is checked over an awaited RPC, and nothing may await
                // inside a transaction); the schedule is then read, changed and written as one unit, so a tick that
                // claims a due run meanwhile is not overwritten with the document from before the claim — which would
                // have the same due time claimed again.
                var project = req.Str("projectId") is { } p ? (p.Length == 0 ? null : await ProjectAsync(ctx, p).ConfigureAwait(false)) : null;
                var agent = req.Str("agent") is { } a ? (a.Length == 0 ? null : await AgentAsync(ctx, a, ct).ConfigureAwait(false)) : null;
                var priority = req.Str("priority") is { } pr ? Priority(pr) : null;
                var updated = store.Change(id, doc =>
                {
                    var now = _time.GetUtcNow();
                    var wasEnabled = ScheduleStore.Enabled(doc);
                    if (req.Str("prompt") is { } prompt)
                    {
                        if (prompt.Trim().Length == 0) throw new RpcException("bad_request", "the prompt cannot be empty");
                        doc["prompt"] = prompt.Trim();
                    }
                    if (req.Str("name") is { } name && name.Trim().Length > 0) doc["name"] = name.Trim();
                    if (req.Str("projectId") is not null) doc["projectId"] = project;
                    if (req.Str("agent") is not null) doc["agent"] = agent;
                    if (priority is not null) doc["priority"] = priority;
                    var cadenceChanged = req.Prop("cadence") is not null;
                    var cadence = cadenceChanged ? Cadence.Parse(NetPiJson.ToNode(req.Prop("cadence")), MinMinutes(ctx)) : ScheduleStore.CadenceOf(doc);
                    if (cadenceChanged)
                    {
                        if (cadence.Kind == Cadence.Once && cadence.At <= now) throw new RpcException("bad_request", "a once cadence must be in the future");
                        doc["cadence"] = cadence.ToJson();
                        doc["finishedAt"] = null;
                    }
                    var enabled = req.Bool("enabled") ?? (cadenceChanged || wasEnabled);
                    // An agent's change of what runs waits for the user like a schedule it adds, and it never enables one: that
                    // is the user's OK. The user enabling a proposal is that OK (idea-96bh9b).
                    if (Proposer(req) is { } proposer)
                    {
                        if (req.Bool("enabled") == true) throw new RpcException("bad_request", "an agent cannot enable a schedule: the user does, in the Schedules tab");
                        if (req.Str("prompt") is not null || cadenceChanged || req.Str("projectId") is not null || req.Str("agent") is not null || priority is not null)
                        {
                            enabled = false;
                            doc["proposedBy"] = proposer;
                        }
                    }
                    else if (enabled) doc.Remove("proposedBy");
                    if (enabled && doc["finishedAt"] is not null) throw new RpcException("bad_request", "this once schedule has run; give it a new cadence to run again");
                    doc["enabled"] = enabled;
                    // a new cadence or a resume counts from now: a paused schedule does not owe the runs it slept through
                    if (cadenceChanged || (enabled && !wasEnabled)) ScheduleStore.SetNext(doc, cadence.First(now));
                    doc["updatedAt"] = ScheduleStore.Iso(now);
                });
                Changed(ctx, id);
                return Shown(updated);
            });

        ctx.Rpc.Register(new RpcMethod("schedules.delete", "Delete a schedule and its history: { id } → true. The chats its runs started stay", Params: [RpcParam.Req("id")]),
            (req, _) =>
            {
                var id = req.Required("id");
                if (!store.Delete(id)) throw new RpcException("not_found", $"Schedule {id} not found");
                Changed(ctx, id);
                return Task.FromResult<object?>(true);
            });

        ctx.Rpc.Register(new RpcMethod("schedules.run", "Run a schedule now, besides its cadence (the same checks apply): { id } → { status: ran|skipped|failed, reason?, sessionId? }", Params: [RpcParam.Req("id")]),
            async (req, ct) =>
            {
                var result = await runner.StartAsync(Require(store, req.Required("id")), ct).ConfigureAwait(false);
                Changed(ctx, req.Required("id"));
                return new JsonObject { ["status"] = result.Status, ["reason"] = result.Reason, ["sessionId"] = result.SessionId };
            });

        ctx.Rpc.Register(new RpcMethod("schedules.decide", "Decide a schedule proposal an agent proposed, on the card in its chat: { id, decision: allow | discard } → allow: the enabled schedule, discard: { id }. Allow is the user's own enable of the proposal (it runs from now on); discard deletes it. The card's buttons call this.",
            ReadOnly: false, Params: [RpcParam.Req("id"), RpcParam.Req("decision", RpcParamType.String, "allow | discard")]),
            async (req, ct) => await proposals.DecideAsync(req.Required("id"), req.Required("decision"), ct).ConfigureAwait(false));

        ctx.Rpc.Register(new RpcMethod("schedules.proposals", "Schedule proposals that wait for the user's decision (a schedules add, the card in its chat): { sessionId? } → { id, sessionId, callId, agentId, schedule, askedAt }[]", ReadOnly: true, Params: [RpcParam.Opt("sessionId")]),
            (req, _) => Task.FromResult<object?>(proposals.List(req.Str("sessionId"))));
    }

    private static int MinMinutes(IPluginContext ctx) => Math.Clamp(ctx.Settings.Get("schedules.minIntervalMinutes", 5), 1, 1440);

    /// <summary>Who proposed the change (<c>proposedBy</c>: the schedules tool names the chat), or null for the user's own.</summary>
    private static string? Proposer(RpcRequest req) => req.Str("proposedBy")?.Trim() is { Length: > 0 } by ? by : null;

    private static JsonObject Require(ScheduleStore store, string id) =>
        store.Get(id) ?? throw new RpcException("not_found", $"Schedule {id} not found");

    /// <summary>A schedule as the RPCs answer it: the stored document plus its cadence in words.</summary>
    /// <summary>The level its chats run at (<see cref="RunPriority"/>), checked.</summary>
    private static string Priority(string value) => RunPriority.Parse(value) is { } level
        ? RunPriority.Name(level)
        : throw new RpcException("bad_request", $"priority must be one of {string.Join(", ", RunPriority.Names)}");

    private static JsonObject Shown(JsonObject doc)
    {
        var shown = (JsonObject)doc.DeepClone();
        shown.Remove("nextRunMs");
        try { shown["when"] = ScheduleStore.CadenceOf(doc).Describe(); } catch (RpcException) { /* a stored cadence a newer version wrote */ }
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

    /// <summary>The agent a schedule names, checked against <c>agents.list</c> when the Agents plugin is running.</summary>
    private static async Task<string?> AgentAsync(IPluginContext ctx, string? agent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent)) return null;
        var key = agent.Trim();
        if (string.Equals(key, "any", StringComparison.OrdinalIgnoreCase) || !ctx.Rpc.Exists("agents.list")) return key;
        var list = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("agents.list", new JsonObject(), ct).ConfigureAwait(false)) as JsonArray;
        var match = list?.FirstOrDefault(s => s?["key"] is JsonValue v && v.TryGetValue<string>(out var k) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (list is not null && match is null) throw new RpcException("not_found", $"Agent {key} not found (agents.list)");
        return match?["key"]?.GetValue<string>() ?? key;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
