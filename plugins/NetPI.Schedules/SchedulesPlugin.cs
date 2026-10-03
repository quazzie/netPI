using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Schedules;

/// <summary>
/// Scheduled runs (idea-1y1174): a prompt that starts as a fresh chat on a project once, every N minutes or daily at a time.
/// The schedules live in the plugin's collections (<see cref="ScheduleStore"/>), so a restart resumes from the persisted next
/// run; a run that fell due while the host was down runs once if it is less than <c>schedules.missedGraceHours</c> late and is
/// recorded as missed otherwise — never a backlog of catch-up runs. Starting one is <see cref="ScheduleRunner"/>.
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
            Id = "schedules", Title = "Schedules", Group = "Agents", Order = 60,
            Help = "Prompts that start on their own as a new chat: once, every N minutes, or daily at a time (schedules.add).",
            Settings =
            [
                SettingInfo.Bool("schedules.enabled", "Run schedules", true, "Off: nothing starts; due runs wait, and the missed-run rule applies when it is switched back on."),
                SettingInfo.Int("schedules.missedGraceHours", "Late runs within", 24, "A run that fell due while NetPI was not running still starts if it is at most this late; later, it is recorded as missed.", 0, 720, "hours"),
                SettingInfo.Int("schedules.minIntervalMinutes", "Shortest interval", 5, "The smallest N an every-N-minutes schedule may have.", 1, 1440, "min"),
                SettingInfo.Int("schedules.maxRunsPerDay", "Runs per schedule per day", 48, "A schedule that started this many runs in the last 24 hours skips until the oldest is a day old.", 1, 1440),
            ],
        });
        var store = Store = new ScheduleStore(ctx.Data);
        var runner = Runner = new ScheduleRunner(ctx, store, _time);
        Register(ctx, store, runner);
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
                var wait = TimeSpan.FromMinutes(1);
                if (store.NextDue() is { } next) wait = TimeSpan.FromTicks(Math.Clamp((next - _time.GetUtcNow()).Ticks, TimeSpan.TicksPerSecond, wait.Ticks));
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

    private void Register(IPluginContext ctx, ScheduleStore store, ScheduleRunner runner)
    {
        const string Doc = "Schedule: { id, name, prompt, projectId?, agent?, cadence: { kind: once|every|daily, at? | minutes? | time?, tz? }, enabled, nextRunAt?, lastRunAt?, lastStatus? (ran|skipped|failed|missed), lastReason?, lastSessionId?, finishedAt? }";

        ctx.Rpc.Register(new RpcMethod("schedules.list", "The schedules, oldest first: { projectId? } → Schedule[]. " + Doc, ReadOnly: true,
            [RpcParam.Opt("projectId", RpcParamType.String, "only this project's")]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.List(req.Str("projectId")).Select(d => (JsonNode)Shown(d))])));

        ctx.Rpc.Register(new RpcMethod("schedules.get", "One schedule: { id } → Schedule", ReadOnly: true, [RpcParam.Req("id")]),
            (req, _) => Task.FromResult<object?>(Shown(Require(store, req.Required("id")))));

        ctx.Rpc.Register(new RpcMethod("schedules.history", "Runs, newest first: { id?, limit? (50) } → { scheduleId, at, status, reason?, sessionId? }[] (the chat a run started is sessionId)", ReadOnly: true,
            [RpcParam.Opt("id", RpcParamType.String, "one schedule's; all when omitted"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.History(req.Str("id"), Math.Clamp(req.Int("limit") ?? 50, 1, 500)).Select(d => (JsonNode)d)])));

        ctx.Rpc.Register(new RpcMethod("schedules.add", "Add a schedule: { prompt, cadence, name?, projectId?, agent? (an agent key or \"any\"), enabled? (true) } → Schedule. The prompt starts a fresh chat on the project at each run",
            Params:
            [
                RpcParam.Req("prompt"), RpcParam.Req("cadence", RpcParamType.Object), RpcParam.Opt("name"), RpcParam.Opt("projectId"),
                RpcParam.Opt("agent"), RpcParam.Opt("enabled", RpcParamType.Boolean),
            ]),
            async (req, ct) =>
            {
                var now = _time.GetUtcNow();
                var prompt = req.Required("prompt").Trim();
                var cadence = Cadence.Parse(NetPiJson.ToNode(req.Prop("cadence")), MinMinutes(ctx));
                var projectId = await ProjectAsync(ctx, req.Str("projectId")).ConfigureAwait(false);
                var agent = await AgentAsync(ctx, req.Str("agent"), ct).ConfigureAwait(false);
                if (cadence.Kind == Cadence.Once && cadence.At <= now) throw new RpcException("bad_request", "a once cadence must be in the future");
                var doc = new JsonObject
                {
                    ["id"] = Ids.New("sch"),
                    ["name"] = req.Str("name")?.Trim() is { Length: > 0 } n ? n : Clip(prompt.ReplaceLineEndings(" "), 60),
                    ["prompt"] = prompt,
                    ["projectId"] = projectId,
                    ["agent"] = agent,
                    ["cadence"] = cadence.ToJson(),
                    ["enabled"] = req.Bool("enabled") ?? true,
                    ["createdAt"] = ScheduleStore.Iso(now),
                    ["updatedAt"] = ScheduleStore.Iso(now),
                };
                ScheduleStore.SetNext(doc, cadence.First(now));
                store.Put(doc);
                Changed(ctx, doc["id"]!.GetValue<string>());
                return Shown(doc);
            });

        ctx.Rpc.Register(new RpcMethod("schedules.update", "Change a schedule: { id, prompt?, cadence?, name?, projectId? (\"\" clears), agent? (\"\" clears), enabled? } → Schedule. enabled: false pauses it, true resumes it from now (a finished once needs a new cadence)",
            Params:
            [
                RpcParam.Req("id"), RpcParam.Opt("prompt"), RpcParam.Opt("cadence", RpcParamType.Object), RpcParam.Opt("name"),
                RpcParam.Opt("projectId"), RpcParam.Opt("agent"), RpcParam.Opt("enabled", RpcParamType.Boolean),
            ]),
            async (req, ct) =>
            {
                var now = _time.GetUtcNow();
                var doc = Require(store, req.Required("id"));
                var wasEnabled = ScheduleStore.Enabled(doc);
                if (req.Str("prompt") is { } prompt)
                {
                    if (prompt.Trim().Length == 0) throw new RpcException("bad_request", "the prompt cannot be empty");
                    doc["prompt"] = prompt.Trim();
                }
                if (req.Str("name") is { } name && name.Trim().Length > 0) doc["name"] = name.Trim();
                if (req.Str("projectId") is { } project) doc["projectId"] = project.Length == 0 ? null : await ProjectAsync(ctx, project).ConfigureAwait(false);
                if (req.Str("agent") is { } agent) doc["agent"] = agent.Length == 0 ? null : await AgentAsync(ctx, agent, ct).ConfigureAwait(false);
                var cadenceChanged = req.Prop("cadence") is not null;
                var cadence = cadenceChanged ? Cadence.Parse(NetPiJson.ToNode(req.Prop("cadence")), MinMinutes(ctx)) : ScheduleStore.CadenceOf(doc);
                if (cadenceChanged)
                {
                    if (cadence.Kind == Cadence.Once && cadence.At <= now) throw new RpcException("bad_request", "a once cadence must be in the future");
                    doc["cadence"] = cadence.ToJson();
                    doc["finishedAt"] = null;
                }
                var enabled = req.Bool("enabled") ?? (cadenceChanged || wasEnabled);
                if (enabled && doc["finishedAt"] is not null) throw new RpcException("bad_request", "this once schedule has run; give it a new cadence to run again");
                doc["enabled"] = enabled;
                // a new cadence or a resume counts from now: a paused schedule does not owe the runs it slept through
                if (cadenceChanged || (enabled && !wasEnabled)) ScheduleStore.SetNext(doc, cadence.First(now));
                doc["updatedAt"] = ScheduleStore.Iso(now);
                store.Put(doc);
                Changed(ctx, doc["id"]!.GetValue<string>());
                return Shown(doc);
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
    }

    private static int MinMinutes(IPluginContext ctx) => Math.Clamp(ctx.Settings.Get("schedules.minIntervalMinutes", 5), 1, 1440);

    private static JsonObject Require(ScheduleStore store, string id) =>
        store.Get(id) ?? throw new RpcException("not_found", $"Schedule {id} not found");

    /// <summary>A schedule as the RPCs answer it: the stored document plus its cadence in words.</summary>
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
