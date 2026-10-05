using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>
/// The agent runtime (<see cref="IAgentRuntime"/>) and its RPC methods: <c>agent.send</c>, <c>agent.abort</c>,
/// <c>agent.queue</c>, <c>agent.dequeue</c>, <c>runs.list</c>, <c>agent.get</c>, and the per-session tool switches
/// <c>agent.tools</c>, <c>agent.setTools</c>.
/// <para>Settings: <c>agent.maxTurns</c> (200), <c>agent.defaultMaxOutputTokens</c> (16384), <c>agent.maxToolResultChars</c> (20000),
/// <c>agent.parallelReadOnlyTools</c> (true), <c>agents.maxDepth</c> (3).</para>
/// </summary>
[NetPiPlugin("netpi.runtime", Name = "Agent runtime", Description = "The model/tool loop of chats and subagents (runs), steering and follow-ups, waiting for subagents", Order = 50)]
public sealed class RuntimePlugin : INetPiPlugin
{
    private AgentRuntime? _runtime;

    internal AgentRuntime? Runtime => _runtime;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // Run state of this plugin: a fork starts without it (the session service remembers the keys even when this plugin is not loaded at a fork).
        context.Sessions.DeclareForkReset("agentId", "parentAgentId", "agentInstructions", "runtimeEnvironment");
        context.Services.Register(new SettingsSection
        {
            Id = "agents", Title = "Runs", Group = "Agents", Order = 10,
            Settings =
            [
                SettingInfo.Int("agent.maxTurns", "Model calls per run", 200, "A run stops after this many.", 1, 10000),
                SettingInfo.Int("agents.maxDepth", "Subagent depth", ToolSelection.DefaultMaxDepth, "How deep subagents may nest; the deepest get no orchestration tools.", 1, 10),
                SettingInfo.Bool("agent.parallelReadOnlyTools", "Run read-only tools in parallel", true, "Several read-only calls of one turn at once."),
                SettingInfo.Int(ToolResultLimit.Setting, "Longest tool result", ToolResultLimit.Default, "Longer results go to a file: the agent sees their start and end and reads the rest from the file when it needs it.", 1000, null, "chars"),
                SettingInfo.Int(OutputLimit.Setting, "Output limit for models without one", OutputLimit.Default, null, 256, null, "tokens"),
            ],
        });
        // The registry of what runs on shared model resources (a local model's slots) outlives the agents plugin, whose
        // scheduler hands the leases out: registered here too, it stays while that plugin is off (disabled, or between a
        // reload's stop and start), so the runtime admits local runs against the same counts meanwhile — a run in flight
        // keeps its slot counted — and the plugin adopts it when it comes back instead of starting from an empty one (its
        // start takes the registered instance; a reload of this plugin finds the agents plugin's registration the same way).
        // A plain class in the shared contracts, so the instance is safe to hold across either plugin's reload.
        var leases = context.Services.Get<IResourceLeases>() as ResourceLeases ?? new ResourceLeases(context.Events);
        context.Services.Register<IResourceLeases>(leases);
        var runtime = new AgentRuntime(context);
        runtime.Initialize();
        _runtime = runtime;
        context.Services.Register<IAgentRuntime>(runtime);

        context.Rpc.Register("agent.send", async (req, token) =>
        {
            var sessionId = req.Required("sessionId");
            context.Sessions.Require(sessionId);
            var text = req.Str("text") ?? "";
            var images = ParseImages(req.Prop("images"));
            if (string.IsNullOrWhiteSpace(text) && images is null) throw new RpcException("bad_request", "Empty message");
            var mode = ParseMode(req.Str("mode"));
            try
            {
                return await runtime.SendAsync(sessionId, new UserInput { Text = text, Images = images }, mode, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException("unavailable", ex.Message);
            }
        }, "Send a message to a session's agent: { sessionId, text, images?, mode?: auto|steer|queue } → AgentInfo");

        context.Rpc.Register("agent.abort", async (req, _) =>
        {
            var id = req.Str("sessionId") ?? req.Required("id");
            return await runtime.AbortAsync(id).ConfigureAwait(false);
        }, "Abort the current run of a session's agent: { sessionId } → bool");

        context.Rpc.RegisterReadOnly("agent.queue", (req, _) =>
            Task.FromResult<object?>(runtime.GetQueue(req.Required("sessionId"))),
            "Pending steering/follow-up inputs: { sessionId } → QueuedInput[]");

        context.Rpc.Register("agent.dequeue", (req, _) =>
            Task.FromResult<object?>(runtime.RemoveQueued(req.Required("sessionId"), req.Required("id"))),
            "Remove a pending input: { sessionId, id } → bool");

        context.Rpc.Register("agent.promote", (req, _) =>
            Task.FromResult<object?>(runtime.PromoteQueued(req.Required("sessionId"), req.Required("id"))),
            "Deliver a queued follow-up at the run's next step instead of after the run (it becomes a steer): { sessionId, id } → bool (false: not a queued follow-up)");

        context.Rpc.RegisterReadOnly("runs.list", (req, _) =>
            Task.FromResult<object?>(runtime.List(req.Bool("includeFinished") ?? true)),
            "Agents (active and recent): { includeFinished? } → AgentInfo[]");

        context.Rpc.RegisterReadOnly("agent.get", (req, _) =>
        {
            var id = req.Str("id");
            var sessionId = req.Str("sessionId");
            var info = id is not null ? runtime.Get(id) : sessionId is not null ? runtime.GetBySession(sessionId) : null;
            return Task.FromResult<object?>(info);
        }, "One agent: { id? , sessionId? } → AgentInfo | null");

        context.Rpc.RegisterReadOnly("agent.tools", (req, _) =>
            Task.FromResult<object?>(SessionToolSwitches.Info(context, runtime, req.Required("sessionId"))),
            "A session's tools with their switches: { sessionId } → { sessionId, started, contextTokens, off, tools: { name, label, category, description, readOnly, pluginId, on }[] }");

        context.Rpc.Register("agent.setTools", (req, _) =>
            Task.FromResult<object?>(SessionToolSwitches.Set(context, runtime, req.Required("sessionId"),
                SessionToolSwitches.Names(req.Prop("off")), SessionToolSwitches.Names(req.Prop("on")))),
            "Switch tools off or back on for one session, from its next model call: { sessionId, off?: string[], on?: string[] } → like agent.tools");

        context.Logger.LogInformation("Agent runtime started ({Count} recent agents)", runtime.List().Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_runtime is not null) await _runtime.StopAsync(ct).ConfigureAwait(false);
    }

    internal static DeliveryMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "steer" => DeliveryMode.Steer,
        "queue" or "followup" or "follow-up" => DeliveryMode.Queue,
        _ => DeliveryMode.Auto,
    };

    private static List<ImagePart>? ParseImages(JsonElement? images)
    {
        if (images is not { ValueKind: JsonValueKind.Array } arr) return null;
        var list = new List<ImagePart>();
        foreach (var img in arr.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object) continue;
            var data = img.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (string.IsNullOrEmpty(data)) continue;
            var mediaType = img.TryGetProperty("mediaType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            // accept data URLs too
            if (data.StartsWith("data:", StringComparison.Ordinal) && data.IndexOf(";base64,", StringComparison.Ordinal) is var idx and > 5)
            {
                mediaType ??= data[5..idx];
                data = data[(idx + 8)..];
            }
            list.Add(new ImagePart { MediaType = mediaType ?? "image/png", Data = data });
        }
        return list.Count == 0 ? null : list;
    }
}
